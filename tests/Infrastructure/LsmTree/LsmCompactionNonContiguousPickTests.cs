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
/// Position-based last-writer-wins across segments is sound only while compaction keeps the
/// segment list ordered by per-key write recency. A NON-contiguous pick (e.g. <c>{0, 2}</c>,
/// which size-tiered and leveled strategies routinely produce) breaks it: the compacted output
/// — carrying segment 0's old version of a key — is slotted after the unpicked middle segment
/// that holds the key's NEWEST version, so reads return the old value and a later full
/// compaction durably discards the new one. DDIA 2e Ch. 4: "If the same key appears in more
/// than one input file, keep only the more recent value."
/// </summary>
public sealed class LsmCompactionNonContiguousPickTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmNonContig_{Guid.NewGuid():N}");

    public LsmCompactionNonContiguousPickTests()
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

    private static SerializableWrapper<int> K(int i) => new(i);
    private static SerializableWrapper<string> V(string s) => new(s);

    /// <summary>
    /// Picks segments 0 and 2 (skipping the middle segment) as soon as three segments exist —
    /// the shape size-tiered tier-bucketing produces for sizes like [S, L, S].
    /// </summary>
    private sealed class PickFirstAndThirdStrategy : ICompactionStrategy<SerializableWrapper<int>>
    {
        public bool ShouldCompact(IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> segments)
            => segments.Count == 3;

        public IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> SelectSegments(
            IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> segments)
            => segments.Count == 3 ? [segments[0], segments[2]] : [];
    }

    [Fact]
    public async Task NonContiguousPick_DoesNotShadowNewerValueInUnpickedMiddleSegment()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 4,
            segmentFolder: _dir,
            segmentPrefix: "seg",
            compactionStrategy: new PickFirstAndThirdStrategy());

        // Segment 0: A=old. Segment 1 (unpicked middle): A=new. Segment 2: C.
        await engine.SetAsync(K(1), V("old"));
        await engine.SetAsync(K(2), V("b"));
        await engine.FlushAsync();
        await engine.SetAsync(K(1), V("new"));
        await engine.SetAsync(K(3), V("c"));
        await engine.FlushAsync();
        await engine.SetAsync(K(4), V("d"));
        await engine.FlushAsync(); // third segment lands → auto-compaction picks {0, 2}

        // Point read: key 1's newest version (segment 1, unpicked) must win.
        var (v1, found1) = await engine.TryGetValueAsync(K(1));
        found1.Should().BeTrue();
        v1.Value.Should().Be("new",
            "the unpicked middle segment holds key 1's newest write; a compacted segment built " +
            "from older inputs must never shadow it");

        // Scan path must agree with the point read.
        var all = (await engine.GetAllItemsAsync()).ToDictionary(kv => kv.Key.Value, kv => kv.Value.Value);
        all[1].Should().Be("new", "the merged scan must elect the same winner as the point read");

        // And a subsequent full compaction must not durably discard the newer value.
        await engine.CompactAsync();
        var (v1After, found1After) = await engine.TryGetValueAsync(K(1));
        found1After.Should().BeTrue();
        v1After.Value.Should().Be("new",
            "full compaction after the partial pick must keep the newest version, not the resurrected old one");
    }
}
