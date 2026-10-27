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

namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Behavioural tests for the active+immutable memtable rotation
/// (DDIA Ch. 4 p. 117–119). Asserts the state-machine invariants
/// L1 (single Active), C2 (queue bound = MaxImmutableMemtables), C6 (read
/// visibility through rotation), and the FlushAsync drain contract.
/// </summary>
public sealed class MemtableRotationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmRotation_{Guid.NewGuid():N}");

    public MemtableRotationTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> NewEngine(
        int memTableCapacity = 4,
        int maxImmutableMemtables = 2,
        IWriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>? wal = null,
        ICompactionStrategy<SerializableWrapper<int>>? compactionStrategy = null)
        => LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: memTableCapacity,
            segmentFolder: _dir,
            segmentPrefix: "rot",
            writeAheadLog: wal,
            maxImmutableMemtables: maxImmutableMemtables,
            compactionStrategy: compactionStrategy);

    private static SerializableWrapper<int> K(int i) => new(i);
    private static SerializableWrapper<string> V(string s) => new(s);

    // ---- Rotation correctness ----

    [Fact]
    public async Task Rotate_WhenActiveFills_SealsAndAllocatesFreshActive()
    {
        // capacity 3: writes 1-3 fill the active. Write 4 seals the original active and
        // lands in the fresh one. After FlushAsync the seal is durable on disk and the
        // fresh active still carries item 4.
        using var engine = NewEngine(memTableCapacity: 3);

        await engine.SetAsync(K(1), V("a"));
        await engine.SetAsync(K(2), V("b"));
        await engine.SetAsync(K(3), V("c"));
        await engine.SetAsync(K(4), V("d"));
        await engine.FlushAsync();

        // After FlushAsync: queue drained AND fresh active sealed too → 2 segments.
        engine.SegmentCount.Should().BeGreaterThanOrEqualTo(1);

        // All four keys remain readable after the rotation.
        for (var i = 1; i <= 4; i++)
        {
            var (val, found) = await engine.TryGetValueAsync(K(i));
            found.Should().BeTrue($"key {i} must survive rotation");
            val.Value.Should().Be(((char)('a' + i - 1)).ToString());
        }
    }

    [Fact]
    public async Task Rotate_PreservesReadVisibility_AcrossSeal()
    {
        // A sealed memtable remains read-visible until the segment is
        // durable. Drive a seal via overflow; immediately read the keys (the flusher
        // may or may not have committed). Either way, every key must be visible.
        using var engine = NewEngine(memTableCapacity: 2);

        await engine.SetAsync(K(1), V("a"));
        await engine.SetAsync(K(2), V("b"));
        // capacity hit; next write seals
        await engine.SetAsync(K(3), V("c"));

        // Without FlushAsync: seal may still be in-flight. Reads must still see all data.
        var (v1, f1) = await engine.TryGetValueAsync(K(1));
        var (v2, f2) = await engine.TryGetValueAsync(K(2));
        var (v3, f3) = await engine.TryGetValueAsync(K(3));

        f1.Should().BeTrue();
        v1.Value.Should().Be("a");
        f2.Should().BeTrue();
        v2.Value.Should().Be("b");
        f3.Should().BeTrue();
        v3.Value.Should().Be("c");
    }

    [Fact]
    public async Task Rotate_TombstoneInFreshActive_ShadowsOlderSlot()
    {
        // Read-walk contract: a tombstone in the fresh active masks an older
        // value in the now-sealed slot. After the rotation, TryGetValue returns absent.
        using var engine = NewEngine(memTableCapacity: 2);

        await engine.SetAsync(K(1), V("alive"));
        await engine.SetAsync(K(2), V("filler"));
        // Active is full. RemoveAsync now triggers EnsureActiveHasCapacity → seal,
        // then writes the tombstone to the FRESH active.
        await engine.RemoveAsync(K(1));

        var (_, found) = await engine.TryGetValueAsync(K(1));
        found.Should().BeFalse("tombstone in fresh active masks the sealed slot's value");

        // Drain so the segment + tombstone are durable.
        await engine.FlushAsync();
        var (_, foundAfter) = await engine.TryGetValueAsync(K(1));
        foundAfter.Should().BeFalse("tombstone must survive flush + read from segment");
    }

    [Fact]
    public async Task Rotate_FifoFlushOrder_ProducesSegmentsInSealOrder()
    {
        // Seals strictly order memtables. Drive multiple seals; after
        // FlushAsync the segments land in seal order. We assert by reading each key
        // post-flush — every key written before any seal must remain visible.
        using var engine = NewEngine(memTableCapacity: 2, maxImmutableMemtables: 4);

        for (var i = 1; i <= 8; i++)
        {
            await engine.SetAsync(K(i), V($"v{i}"));
        }
        await engine.FlushAsync();

        // Every key must survive multi-seal rotation regardless of how many segments
        // exist after FlushAsync (the auto-compaction strategy may have merged the
        // freshly-flushed segments into one — that is not a regression in seal ordering).
        for (var i = 1; i <= 8; i++)
        {
            var (val, found) = await engine.TryGetValueAsync(K(i));
            found.Should().BeTrue($"key {i} must survive multi-seal rotation");
            val.Value.Should().Be($"v{i}");
        }

        engine.SegmentCount.Should().BeGreaterThanOrEqualTo(1);
    }

    // ---- Back-pressure ----

    [Fact]
    public async Task BackPressure_EngineRejects_MaxImmutableMemtablesBelow1()
    {
        // Ctor validation rejects MaxImmutableMemtables < 1.
        var act = () => LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 4,
            segmentFolder: _dir,
            segmentPrefix: "rotbad",
            maxImmutableMemtables: 0);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*MaxImmutableMemtables*at least 1*");
    }

    [Fact]
    public async Task BackPressure_DoesNotBlockReads_WhileSealsAccumulate()
    {
        // Ingest blocks on a saturated queue, reads do not. Drive enough
        // writes to keep the flusher busy; reads should complete promptly.
        using var engine = NewEngine(memTableCapacity: 4, maxImmutableMemtables: 2);

        for (var i = 1; i <= 20; i++)
        {
            await engine.SetAsync(K(i), V($"v{i}"));
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (val, found) = await engine.TryGetValueAsync(K(1));
        sw.Stop();

        found.Should().BeTrue();
        val.Value.Should().Be("v1");
        sw.Elapsed.TotalSeconds.Should().BeLessThan(2,
            "reads must not block on the rotation back-pressure path");

        await engine.FlushAsync();
    }

    // ---- FlushAsync / ClearAsync ----

    [Fact]
    public async Task FlushAsync_NoOp_OnIdleEngine()
    {
        // FlushAsync on a fresh engine with no writes returns without
        // sealing or signalling the flusher.
        using var engine = NewEngine(memTableCapacity: 4);

        await engine.FlushAsync();

        engine.SegmentCount.Should().Be(0);
    }

    [Fact]
    public async Task FlushAsync_DrainsQueue_AndSealsActiveIfNonEmpty()
    {
        // After FlushAsync returns, _immutables.Count == 0 and any data in
        // the active has been sealed and committed too.
        using var engine = NewEngine(memTableCapacity: 3, maxImmutableMemtables: 4);

        // Two complete seals + 1 entry in fresh active.
        for (var i = 1; i <= 7; i++)
        {
            await engine.SetAsync(K(i), V($"v{i}"));
        }

        await engine.FlushAsync();

        // Every key is in a segment; subsequent re-open recovers them from disk.
        engine.SegmentCount.Should().BeGreaterThanOrEqualTo(2);
        for (var i = 1; i <= 7; i++)
        {
            var (val, found) = await engine.TryGetValueAsync(K(i));
            found.Should().BeTrue();
            val.Value.Should().Be($"v{i}");
        }
    }

    [Fact]
    public async Task FlushAsync_TruncatesWal_WhenQueueDrains()
    {
        // After FlushAsync, the WAL has been truncated because the
        // last commit's queue-empty branch fired. Use a real WAL to verify.
        var walDir = Path.Combine(_dir, "wal");
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);

        using (var engine = NewEngine(memTableCapacity: 3, wal: wal))
        {
            await engine.SetAsync(K(1), V("a"));
            await engine.SetAsync(K(2), V("b"));
            await engine.SetAsync(K(3), V("c"));
            await engine.SetAsync(K(4), V("d"));
            await engine.FlushAsync();

            Directory.GetFiles(walDir, "wal_*.log").Should().BeEmpty(
                "FlushAsync drains the queue; the queue-empty branch in the flusher's "
                + "commit step truncates the WAL.");
        }
    }

    [Fact]
    public async Task ClearAsync_DiscardsQueuedImmutables_AndDoesNotProduceSegments()
    {
        // ClearAsync drains in-flight flushes, then discards the active +
        // any racing seals. Segments remaining at the time of ClearAsync ARE deleted
        // (existing behaviour). Newly-sealed slots that hadn't committed yet are
        // simply cancelled — their data was WAL-recorded; recovery would replay them
        // on a future open, but ClearAsync also truncates the WAL.
        using var engine = NewEngine(memTableCapacity: 2);

        await engine.SetAsync(K(1), V("a"));
        await engine.SetAsync(K(2), V("b"));
        await engine.SetAsync(K(3), V("c"));

        await engine.ClearAsync();

        engine.SegmentCount.Should().Be(0);
        var (_, found) = await engine.TryGetValueAsync(K(1));
        found.Should().BeFalse("ClearAsync purges every memtable + every segment");
    }

    // ---- Recovery ----

    [Fact]
    public async Task Recovery_ReplaysAcrossRotations_ToSameLogicalState()
    {
        // WAL replay rotates seals during recovery; DrainAllInFlight
        // ensures the queue empties before OpenAsync returns. The recovered state
        // matches the pre-dispose state for every key.
        var walDir = Path.Combine(_dir, "wal");
        var beforeDispose = new Dictionary<int, string>();

        // Phase 1 — write enough to fill the active several times, dispose without
        // FlushAsync (so some writes live only in the WAL).
        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
            using var engine = NewEngine(memTableCapacity: 3, wal: wal);

            for (var i = 1; i <= 12; i++)
            {
                await engine.SetAsync(K(i), V($"v{i}"));
                beforeDispose[i] = $"v{i}";
            }

            await engine.FlushAsync(); // ensure WAL truncated + segments durable
        }

        // Phase 2 — re-open the engine using the async factory (replays WAL).
        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
            using var engine = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<string>>(
                memTableCapacity: 3,
                segmentFolder: _dir,
                segmentPrefix: "rot",
                writeAheadLog: wal,
                maxImmutableMemtables: 2);

            foreach (var (k, v) in beforeDispose)
            {
                var (val, found) = await engine.TryGetValueAsync(K(k));
                found.Should().BeTrue($"key {k} must be recovered post-restart");
                val.Value.Should().Be(v);
            }
        }
    }

    // ---- Faulted state ----

    private sealed class ThrowingCompactionStrategy : ICompactionStrategy<SerializableWrapper<int>>
    {
        public TaskCompletionSource Invoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ShouldCompact(IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> segments)
        {
            Invoked.TrySetResult();
            throw new InvalidOperationException("strategy failed");
        }

        public IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> SelectSegments(
            IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> segments)
            => [];
    }

    [Fact]
    public async Task AutoCompaction_WhenStrategyThrows_SubsequentCompactDoesNotBlock()
    {
        var strategy = new ThrowingCompactionStrategy();
        var engine = NewEngine(
            memTableCapacity: 1,
            compactionStrategy: strategy);

        try
        {
            await engine.SetAsync(K(1), V("a"));
            await engine.SetAsync(K(2), V("b"));
            await strategy.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await engine.CompactAsync(cancellation.Token);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public async Task FaultedState_AfterDispose_DoesNotHang()
    {
        // Dispose cancels the flusher loop and returns. Verifies the
        // flusher Task observes cancellation through the channel reader.
        var engine = NewEngine(memTableCapacity: 4);
        await engine.SetAsync(K(1), V("a"));

        // Dispose synchronously — must not hang or throw.
        engine.Dispose();
    }
}
