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
namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Background compaction (DDIA 2e p. 121: "While the merge is going on, we can still continue
/// to serve reads by using the input segments of the merge"): the O(dataset) merge + segment
/// write must run WITHOUT the engine lock — only the pick and the revalidate/commit steps
/// hold it, mirroring the flusher's 3-step protocol. These tests park a compaction inside its
/// merge window (CompactionMergeHook) and prove reads, writes, flushes, and Clear all proceed
/// or interact correctly with the in-flight merge.
/// </summary>
public sealed class LsmBackgroundCompactionTests : IDisposable
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmBgCompact_{Guid.NewGuid():N}");

    public LsmBackgroundCompactionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static SerializableWrapper<int> K(int i) => new(i);
    private static SerializableWrapper<string> V(string s) => new(s);

    private LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> NewEngine()
        => LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100, segmentFolder: _dir, segmentPrefix: "seg");

    /// <summary>Writes two on-disk segments (keys 1..4 and 5..8) so a compaction has work.</summary>
    private static async Task SeedTwoSegmentsAsync(
        LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> engine)
    {
        for (var i = 1; i <= 4; i++)
        {
            await engine.SetAsync(K(i), V($"v{i}"));
        }

        await engine.FlushAsync();
        for (var i = 5; i <= 8; i++)
        {
            await engine.SetAsync(K(i), V($"v{i}"));
        }

        await engine.FlushAsync();
        engine.SegmentCount.Should().BeGreaterThanOrEqualTo(2, "the compaction needs at least two segments");
    }

    private static (TaskCompletionSource Parked, TaskCompletionSource Release) ParkOneMerge(
        LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> engine)
    {
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fired = 0;
        engine.CompactionMergeHook = async () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
            {
                parked.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }
        };
        return (parked, release);
    }

    [Fact]
    public async Task Reads_AreServed_WhileCompactionMergeIsInFlight()
    {
        using var engine = NewEngine();
        await SeedTwoSegmentsAsync(engine);
        var (parked, release) = ParkOneMerge(engine);

        var compaction = engine.CompactAsync();
        await parked.Task.WaitAsync(s_timeout);

        try
        {
            // AC-1: point read, range read, and full scan all complete against the input
            // segments while the merge is parked.
            var read = engine.TryGetValueAsync(K(3));
            (await read.WaitAsync(s_timeout)).Found.Should().BeTrue("reads must be served from the input segments during the merge");

            var all = engine.GetAllItemsAsync();
            (await all.WaitAsync(s_timeout)).Should().HaveCount(8);

            var range = new List<int>();
            await foreach (var kv in engine.GetRangeAsync(K(2), K(6)).WithCancellation(new CancellationTokenSource(s_timeout).Token))
            {
                range.Add(kv.Key.Value);
            }

            range.Should().Equal(2, 3, 4, 5, 6);
        }
        finally
        {
            release.TrySetResult();
        }

        await compaction.WaitAsync(s_timeout);

        // Exactness after the merge completes.
        (await engine.GetAllItemsAsync()).Should().HaveCount(8);
    }

    [Fact]
    public async Task WritesAndFlushes_Proceed_WhileCompactionMergeIsInFlight()
    {
        using var engine = NewEngine();
        await SeedTwoSegmentsAsync(engine);
        var (parked, release) = ParkOneMerge(engine);

        var compaction = engine.CompactAsync();
        await parked.Task.WaitAsync(s_timeout);

        try
        {
            // AC-2: writes and a delete land while the merge is parked.
            await engine.SetAsync(K(100), V("during-merge")).WaitAsync(s_timeout);
            await engine.RemoveAsync(K(1)).WaitAsync(s_timeout);
        }
        finally
        {
            release.TrySetResult();
        }

        await compaction.WaitAsync(s_timeout);

        // AC-3 slice: state written during the merge survives the commit swap.
        var (v, found) = await engine.TryGetValueAsync(K(100));
        found.Should().BeTrue("a write accepted during the merge must survive the compaction commit");
        v.Value.Should().Be("during-merge");
        (await engine.TryGetValueAsync(K(1))).Found.Should().BeFalse("the delete accepted during the merge must survive");
        (await engine.TryGetValueAsync(K(5))).Found.Should().BeTrue("compacted data is intact");
    }

    [Fact]
    public async Task FlusherAppendedSegment_DuringMerge_SurvivesCommitInCorrectOrder()
    {
        using var engine = NewEngine();
        await SeedTwoSegmentsAsync(engine);

        // Overwrite key 2 so last-writer-wins ordering is observable across the swap.
        var (parked, release) = ParkOneMerge(engine);
        var compaction = engine.CompactAsync();
        await parked.Task.WaitAsync(s_timeout);

        try
        {
            await engine.SetAsync(K(2), V("newer")).WaitAsync(s_timeout);
            // Seal + flush a NEW segment while the merge is parked — the flusher appends it
            // to the segment list mid-compaction.
            await engine.FlushAsync().WaitAsync(s_timeout);
        }
        finally
        {
            release.TrySetResult();
        }

        await compaction.WaitAsync(s_timeout);

        var (v2, found2) = await engine.TryGetValueAsync(K(2));
        found2.Should().BeTrue();
        v2.Value.Should().Be("newer", "the flusher-appended segment is newer than the compacted output and must win LWW");
        (await engine.GetAllItemsAsync()).Should().HaveCount(8, "no keys lost across the concurrent swap");
    }

    [Fact]
    public async Task Clear_DuringMerge_AbortsCommit_NoManifestFlip_NoOrphan()
    {
        using var engine = NewEngine();
        await SeedTwoSegmentsAsync(engine);
        var (parked, release) = ParkOneMerge(engine);

        var compaction = engine.CompactAsync();
        await parked.Task.WaitAsync(s_timeout);

        // AC-4: Clear discards every segment while the merge is parked. The release signal
        // MUST be failure-safe: if this WaitAsync times out (the pre-fix code holds the
        // engine lock for the whole merge), an unreleased hook would park the compaction
        // forever and deadlock the engine's Dispose — hanging the entire test host.
        var clearTask = engine.ClearAsync();
        try
        {
            await clearTask.WaitAsync(s_timeout);
        }
        finally
        {
            release.TrySetResult();
        }

        // The compaction must settle without throwing and WITHOUT resurrecting state.
        await compaction.WaitAsync(s_timeout);

        engine.SegmentCount.Should().Be(0, "Clear won the race; the compaction commit must abort");
        (await engine.GetAllItemsAsync()).Should().BeEmpty();

        // No orphan compacted segment file survives (best-effort delete at abort).
        Directory.GetFiles(_dir, "seg_*").Where(f => new FileInfo(f).Length > 0)
            .Should().BeEmpty("the aborted compaction's output must not linger as an orphan");
    }

    [Fact]
    public async Task ConcurrentExplicitCompactions_Serialize_NoDuplicateOutputs()
    {
        using var engine = NewEngine();
        await SeedTwoSegmentsAsync(engine);
        var (parked, release) = ParkOneMerge(engine);

        var first = engine.CompactAsync();
        await parked.Task.WaitAsync(s_timeout);

        Task second;
        try
        {
            // AC-5: a second explicit compaction must serialize behind the first, not interleave.
            second = engine.CompactAsync();
            second.IsCompleted.Should().BeFalse("the second compaction must wait for the single-compactor gate");
        }
        finally
        {
            release.TrySetResult();
        }
        await first.WaitAsync(s_timeout);
        await second.WaitAsync(s_timeout);

        (await engine.GetAllItemsAsync()).Should().HaveCount(8);
        engine.SegmentCount.Should().Be(1, "full compactions merge everything into one segment");
    }
}
