// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License").
//  You may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Infrastructure.Caching;
using Boutquin.Storage.Infrastructure.Cdc;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins three engine contracts that hold when something goes wrong around a flush, and one
/// that holds when a block cache outlives the files it describes:
/// a flush whose post-flush auto-compaction fails or is cancelled still completes;
/// a manifest write that fails during the flush commit leaves the parallel segment lists in
/// lock-step; a CDC adapter refuses a write the engine would refuse before journaling it; and a
/// shared block cache never serves one file's block for another file.
/// </summary>
public sealed class LsmEngineFlushFaultAndCacheIdentityTests : IDisposable
{
    private static readonly TimeSpan s_bound = TimeSpan.FromSeconds(10);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"LsmFlushFaultCacheIdentity_{Guid.NewGuid():N}");

    public LsmEngineFlushFaultAndCacheIdentityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string NewFolder(string tag)
    {
        var folder = Path.Combine(_root, tag + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static SerializableWrapper<int> K(int v) => new(v);

    private static SerializableWrapper<string> V(string s) => new(s);

    private static LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> CompactAtTwo(string folder)
        => LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100,
            segmentFolder: folder,
            segmentPrefix: "seg",
            compactionStrategy: new FullCompactionStrategy<SerializableWrapper<int>>(2));

    // ---------------------------------------------------------------------------------------
    // A flush whose post-flush auto-compaction fails or is cancelled still completes.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_AutoCompactionMergeFails_FaultsWithTheMergeFailureAndDisposeReturns()
    {
        var engine = CompactAtTwo(NewFolder("merge-fail"));
        await engine.SetAsync(K(1), V("a")).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);
        engine.CompactionMergeHook = () => throw new IOException("injected merge failure");

        await engine.SetAsync(K(2), V("b")).ConfigureAwait(true);
        var flush = engine.FlushAsync();
        var flushSettled = await Task.WhenAny(flush, Task.Delay(s_bound)).ConfigureAwait(true) == flush;

        flushSettled.Should().BeTrue("a failed post-flush auto-compaction must complete the flush it belongs to");
        var flushError = await Record.ExceptionAsync(() => flush).ConfigureAwait(true);
        flushError.Should().BeOfType<IOException>().Which.Message.Should().Be("injected merge failure");

        var dispose = Task.Run(engine.Dispose);
        (await Task.WhenAny(dispose, Task.Delay(s_bound)).ConfigureAwait(true) == dispose)
            .Should().BeTrue("disposal must not wait on a flush that can no longer complete");
    }

    [Fact]
    public async Task FlushAsync_CompactionStrategyThrowsAfterTheFlushCommit_FaultsWithThatFailure()
    {
        var strategy = new ThrowingOnSecondSegmentStrategy();
        var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100, segmentFolder: NewFolder("strategy-fail"), segmentPrefix: "seg",
            compactionStrategy: strategy);
        await engine.SetAsync(K(1), V("a")).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);

        await engine.SetAsync(K(2), V("b")).ConfigureAwait(true);
        var flush = engine.FlushAsync();
        var flushSettled = await Task.WhenAny(flush, Task.Delay(s_bound)).ConfigureAwait(true) == flush;

        flushSettled.Should().BeTrue("a failure after the slot left the queue must still complete its flush");
        var flushError = await Record.ExceptionAsync(() => flush).ConfigureAwait(true);
        flushError.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("injected strategy failure");

        var dispose = Task.Run(engine.Dispose);
        (await Task.WhenAny(dispose, Task.Delay(s_bound)).ConfigureAwait(true) == dispose).Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_DuringAutoCompactionMerge_CancelsThePendingFlushAndReturns()
    {
        var engine = CompactAtTwo(NewFolder("merge-dispose"));
        await engine.SetAsync(K(1), V("a")).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);
        var mergeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMerge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.CompactionMergeHook = async () =>
        {
            mergeEntered.TrySetResult();
            await releaseMerge.Task.ConfigureAwait(false);
        };

        await engine.SetAsync(K(2), V("b")).ConfigureAwait(true);
        var flush = engine.FlushAsync();
        await mergeEntered.Task.WaitAsync(s_bound).ConfigureAwait(true);
        var dispose = Task.Run(engine.Dispose);
        await Task.Delay(200).ConfigureAwait(true);
        releaseMerge.TrySetResult();

        (await Task.WhenAny(dispose, Task.Delay(s_bound)).ConfigureAwait(true) == dispose)
            .Should().BeTrue("disposal racing an auto-compaction must not deadlock on the flush it cancels");
        (await Task.WhenAny(flush, Task.Delay(s_bound)).ConfigureAwait(true) == flush).Should().BeTrue();
        var flushError = await Record.ExceptionAsync(() => flush).ConfigureAwait(true);
        flushError.Should().BeAssignableTo<OperationCanceledException>();
    }

    private sealed class ThrowingOnSecondSegmentStrategy : ICompactionStrategy<SerializableWrapper<int>>
    {
        public bool ShouldCompact(IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> segments)
            => segments.Count >= 2 ? throw new InvalidOperationException("injected strategy failure") : false;

        public IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> SelectSegments(
            IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> segments) => segments;
    }

    // ---------------------------------------------------------------------------------------
    // A manifest write that fails during the flush commit leaves the lists in lock-step.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task FlushCommitManifestFailure_SnapshotReadAndCompaction_DoNotIndexPastTheSegmentLists()
    {
        var folder = NewFolder("manifest-fail");
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100, segmentFolder: folder, segmentPrefix: "seg");
        await engine.SetAsync(K(1), V("a")).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);

        var manifestPath = Path.Combine(folder, "CURRENT");
        var savedManifestPath = Path.Combine(folder, "CURRENT.saved");
        engine.FlusherCommitLockHook = _ =>
        {
            // Fail the real manifest replacement after the segment is written, using a directory
            // at the destination instead of OS-specific permissions or a privileged-user assumption.
            File.Move(manifestPath, savedManifestPath);
            Directory.CreateDirectory(manifestPath);
            return Task.CompletedTask;
        };
        await engine.RemoveAsync(K(1)).ConfigureAwait(true);
        await engine.SetAsync(K(2), V("b")).ConfigureAwait(true);
        Exception? flushError;
        try
        {
            flushError = await Record.ExceptionAsync(() => engine.FlushAsync()).ConfigureAwait(true);
        }
        finally
        {
            engine.FlusherCommitLockHook = null;
            Directory.Delete(manifestPath);
            File.Move(savedManifestPath, manifestPath);
        }
        (flushError is IOException or UnauthorizedAccessException).Should().BeTrue(
            "replacing a directory with the manifest file must fail on every supported filesystem");

        var reads = new List<(int Key, bool Found, string? Value)>();
        var snapshotError = await Record.ExceptionAsync(async () =>
        {
            await using var snapshot = await engine.AcquireSnapshotAsync().ConfigureAwait(true);
            foreach (var key in new[] { 99, 2, 1 })
            {
                var (value, found) = await snapshot.TryGetValueAsync(K(key)).ConfigureAwait(true);
                reads.Add((key, found, found ? value.Value : null));
            }
        }).ConfigureAwait(true);

        snapshotError.Should().BeNull("a faulted engine keeps serving reads, and its segment lists stay in lock-step");
        reads.Should().Equal((99, false, null), (2, true, "b"), (1, false, null));

        var compactError = await Record.ExceptionAsync(() => engine.CompactAsync()).ConfigureAwait(true);
        (compactError as ArgumentOutOfRangeException).Should().BeNull(
            "the compaction tombstone filter indexes the same lists by the same positions");
        var (afterCompaction, stillFound) = await engine.TryGetValueAsync(K(2)).ConfigureAwait(true);
        stillFound.Should().BeTrue();
        afterCompaction.Value.Should().Be("b");
    }

    // ---------------------------------------------------------------------------------------
    // The CDC adapter refuses an entry the engine would refuse, before journaling it.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task LsmCdcAdapter_EntryTheEngineRefuses_IsRejectedBeforeTheJournalAndLeavesTheAdapterWritable()
    {
        var inner = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var format = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(inner);
        await using var engine = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
            memTableCapacity: 16, segmentFolder: NewFolder("cdc"), segmentPrefix: "ts", segmentFormat: format);
        var journal = new InMemoryEventLog<ChangeRecord<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>>();
        var adapter = new LsmCdcAdapter<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(engine, journal);
        await adapter.RecoverAsync().ConfigureAwait(true);

        var day = new DateOnly(2024, 1, 1);
        var bar = new MarketDataBar { Date = day, Close = 10m, Open = 10m, High = 10m, Low = 10m, SplitRatio = 1m };
        var mismatchedKey = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), day.AddDays(4));

        var refused = await Record.ExceptionAsync(() => adapter.SetAsync(mismatchedKey, bar).AsTask()).ConfigureAwait(true);
        refused.Should().BeOfType<ArgumentException>();

        var journaled = 0;
        await foreach (var _ in journal.ReadFromAsync(0).ConfigureAwait(true))
        {
            journaled++;
        }

        journaled.Should().Be(0, "an entry the engine refuses must never reach the authoritative journal");

        var goodKey = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), day);
        var position = await adapter.SetAsync(goodKey, bar).ConfigureAwait(true);
        position.Should().Be(0);
        await adapter.RecoverAsync().ConfigureAwait(true);
        var (stored, found) = await engine.TryGetValueAsync(goodKey).ConfigureAwait(true);
        found.Should().BeTrue();
        stored.Should().Be(bar);
    }

    // ---------------------------------------------------------------------------------------
    // A shared block cache never serves one file's block for another file.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SharedBlockCache_TwoEnginesInDifferentFolders_EachReadsItsOwnBlocks()
    {
        var cache = new LruBlockCache(maxBytes: 1024 * 1024);
        using var engineA = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100, segmentFolder: NewFolder("engine-a"), segmentPrefix: "seg",
            segmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(blockCache: cache));
        using var engineB = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100, segmentFolder: NewFolder("engine-b"), segmentPrefix: "seg",
            segmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(blockCache: cache));

        foreach (var (engine, tag) in new[] { (engineA, "A"), (engineB, "B") })
        {
            await engine.SetAsync(K(1), V(tag + "-seg0")).ConfigureAwait(true);
            await engine.FlushAsync().ConfigureAwait(true);
            await engine.SetAsync(K(1), V(tag + "-seg1")).ConfigureAwait(true);
            await engine.FlushAsync().ConfigureAwait(true);
        }

        var (fromA, foundA) = await engineA.TryGetValueAsync(K(1)).ConfigureAwait(true);
        var (fromB, foundB) = await engineB.TryGetValueAsync(K(1)).ConfigureAwait(true);

        foundA.Should().BeTrue();
        fromA.Value.Should().Be("A-seg1");
        foundB.Should().BeTrue();
        fromB.Value.Should().Be("B-seg1", "engine B's segment file must not be answered from engine A's cached block");
    }

    [Fact]
    public async Task SharedBlockCache_ClearedStoreReopened_ReadsThePostReopenSegments()
    {
        var folder = NewFolder("clear-reopen");
        var cache = new LruBlockCache(maxBytes: 1024 * 1024);
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(blockCache: cache);

        using (var before = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100, segmentFolder: folder, segmentPrefix: "seg", segmentFormat: format))
        {
            await before.SetAsync(K(1), V("pre-clear-seg0")).ConfigureAwait(true);
            await before.FlushAsync().ConfigureAwait(true);
            await before.SetAsync(K(1), V("pre-clear-seg1")).ConfigureAwait(true);
            await before.FlushAsync().ConfigureAwait(true);
            var (cleared, _) = await before.TryGetValueAsync(K(1)).ConfigureAwait(true);
            cleared.Value.Should().Be("pre-clear-seg1");
            await before.ClearAsync().ConfigureAwait(true);
        }

        using var after = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100, segmentFolder: folder, segmentPrefix: "seg", segmentFormat: format).ConfigureAwait(true);
        await after.SetAsync(K(1), V("post-reopen-seg0")).ConfigureAwait(true);
        await after.FlushAsync().ConfigureAwait(true);
        await after.SetAsync(K(1), V("post-reopen-seg1")).ConfigureAwait(true);
        await after.FlushAsync().ConfigureAwait(true);

        var (value, found) = await after.TryGetValueAsync(K(1)).ConfigureAwait(true);
        found.Should().BeTrue();
        value.Value.Should().Be("post-reopen-seg1", "a cleared file's cached block must not answer for a new file");
    }
}
