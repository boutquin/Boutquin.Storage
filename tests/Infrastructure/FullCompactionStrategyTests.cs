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

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Unit tests for <see cref="FullCompactionStrategy{TKey}"/> — Phase B3 metadata-driven interface.
/// </summary>
public sealed class FullCompactionStrategyTests
{
    private static SegmentMetadata<int> Seg(int level = 0, long size = 1024, int min = 0, int max = 100) =>
        new(level, MinKey: min, MaxKey: max, SizeBytes: size, Crc32C: 0u);

    private static IReadOnlyList<SegmentMetadata<int>> Segments(int count) =>
        Enumerable.Range(0, count).Select(i => Seg(min: i * 100, max: (i * 100) + 99)).ToList();

    [Fact]
    public void Constructor_ThresholdLessThan2_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FullCompactionStrategy<int>(threshold: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FullCompactionStrategy<int>(threshold: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FullCompactionStrategy<int>(threshold: -5));
    }

    [Fact]
    public void ShouldCompact_BelowThreshold_False()
    {
        var strategy = new FullCompactionStrategy<int>(threshold: 4);
        strategy.ShouldCompact(Segments(3)).Should().BeFalse();
    }

    [Fact]
    public void ShouldCompact_AtThreshold_True()
    {
        var strategy = new FullCompactionStrategy<int>(threshold: 4);
        strategy.ShouldCompact(Segments(4)).Should().BeTrue();
    }

    [Fact]
    public void ShouldCompact_AboveThreshold_True()
    {
        var strategy = new FullCompactionStrategy<int>(threshold: 3);
        strategy.ShouldCompact(Segments(8)).Should().BeTrue();
    }

    [Fact]
    public void SelectSegments_ReturnsAllInputInOrder()
    {
        var strategy = new FullCompactionStrategy<int>(threshold: 2);
        var segments = Segments(5);

        var selected = strategy.SelectSegments(segments);

        selected.Should().Equal(segments);
    }

    [Fact]
    public void ShouldCompact_NullInput_Throws()
    {
        var strategy = new FullCompactionStrategy<int>(threshold: 2);
        Assert.Throws<ArgumentNullException>(() => strategy.ShouldCompact(null!));
    }

    [Fact]
    public void SelectSegments_NullInput_Throws()
    {
        var strategy = new FullCompactionStrategy<int>(threshold: 2);
        Assert.Throws<ArgumentNullException>(() => strategy.SelectSegments(null!));
    }
}
