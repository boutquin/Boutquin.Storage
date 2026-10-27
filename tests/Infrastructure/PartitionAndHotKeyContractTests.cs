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
/// Keys that are equal under <see cref="IComparable{T}"/> are the same logical key, so partition
/// routing and hot-key salting must treat them identically even when their string forms differ
/// (a <see cref="DateTimeOffset"/> instant in two offsets, <c>1.0m</c> versus <c>1.00m</c>,
/// <c>0.0</c> versus <c>-0.0</c>). Routing a write and a read of one logical key to different
/// shards is silent data loss.
/// </summary>
public sealed class PartitionAndHotKeyContractTests
{
    private const int PartitionCount = 64;

    [Fact]
    public void HashPartitioner_DateTimeOffsetEqualInstantsInDifferentOffsets_ShareAPartition()
    {
        var partitioner = new HashPartitioner<DateTimeOffset>(PartitionCount);
        var random = new Random(4101);

        for (var i = 0; i < 500; i++)
        {
            var utc = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(random.Next(0, 5_000_000));
            var shifted = utc.ToOffset(TimeSpan.FromMinutes(random.Next(-14 * 4, 14 * 4) * 15));

            shifted.CompareTo(utc).Should().Be(0);
            partitioner.GetPartition(shifted).Should().Be(partitioner.GetPartition(utc), $"{utc:O} and {shifted:O} are one instant");
        }
    }

    [Fact]
    public void HashPartitioner_DecimalsDifferingOnlyInScale_ShareAPartition()
    {
        var partitioner = new HashPartitioner<decimal>(PartitionCount);

        foreach (var (a, b) in new[] { (1.0m, 1.00m), (0m, 0.000m), (-2.50m, -2.5m), (100m, 100.000m), (1234.5600m, 1234.56m) })
        {
            a.CompareTo(b).Should().Be(0);
            partitioner.GetPartition(a).Should().Be(partitioner.GetPartition(b), $"{a} and {b} are the same decimal value");
        }
    }

    [Fact]
    public void HashPartitioner_PositiveAndNegativeZero_ShareAPartition()
    {
        new HashPartitioner<double>(PartitionCount).GetPartition(-0.0).Should().Be(new HashPartitioner<double>(PartitionCount).GetPartition(0.0));
        new HashPartitioner<float>(PartitionCount).GetPartition(-0.0f).Should().Be(new HashPartitioner<float>(PartitionCount).GetPartition(0.0f));
    }

    [Fact]
    public void HashPartitioner_DistinctValues_StillSpreadAcrossPartitions()
    {
        // Negative pairing: canonicalisation must not collapse distinct keys.
        var partitioner = new HashPartitioner<decimal>(PartitionCount);
        var used = Enumerable.Range(0, 2000).Select(i => partitioner.GetPartition(i + 0.5m)).Distinct().Count();

        used.Should().BeGreaterThan(PartitionCount / 2);
        partitioner.GetPartition(1.5m).Should().Be(partitioner.GetPartition(1.50m));

        var instants = new HashPartitioner<DateTimeOffset>(PartitionCount);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Enumerable.Range(0, 2000).Select(i => instants.GetPartition(start.AddMinutes(i))).Distinct().Count()
            .Should().BeGreaterThan(PartitionCount / 2);
    }

    [Fact]
    public void SaltedHotKeyStrategy_EqualDateTimeOffsetKeys_ShareReadAndWriteKeys()
    {
        var strategy = new SaltedHotKeyStrategy<DateTimeOffset>();
        var utc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var shifted = utc.ToOffset(TimeSpan.FromHours(1));

        var written = strategy.GetWriteKey(utc, 4, 17UL);

        strategy.GetReadKeys(shifted, 4).Should().Contain(written);
        strategy.GetReadKeys(shifted, 4).Should().Equal(strategy.GetReadKeys(utc, 4));
    }

    [Fact]
    public void SaltedHotKeyStrategy_EqualDecimalAndZeroKeys_ShareReadKeys()
    {
        var decimals = new SaltedHotKeyStrategy<decimal>();
        decimals.GetReadKeys(1.0m, 3).Should().Equal(decimals.GetReadKeys(1.00m, 3));

        var doubles = new SaltedHotKeyStrategy<double>();
        doubles.GetReadKeys(-0.0, 3).Should().Equal(doubles.GetReadKeys(0.0, 3));
    }

    [Fact]
    public void SaltedHotKeyStrategy_DistinctKeys_GetDisjointSalts()
    {
        var strategy = new SaltedHotKeyStrategy<DateTimeOffset>();
        var a = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        strategy.GetReadKeys(a, 4).Should().NotIntersectWith(strategy.GetReadKeys(a.AddTicks(1), 4));
    }

    [Fact]
    public void ShardRouter_EqualKeysWithDifferentFormatting_RouteToTheSameNode()
    {
        const int shards = 16;
        var snapshot = new ShardMapSnapshot(
            1,
            Enumerable.Range(0, shards).Select(i => new ShardAssignment($"shard-{i:D4}", $"node-{i}")).ToArray());
        var router = new ShardRouter<DateTimeOffset>(
            new HashPartitioner<DateTimeOffset>(shards),
            new ShardMap(new FixedAssignmentStore(snapshot), snapshot));
        var start = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);

        for (var i = 0; i < 200; i++)
        {
            var utc = start.AddMinutes(i * 37);
            var shifted = utc.ToOffset(TimeSpan.FromHours((i % 27) - 13));

            router.Route(shifted).Should().BeEquivalentTo(router.Route(utc), $"{shifted:O} and {utc:O} are one instant");
        }
    }

    private sealed class FixedAssignmentStore(ShardMapSnapshot snapshot) : IShardAssignmentStore
    {
        public Task<ShardMapSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ShardMapSnapshot?>(snapshot);

        public Task<bool> TrySaveAsync(ShardMapSnapshot next, long expectedVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
