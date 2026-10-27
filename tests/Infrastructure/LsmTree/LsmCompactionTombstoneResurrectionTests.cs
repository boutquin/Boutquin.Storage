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

namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Compaction must apply tombstones with the same seqno-aware, newest-wins semantics as the
/// point-lookup and scan paths. The compaction tombstone filter dropped a key whenever any picked
/// segment carried a strippable tombstone for it, without comparing the surviving merge entry's
/// sequence number — so a re-insert that flushed to a <em>newer</em> segment than an older flushed
/// tombstone was silently dropped during compaction (DDIA 2e p. 121 — a later write shadows an
/// earlier tombstone; p. 124 — a tombstone is only discarded once it shadows every older
/// occurrence, never when a newer live write exists).
/// </summary>
public sealed class LsmCompactionTombstoneResurrectionTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"LsmCompactTomb_{Guid.NewGuid():N}");

    public LsmCompactionTombstoneResurrectionTests() => Directory.CreateDirectory(_dir);

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
            MemTableCapacity: 1000,
            SegmentFolder: _dir,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            CompactionStrategy: null));

    private static SerializableWrapper<int> K(int k) => new(k);
    private static SerializableWrapper<string> V(string v) => new(v);

    [Fact]
    public async Task Compaction_DeleteFlushReinsertFlush_KeepsLiveValue()
    {
        using var engine = NewEngine();

        await engine.SetAsync(K(1), V("v1"));   // segment 0 data: 1@1
        await engine.FlushAsync();
        await engine.RemoveAsync(K(1));         // tombstone 1@2
        await engine.FlushAsync();              // tombstone-only flush attaches 1@2 to segment 0
        await engine.SetAsync(K(1), V("v3"));   // re-insert 1@3 — newest write wins
        await engine.FlushAsync();              // segment 1 data: 1@3

        // Pre-compaction: the seqno-aware point lookup already returns the live re-insert.
        var (before, foundBefore) = await engine.TryGetValueAsync(K(1));
        foundBefore.Should().BeTrue();
        before.Value.Should().Be("v3");

        await engine.CompactAsync();

        // Post-compaction: the live re-insert must survive. The bug dropped it because the
        // tombstone filter stripped key 1 without comparing 1@2 (tombstone) against 1@3 (data).
        var (after, foundAfter) = await engine.TryGetValueAsync(K(1));
        foundAfter.Should().BeTrue();
        after.Value.Should().Be("v3");

        var all = (await engine.GetAllItemsAsync()).ToList();
        all.Should().ContainSingle(p => p.Key.Value == 1)
            .Which.Value.Value.Should().Be("v3");
    }

    [Fact]
    public async Task PointRead_PreservedTombstoneOlderThanReinsert_ReturnsLiveValue()
    {
        // A1 (the point-read sibling of the compaction/scan/snapshot tombstone fixes): when
        // compaction PRESERVES a tombstone (non-strippable because a live snapshot pins a seqno
        // at or below it), the compacted segment carries BOTH the preserved tombstone (1@2) and
        // the newer re-insert (1@3). The non-snapshot point read must return the re-insert. The
        // un-fixed path returned not-found on tombstone membership alone, without comparing the
        // tombstone seqno against the segment's own newer data (inverse of DDIA 2e p. 121 — a
        // later write shadows an earlier tombstone). The snapshot read path already did this; the
        // plain TryGetValueAsync path did not.
        using var engine = NewEngine();

        await engine.SetAsync(K(1), V("v1"));   // 1@1
        await engine.FlushAsync();              // seg0 data 1@1
        await engine.RemoveAsync(K(1));         // tombstone 1@2
        await engine.FlushAsync();              // tombstone attaches to seg0

        // Pin a snapshot at seqno 2 so compaction cannot strip the 1@2 tombstone
        // (stripThis = canStripBottom && tSeqno < minSnapSeqno; 2 < 2 is false → preserved).
        await using var pin = await engine.AcquireSnapshotAsync();

        await engine.SetAsync(K(1), V("v3"));   // re-insert 1@3 — newest write wins
        await engine.FlushAsync();              // seg1 data 1@3

        await engine.CompactAsync();            // one segment now holds data 1@3 + preserved tombstone 1@2

        var (after, found) = await engine.TryGetValueAsync(K(1));
        found.Should().BeTrue();
        after.Value.Should().Be("v3");
    }

    [Fact]
    public async Task Compaction_DeleteFlushReinsertFlush_ThenDeleteWins_OmitsKey()
    {
        // Guard against over-correction: when the newest flushed write is the tombstone, the key
        // must still be removed by compaction.
        using var engine = NewEngine();

        await engine.SetAsync(K(1), V("v1"));   // 1@1
        await engine.FlushAsync();
        await engine.SetAsync(K(1), V("v2"));   // 1@2
        await engine.FlushAsync();
        await engine.RemoveAsync(K(1));         // tombstone 1@3 — newest write
        await engine.FlushAsync();

        await engine.CompactAsync();

        var (_, found) = await engine.TryGetValueAsync(K(1));
        found.Should().BeFalse();

        var all = (await engine.GetAllItemsAsync()).ToList();
        all.Should().NotContain(p => p.Key.Value == 1);
    }
}
