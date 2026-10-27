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
/// Finding #1 — the scan path (<c>MergedStreamAsync</c>, backing <c>GetAllItemsAsync</c> and
/// <c>GetRangeAsync</c>) must apply tombstones with the same seqno-aware, newest-wins semantics
/// as the point-lookup path. A flat tombstone-membership set drops a live re-insert whose seqno
/// is newer than an older slot/segment tombstone for the same key (DDIA 2e p. 121 — a later
/// write shadows an earlier tombstone).
/// </summary>
public sealed class LsmTombstoneScanResurrectionTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"LsmTombScan_{Guid.NewGuid():N}");

    public LsmTombstoneScanResurrectionTests() => Directory.CreateDirectory(_dir);

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
    public async Task GetAllItems_DeleteThenReinsert_DoesNotDropLiveValue()
    {
        using var engine = NewEngine();

        await engine.SetAsync(K(1), V("v1"));
        await engine.FlushAsync();        // segment 0 holds {1 : v1} (seqno 1)
        await engine.RemoveAsync(K(1));   // tombstone seqno 2 in the active
        await engine.FlushAsync();        // tombstone-only flush attaches tombstone 1@2 to segment 0
        await engine.SetAsync(K(1), V("v3")); // active, seqno 3 — newest write wins

        // Control: the point-lookup path already gets this right.
        var (val, found) = await engine.TryGetValueAsync(K(1));
        found.Should().BeTrue();
        val.Value.Should().Be("v3");

        // The scan path must agree with the point lookup.
        var all = (await engine.GetAllItemsAsync()).ToList();
        all.Should().ContainSingle(p => p.Key.Value == 1)
            .Which.Value.Value.Should().Be("v3");

        var range = new List<(SerializableWrapper<int> Key, SerializableWrapper<string> Value)>();
        await foreach (var item in engine.GetRangeAsync(K(0), K(10)))
        {
            range.Add(item);
        }
        range.Should().ContainSingle(p => p.Key.Value == 1)
            .Which.Value.Value.Should().Be("v3");
    }

    [Fact]
    public async Task GetAllItems_DeleteWithoutReinsert_StillOmitsKey()
    {
        // Guard against over-correction: a genuine delete (tombstone is the newest write)
        // must still suppress the key on the scan path.
        using var engine = NewEngine();

        await engine.SetAsync(K(1), V("v1"));
        await engine.FlushAsync();
        await engine.RemoveAsync(K(1));   // tombstone is the newest write for key 1
        await engine.FlushAsync();

        var (_, found) = await engine.TryGetValueAsync(K(1));
        found.Should().BeFalse();

        var all = (await engine.GetAllItemsAsync()).ToList();
        all.Should().NotContain(p => p.Key.Value == 1);
    }

    [Fact]
    public async Task GetAllItems_ReinsertThenDelete_SuppressesKey()
    {
        // The reverse of the resurrection case: re-insert, then delete again. The newest
        // write is the tombstone (highest seqno), so the key must be suppressed.
        using var engine = NewEngine();

        await engine.SetAsync(K(1), V("v1"));
        await engine.FlushAsync();
        await engine.SetAsync(K(1), V("v2")); // active, seqno 2
        await engine.RemoveAsync(K(1));        // active tombstone seqno 3 — newest

        var (_, found) = await engine.TryGetValueAsync(K(1));
        found.Should().BeFalse();

        var all = (await engine.GetAllItemsAsync()).ToList();
        all.Should().NotContain(p => p.Key.Value == 1);
    }
}
