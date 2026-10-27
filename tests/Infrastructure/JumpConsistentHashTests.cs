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

public sealed class JumpConsistentHashTests
{
    [Fact]
    public void JumpConsistentHash_IsPartOfThePartitioningSurface()
    {
        var type = typeof(HashPartitioner<>).Assembly.GetType(
            "Boutquin.Storage.Distributed.Partitioning.JumpConsistentHash");

        type.Should().NotBeNull();
    }

    [Fact]
    public void GetBucket_MatchesPublishedCompatibilityVectors()
    {
        int[] expectedForOneHundredBuckets =
        [
            0, 55, 62, 8, 45, 59, 86, 97, 82, 59,
            73, 37, 17, 56, 86, 21, 90, 37, 38, 83,
        ];

        for (ulong key = 0; key < (ulong)expectedForOneHundredBuckets.Length; key++)
        {
            JumpConsistentHash.GetBucket(key, 100).Should().Be(expectedForOneHundredBuckets[key]);
        }

        JumpConsistentHash.GetBucket(10_863_919_174_838_991UL, 11).Should().Be(6);
        JumpConsistentHash.GetBucket(2_016_238_256_797_177_309UL, 11).Should().Be(3);
        JumpConsistentHash.GetBucket(1_673_758_223_894_951_030UL, 11).Should().Be(5);
        JumpConsistentHash.GetBucket(2UL, 100_001).Should().Be(80_343);
    }

    [Fact]
    public void GetBucket_AddedBucketOnlyStealsKeysForItself()
    {
        var random = new Random(20_260_917);
        for (var sample = 0; sample < 2_000; sample++)
        {
            var key = unchecked((ulong)random.NextInt64(long.MinValue, long.MaxValue));
            var previous = JumpConsistentHash.GetBucket(key, 1);

            for (var bucketCount = 2; bucketCount <= 128; bucketCount++)
            {
                var current = JumpConsistentHash.GetBucket(key, bucketCount);
                current.Should().BeOneOf(previous, bucketCount - 1);
                previous = current;
            }
        }
    }

    [Fact]
    public void GetBucket_RejectsNonPositiveBucketCount()
    {
        var act = () => JumpConsistentHash.GetBucket(42, 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
