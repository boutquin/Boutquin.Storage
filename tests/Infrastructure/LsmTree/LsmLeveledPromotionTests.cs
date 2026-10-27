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
/// DDIA 2e Ch. 4 (leveled compaction): "When a level's SSTables combine to exceed a maximum
/// size limit, one or more SSTables from level i are merged into level i + 1." For every
/// level ≥ 1 with an empty next level, the leveled strategy picks exactly ONE source segment
/// — a level-move request. The engine's old "fewer than two picks → no-op" guard silently
/// discarded that request forever: the strategy kept reporting ShouldCompact, the pyramid
/// was structurally capped at L1, and the level-size schedule above L1 was dead code. A
/// single-segment strategy pick must execute as a rewrite at level + 1.
/// </summary>
public sealed class LsmLeveledPromotionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmLeveled_{Guid.NewGuid():N}");

    public LsmLeveledPromotionTests()
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
    /// Emulates the leveled shape with tiny data: two L0 segments merge into L1 (multi-pick),
    /// then the lone L1 segment is picked ALONE for promotion to L2 while no L2 exists — the
    /// exact single-pick request LeveledCompactionStrategy.PickForLevel produces for an
    /// oversized level with an empty next level.
    /// </summary>
    private sealed class PromoteSingleSegmentStrategy : ICompactionStrategy<SerializableWrapper<int>>
    {
        public bool ShouldCompact(IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> segments)
            => SelectSegments(segments).Count > 0;

        public IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> SelectSegments(
            IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> segments)
        {
            var l0 = segments.Where(s => s.Level == 0).ToList();
            if (l0.Count >= 2)
            {
                return l0;
            }

            // Single-pick promotion: one L1 segment, empty L2.
            var l1 = segments.Where(s => s.Level == 1).ToList();
            if (l1.Count == 1 && !segments.Any(s => s.Level >= 2))
            {
                return l1;
            }

            return [];
        }
    }

    private sealed class RewriteOldestStrategy : ICompactionStrategy<SerializableWrapper<int>>
    {
        public bool ShouldCompact(IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> segments) => segments.Count >= 2;

        public IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> SelectSegments(
            IReadOnlyList<SegmentMetadata<SerializableWrapper<int>>> segments)
            => segments.Count >= 2 ? [segments[0]] : [];
    }

    [Fact]
    public async Task RepeatedSingleSegmentRewrites_StayReopenableAtMaximumLevel()
    {
        using (var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100, segmentFolder: _dir, segmentPrefix: "seg", compactionStrategy: new RewriteOldestStrategy()))
        {
            for (var i = 0; i < 67; i++)
            {
                await engine.SetAsync(K(i), V($"v{i}"));
                await engine.FlushAsync();
            }
        }

        var entries = SegmentManifest.ReadEntries(_dir);
        entries.Should().NotBeNull();
        entries!.Max(e => e.Level).Should().Be(64);
        using var reopened = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100, segmentFolder: _dir, segmentPrefix: "seg");
        for (var i = 0; i < 67; i++)
        {
            var (value, found) = await reopened.TryGetValueAsync(K(i));
            found.Should().BeTrue($"key {i} must survive repeated level moves and recovery");
            value.Value.Should().Be($"v{i}");
        }
    }

    [Fact]
    public async Task SingleSegmentStrategyPick_ExecutesAsLevelPromotion()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _dir,
            segmentPrefix: "seg",
            compactionStrategy: new PromoteSingleSegmentStrategy());

        // Two L0 flushes → multi-pick merge lands an L1 segment.
        await engine.SetAsync(K(1), V("a"));
        await engine.SetAsync(K(2), V("b"));
        await engine.FlushAsync();
        await engine.SetAsync(K(3), V("c"));
        await engine.SetAsync(K(4), V("d"));
        await engine.FlushAsync(); // triggers L0 merge → L1

        // One more flush commit re-evaluates the strategy: it now requests the lone L1
        // segment's promotion. The engine must execute it as a rewrite at L2.
        await engine.SetAsync(K(5), V("e"));
        await engine.SetAsync(K(6), V("f"));
        await engine.FlushAsync();

        var entries = SegmentManifest.ReadEntries(_dir);
        entries.Should().NotBeNull();
        entries!.Select(e => e.Level).Should().Contain(2,
            "a single-segment strategy pick is a level-move request (DDIA 2e: merge level i " +
            "into level i + 1) and must not be silently no-oped");

        // Data integrity across the promotions.
        for (var i = 1; i <= 6; i++)
        {
            var (v, found) = await engine.TryGetValueAsync(K(i));
            found.Should().BeTrue($"key {i} must survive the level promotions");
        }
    }
}
