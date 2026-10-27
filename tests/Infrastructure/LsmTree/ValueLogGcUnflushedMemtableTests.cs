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
#nullable disable

using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Finding #3 — value-log GC must never reclaim a closed value-log file still referenced by a
/// WiscKey pointer that lives only in an unflushed (active or immutable) memtable. The pointer
/// is placed in the active memtable at write time and does not reach an SSTable until flush, so a
/// GC liveness scan that walks only flushed segments will delete a file the engine still needs
/// (DDIA 2e — WiscKey key/value separation: GC must not free a referenced value).
/// </summary>
public sealed class ValueLogGcUnflushedMemtableTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"VlogGcUnflushed_{Guid.NewGuid():N}");

    public ValueLogGcUnflushedMemtableTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* test-runner cleanup — ignore */ }
        }
    }

    private LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> NewEngine()
        => new(new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 1000, // keep everything in the active memtable — no auto-flush
            SegmentFolder: _dir,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            CompactionStrategy: null,
            // Small rotation size so a few large values roll the value-log file, closing file 0.
            ValueLog: new ValueLogOptions(ValueLogThresholdBytes: 64, SegmentMaxBytes: 220)));

    private static string Payload(int seed) => new((char)('a' + (seed % 26)), 120);

    [Fact]
    public async Task Gc_DoesNotReclaim_FileReferencedByUnflushedMemtable()
    {
        using var engine = NewEngine();

        // Each 120-byte payload exceeds the 64-byte threshold → goes to the value log as a
        // pointer in the active memtable. With SegmentMaxBytes=220, successive writes rotate
        // the value-log file, so file 0 becomes a *closed* (non-current) file whose only
        // reference is the unflushed active memtable.
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>(Payload(1)));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>(Payload(2)));
        await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>(Payload(3)));

        // No FlushAsync — pointers live only in the memtable.
        var result = await engine.CompactValueLogAsync();

        result.FilesReclaimed.Should().Be(
            0, "every closed value-log file is still referenced by the unflushed active memtable");

        var (v1, f1) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
        f1.Should().BeTrue();
        v1.Value.Should().Be(Payload(1));

        var (v2, f2) = await engine.TryGetValueAsync(new SerializableWrapper<int>(2));
        f2.Should().BeTrue();
        v2.Value.Should().Be(Payload(2));
    }

    /// <summary>
    /// A pointer-path Set parked in the ingest-gate lock-release window holds a value-log
    /// pointer that is in NO memtable yet (value-log + WAL appended; apply pending). A GC
    /// round in that window must still count the pointer live — otherwise a concurrent
    /// rotation closes the pointer's file and GC reclaims it, durably destroying an
    /// acknowledged write (DDIA 2e — WiscKey: GC must not free a referenced value).
    /// </summary>
    [Fact]
    public async Task Gc_DoesNotReclaim_FileReferencedByInFlightGateParkedWrite()
    {
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
                MemTableCapacity: 2, // active fills after two writes → third takes the gate path
                SegmentFolder: _dir,
                SegmentPrefix: "seg",
                SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
                CompactionStrategy: null,
                // Every ~141-byte frame rotates: each value-log file holds exactly one frame,
                // so the parked write's file is CLOSED by the next write's rotation.
                ValueLog: new ValueLogOptions(ValueLogThresholdBytes: 64, SegmentMaxBytes: 220)));

        // Fill the active memtable with two pointer-path writes.
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>(Payload(1)));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>(Payload(2)));

        // Park the third pointer-path Set inside the release→gate window: its value-log frame
        // and WAL frame are durable, but the pointer has not been applied to any memtable.
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

        var parkedSet = engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>(Payload(3)));
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        engine.IngestGateWindowHook = null;

        // A fourth write rotates the value log, closing the parked write's file, and seals
        // the active. Then a GC round runs while the parked pointer is still in flight.
        await engine.SetAsync(new SerializableWrapper<int>(4), new SerializableWrapper<string>(Payload(4)));
        var gcResult = await engine.CompactValueLogAsync();

        release.TrySetResult();
        await parkedSet;

        gcResult.FilesReclaimed.Should().Be(
            0, "the parked write's value-log file is referenced by an in-flight acknowledged write");

        var (v3, f3) = await engine.TryGetValueAsync(new SerializableWrapper<int>(3));
        f3.Should().BeTrue("the parked write was acknowledged and must remain readable");
        v3.Value.Should().Be(Payload(3));
    }
}
