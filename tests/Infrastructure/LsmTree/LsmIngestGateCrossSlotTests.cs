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
/// Cross-slot manifestations of the ingest-gate window. The same-slot inversions are guarded
/// (see <see cref="LsmIngestGateApplyOrderTests"/>), but a mutator parked across the gate can
/// also be overtaken by mutations that land in a DIFFERENT slot and then flush to disk. Two
/// invariants must survive that interleaving:
/// (1) point reads and scans must agree — a positional memtable hit must not resurrect a key
///     whose newest mutation is a flushed tombstone with a higher seqno;
/// (2) an acknowledged write must survive restart — the WAL truncation watermark must never
///     cover a frame whose data has not yet been flushed to a segment.
/// </summary>
public sealed class LsmIngestGateCrossSlotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmGateCrossSlot_{Guid.NewGuid():N}");

    public LsmIngestGateCrossSlotTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private static SerializableWrapper<int> K(int i) => new(i);
    private static SerializableWrapper<string> V(string s) => new(s);

    [Fact]
    public async Task PointReadAndScan_MustAgree_WhenSetIsOvertakenAcrossSlotFlush()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _dir,
            segmentPrefix: "seg");

        // Fill the active memtable so the next mutator takes the gate path.
        await engine.SetAsync(K(1), V("a"));
        await engine.SetAsync(K(2), V("b"));

        // Park the Set inside the release→gate window (one-shot hook).
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fired = 0;
        engine.IngestGateWindowHook = async () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
            {
                parked.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }
        };

        // T1: Set(5) parks inside the gate window.
        var setTask = engine.SetAsync(K(5), V("set-old"));
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        engine.IngestGateWindowHook = null;

        // T2: Remove(5) overtakes; its tombstone lands in the successor slot, which is then
        // FLUSHED — the tombstone is now on disk, in no memtable slot.
        await engine.RemoveAsync(K(5));
        await engine.FlushAsync();

        // Resume T1: the overtaken Set applies into the current (fresh) active slot.
        release.TrySetResult();
        await setTask;

        var (_, pointFound) = await engine.TryGetValueAsync(K(5));
        var scanHasKey = (await engine.GetAllItemsAsync()).Any(kv => kv.Key.Equals(K(5)));

        pointFound.Should().Be(scanHasKey,
            "the point-read path and the scan path must give the same verdict for the same key on the same engine state");
    }

    [Fact]
    public async Task AckedWrite_ParkedAcrossSlotFlush_SurvivesReopen()
    {
        // DDIA 2e Ch. 4: flushing a memtable lets you discard "the corresponding part of the
        // log" — only the part whose data reached a segment. A mutator parked across the gate
        // window must not have its WAL frame covered by a flush of OTHER slots' data.
        var walDir = Path.Combine(_dir, "wal");

        // Minimum-size WAL segments (1 MiB) + over-cap filler values: the WAL rotates
        // immediately after an over-cap frame, so the parked write's frame sits in a CLOSED
        // segment by the time the flush-side truncation runs. The value-log threshold is
        // raised so the big fillers stay INLINE in WAL frames instead of detouring to the
        // value log as 12-byte pointers.
        var walOptions = new WriteAheadLogOptions { SegmentMaxBytes = 1L * 1024 * 1024 };
        var bigInlineValues = new Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogOptions(
            ValueLogThresholdBytes: 64 * 1024 * 1024);
        var bigValue = new string('x', 1_200_000);

        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir, walOptions);
            using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
                memTableCapacity: 2, segmentFolder: _dir, segmentPrefix: "seg", writeAheadLog: wal,
                valueLog: bigInlineValues);

            await engine.SetAsync(K(1), V("v1"));
            await engine.SetAsync(K(2), V("v2")); // active now full

            var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fired = 0;
            engine.IngestGateWindowHook = async () =>
            {
                if (Interlocked.Exchange(ref fired, 1) == 0)
                {
                    parked.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                }
            };

            // T1: Set(9) parks inside the gate window.
            var setTask = engine.SetAsync(K(9), V("must-survive"));
            await parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            engine.IngestGateWindowHook = null;

            // T2: overtaking mutations land in the successor slot and flush; the flush-side
            // WAL truncation runs with a watermark covering their (higher) seqnos. Each big
            // filler frame closes its WAL segment, so key 9's frame is in a closed segment.
            await engine.RemoveAsync(K(1));
            await engine.SetAsync(K(7), V(bigValue));
            await engine.SetAsync(K(8), V(bigValue));
            await engine.FlushAsync();

            // Resume T1 — the write completes and is ACKNOWLEDGED.
            release.TrySetResult();
            await setTask;

            // Dispose WITHOUT FlushAsync — simulates a crash. Key 9 exists only in the
            // active memtable + (it must!) the WAL.
        }

        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir, walOptions);
        using var reopened = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2, segmentFolder: _dir, segmentPrefix: "seg", writeAheadLog: wal2,
            valueLog: bigInlineValues);

        var (v9, found9) = await reopened.TryGetValueAsync(K(9));
        found9.Should().BeTrue(
            "key 9 was acknowledged with a fsync'd WAL frame; a flush of OTHER slots must not truncate " +
            "the only durable copy of a write that has not yet reached any segment");
        v9.Value.Should().Be("must-survive");
    }
}
