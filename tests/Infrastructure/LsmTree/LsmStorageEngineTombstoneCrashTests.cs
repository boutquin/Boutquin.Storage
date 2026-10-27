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
/// Phase A1 crash-injection tests for <see cref="LsmStorageEngine{TKey, TValue}"/>.
/// Establishes that a <c>RemoveAsync</c> before flush is durable — the tombstone replays
/// from the WriteAheadLog on the next start, shadowing the older segment's live value.
/// </summary>
public sealed class LsmStorageEngineTombstoneCrashTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmTombstoneCrash_{Guid.NewGuid():N}");

    public LsmStorageEngineTombstoneCrashTests()
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

    // The WAL is handed back alongside the engine because the engine does NOT own its injected
    // WAL — the caller disposes both. Leaking the WAL would hold its exclusive single-writer
    // directory lock, so the next "restart" (a fresh WAL over the same directory) would fail
    // fast at construction instead of recovering.
    private (WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>> Wal,
             LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> Engine)
        NewEngine(int memTableCapacity = 3)
    {
        var walPath = Path.Combine(_dir, "wal.log");
        var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
        var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: memTableCapacity,
            segmentFolder: _dir,
            segmentPrefix: "seg",
            writeAheadLog: wal);
        return (wal, engine);
    }

    private async Task<(WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>> Wal,
                        LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> Engine)>
        NewEngineAsync(int memTableCapacity = 3)
    {
        var walPath = Path.Combine(_dir, "wal.log");
        var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
        var engine = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: memTableCapacity,
            segmentFolder: _dir,
            segmentPrefix: "seg",
            writeAheadLog: wal);
        return (wal, engine);
    }

    private static SerializableWrapper<int> K(int i) => new(i);
    private static SerializableWrapper<string> V(string s) => new(s);

    /// <summary>
    /// Reproduces the DDIA 2e p. 121 tombstone-durability requirement. Without Phase A1's
    /// AppendDeleteAsync, the RemoveAsync lands only in the in-memory tombstone set;
    /// the next start rebuilds from the WAL (which had no record of the delete) and the
    /// value from the older segment resurrects.
    /// </summary>
    [Fact]
    public async Task RemoveBeforeFlush_IsDurable_AcrossRestart()
    {
        // Arrange: seed the engine with a key that reaches disk (via flush).
        var (wal1, engine1) = NewEngine(memTableCapacity: 2);
        using (wal1)
        using (engine1)
        {
            await engine1.SetAsync(K(1), V("alive"));
            await engine1.SetAsync(K(2), V("bystander"));
            await engine1.SetAsync(K(3), V("third")); // triggers flush — 'alive' is now on disk.
            // Force a fresh MemTable by flushing remaining entries so any tombstone written
            // AFTER the next restart operates over a segment-backed value.
            await engine1.FlushAsync();
        }

        var (wal2, engine2) = await NewEngineAsync(memTableCapacity: 2);
        using (wal2)
        using (engine2)
        {
            // Act (pre-crash): delete key 1. Tombstone lands in the WAL but NOT in a segment.
            await engine2.RemoveAsync(K(1));

            // Sanity check: engine sees the delete in this session.
            var (_, found) = await engine2.TryGetValueAsync(K(1));
            found.Should().BeFalse();
        }

        // Simulate crash by abandoning the engine mid-flight (we skipped FlushAsync, so the
        // tombstone is MemTable-only from the perspective of segment storage).

        // Act (post-restart): re-open a fresh engine over the same directory.
        var restartedPair = await NewEngineAsync(memTableCapacity: 2);
        using var wal3 = restartedPair.Wal;
        using var restarted = restartedPair.Engine;

        var (value, resurrectedFound) = await restarted.TryGetValueAsync(K(1));

        // Assert: the delete survived the restart. Without A1 this would resurrect "alive".
        resurrectedFound.Should().BeFalse(
            "tombstones must be WAL-durable — a crash between RemoveAsync and the next flush " +
            "must not resurrect the deleted value from an older segment (DDIA 2e p. 121).");
        value.Should().BeNull();
    }

    /// <summary>
    /// Replaying a Set, then Delete, then Set for the same key collapses to the trailing Set —
    /// the tombstone is cleared by the later live write. Validates the WAL replay
    /// invariant that "Set following Delete for the same key at higher position collapses
    /// to Set in the recovered MemTable."
    /// </summary>
    [Fact]
    public async Task ReplaySetDeleteSet_ReturnsLastSet()
    {
        var (wal1, engine1) = NewEngine(memTableCapacity: 100);
        using (wal1)
        using (engine1)
        {
            await engine1.SetAsync(K(5), V("first"));
            await engine1.RemoveAsync(K(5));
            await engine1.SetAsync(K(5), V("second"));
            // No flush — all three operations remain in the WAL only.
        }

        var restartedPair = await NewEngineAsync(memTableCapacity: 100);
        using var wal2 = restartedPair.Wal;
        using var restarted = restartedPair.Engine;
        var (value, found) = await restarted.TryGetValueAsync(K(5));
        found.Should().BeTrue();
        value.Value.Should().Be("second");
    }
}
