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
/// Pins for load-bearing invariants that had no test that would fail if violated:
/// the skip list's derived level cap and probability validation, the incremental and
/// two-span CRC32C paths' equivalence with the one-shot known-answer value, and the
/// secondary index's caller-maintained update protocol.
/// </summary>
public sealed class InvariantPinningTests
{
    // ── SkipListMemTable: derived MaxLevel cap + probability bounds ──────────

    [Fact]
    public void SkipList_DerivedMaxLevel_FollowsLogOfCapacity()
    {
        // MaxLevel = ceil(log_{1/p}(maxSize)) for p = 0.5: a regression back to a fixed
        // tiny cap silently degrades search to O(n / 2^cap) and no behavioural test would
        // notice on small datasets.
        new SkipListMemTable<int, int>(1_000_000).MaxLevel.Should().Be(20);
        new SkipListMemTable<int, int>(1024).MaxLevel.Should().Be(10);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    [InlineData(-0.5)]
    public void SkipList_ProbabilityOutsideOpenUnitInterval_Throws(double probability)
    {
        var act = () => new SkipListMemTable<int, int>(maxSize: 16, maxLevel: 0, probability: probability);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Crc32C: two-span and incremental paths must equal the one-shot KAT ───

    [Fact]
    public void Crc32C_TwoSpanAndIncremental_MatchOneShotKat_AtEverySplit()
    {
        // The Castagnoli check value for "123456789" is 0xE3069283. The two-span overload
        // (used by block frames) and the Append/Finalize incremental path (used by the
        // manifest and SSTable footers) must produce the same value at every split point —
        // a symmetric writer/reader bug in either path survives round-trip tests.
        var data = "123456789"u8.ToArray();
        const uint expected = 0xE3069283u;

        Crc32C.HashToUInt32(data).Should().Be(expected);

        for (var split = 0; split <= data.Length; split++)
        {
            var a = data.AsSpan(0, split);
            var b = data.AsSpan(split);

            Crc32C.HashToUInt32(a, b).Should().Be(expected, $"two-span split at {split} must match the KAT");

            var state = Crc32C.InitialState;
            Crc32C.Append(a, ref state);
            Crc32C.Append(b, ref state);
            Crc32C.Finalize(state).Should().Be(expected, $"incremental split at {split} must match the KAT");
        }
    }
}

/// <summary>
/// Pins the secondary index's caller-maintained update protocol: re-indexing a primary key
/// under a new value does NOT remove the posting its old value created — the caller must
/// pair RemoveAsync(old) with IndexAsync(new). This characterizes the documented contract
/// so a silent behavioural change (auto-remove on re-index) is caught.
/// </summary>
public sealed class SecondaryIndexUpdateProtocolTests
{
    [Fact]
    public async Task ReIndexWithoutRemove_LeavesStalePosting_ByDocumentedContract()
    {
        var index = new SecondaryIndex<SerializableWrapper<int>, string, SerializableWrapper<string>>(
            value => new SerializableWrapper<string>(value.Split(':')[0]));
        var pk = new SerializableWrapper<int>(1);

        await index.IndexAsync(pk, "engineering:Alice");
        await index.IndexAsync(pk, "marketing:Alice"); // re-index, no RemoveAsync

        (await index.LookupAsync(new SerializableWrapper<string>("engineering")))
            .Should().Contain(pk, "without RemoveAsync(old) the stale posting remains — the documented protocol");
        (await index.LookupAsync(new SerializableWrapper<string>("marketing")))
            .Should().Contain(pk);

        // The documented two-call protocol clears the stale posting.
        await index.RemoveAsync(pk, "engineering:Alice");
        (await index.LookupAsync(new SerializableWrapper<string>("engineering")))
            .Should().BeEmpty("RemoveAsync(old) + IndexAsync(new) is the complete update");
    }
}

/// <summary>
/// Structural invariant checks for BTree and BPlusTree after randomized and sequential
/// (worst-case) insertion batches — the RB-tree precedent applied to its siblings: occupancy
/// bounds, child/key arity, uniform leaf depth, separator ordering, and B+ leaf-chain
/// exactness. Behavioural iteration tests cannot catch a silent balance regression.
/// </summary>
public sealed class BTreeFamilyStructuralInvariantTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task BTree_RandomizedAndSequentialInsertion_PreservesInvariants(int minimumDegree)
    {
        var sequential = new BTree<int, int>(minimumDegree);
        for (var i = 1; i <= 500; i++)
        {
            await sequential.SetAsync(i, i);
            sequential.SatisfiesBTreeInvariants().Should().BeTrue($"sequential insert {i} must preserve B-tree invariants");
        }

        var randomized = new BTree<int, int>(minimumDegree);
        var rng = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            await randomized.SetAsync(rng.Next(1, 10_000), i + 1);
        }
        randomized.SatisfiesBTreeInvariants().Should().BeTrue();
    }

    [Theory]
    [InlineData(2)] // minimum order — the tightest split geometry
    [InlineData(3)]
    [InlineData(4)]
    public async Task BPlusTree_RandomizedAndSequentialInsertion_PreservesInvariants(int order)
    {
        var sequential = new BPlusTree<int, int>(order);
        for (var i = 1; i <= 500; i++)
        {
            await sequential.SetAsync(i, i);
            sequential.SatisfiesBPlusTreeInvariants().Should().BeTrue($"sequential insert {i} must preserve B+ tree invariants");
        }

        var randomized = new BPlusTree<int, int>(order);
        var rng = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            await randomized.SetAsync(rng.Next(1, 10_000), i + 1);
        }
        randomized.SatisfiesBPlusTreeInvariants().Should().BeTrue();
    }
}
