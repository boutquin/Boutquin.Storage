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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Exactness coverage for the point-read range-skip optimization (DDIA 2e Ch. 4 — data skipping via
/// per-segment min/max). A point read skips the data probe of any on-disk segment whose footer
/// <c>[min, max]</c> key range provably excludes the key. The optimization must be behaviour-
/// preserving: every present key still resolves to its correct value, every gap key still resolves to
/// not-found, and tombstone masking is unaffected. These tests build segments with disjoint key
/// ranges so the skip path is exercised on most probes, and assert exact results.
/// </summary>
public sealed class LsmRangeSkipReadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmRangeSkip_{Guid.NewGuid():N}");

    public LsmRangeSkipReadTests() => Directory.CreateDirectory(_dir);

    private static SerializableWrapper<int> K(int i) => new(i);

    private static SerializableWrapper<string> V(string s) => new(s);

    [Fact]
    public async Task PointRead_AcrossDisjointRangeSegments_ReturnsExactResults()
    {
        // Three on-disk segments with disjoint key ranges: A=[0..9], B=[100..109], C=[200..209].
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 1000, segmentFolder: _dir, segmentPrefix: "seg");

        foreach (var (lo, tag) in new[] { (0, "A"), (100, "B"), (200, "C") })
        {
            for (var i = lo; i < lo + 10; i++)
            {
                await engine.SetAsync(K(i), V($"{tag}{i}"));
            }

            await engine.FlushAsync(); // seal this range into its own segment
        }

        engine.SegmentCount.Should().BeGreaterThanOrEqualTo(3);

        // Every present key resolves to its correct per-range value (a broken skip that dropped
        // in-range keys would fail here).
        foreach (var (lo, tag) in new[] { (0, "A"), (100, "B"), (200, "C") })
        {
            for (var i = lo; i < lo + 10; i++)
            {
                var (val, found) = await engine.TryGetValueAsync(K(i));
                found.Should().BeTrue($"key {i} was written to segment {tag}");
                val.Value.Should().Be($"{tag}{i}");
            }
        }

        // Gap keys — below, between, and above every segment range — resolve to not-found.
        foreach (var gap in new[] { -5, 10, 50, 99, 110, 150, 199, 210, 5000 })
        {
            var (_, found) = await engine.TryGetValueAsync(K(gap));
            found.Should().BeFalse($"key {gap} is outside every segment's key range");
        }
    }

    [Fact]
    public async Task PointRead_RangeSkip_DoesNotBypassTombstoneMasking()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 1000, segmentFolder: _dir, segmentPrefix: "seg");

        // Older segment holds key 5 = "v".
        await engine.SetAsync(K(5), V("v"));
        await engine.FlushAsync();

        // A later delete tombstones key 5 and is flushed. Key 5 is inside the older segment's range,
        // so that segment is NOT range-skipped; the tombstone must still mask the older value.
        await engine.RemoveAsync(K(5));
        await engine.FlushAsync();

        var (_, found) = await engine.TryGetValueAsync(K(5));
        found.Should().BeFalse("the tombstone must mask the older value even with range-skip enabled");

        // A neighbouring live key in the same original range is unaffected.
        await engine.SetAsync(K(6), V("six"));
        var (v6, found6) = await engine.TryGetValueAsync(K(6));
        found6.Should().BeTrue();
        v6.Value.Should().Be("six");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the per-test segment directory.
        }
    }
}
