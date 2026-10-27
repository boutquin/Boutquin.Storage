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

using Boutquin.Storage.Infrastructure.Caching;

namespace Boutquin.Storage.Infrastructure.Tests.OssComparison;

public sealed class AlgorithmOracleTests
{
    [Fact]
    public void HashFamilies_MatchIndependentImplementationsAcrossBlockAndTailLengths()
    {
        // Executed Austin Appleby's public-domain x86_32 reference at commit
        // 07bb4de10a63e8cc2e1724865454eba635742383, using Storage's documented seed.
        uint[] murmur =
        [
            0x821F9986U, 0x5E8D7A77U, 0xA1DCB873U, 0x4CC94F34U, 0x747C13A4U, 0x65F4CA0AU, 0xA97C4159U, 0xBFE4A418U,
            0x848C3BA1U, 0x5EF26983U, 0xE8AC20C7U, 0x4A5E591DU, 0x1C2F2E85U, 0xCB72A61CU, 0x110DD89DU, 0xD3231A79U,
            0xC55BA591U, 0xF91465A5U, 0x1A9C063CU, 0xF9EF967CU, 0x94048789U, 0xA4AEF487U, 0x3E71AB2CU, 0xF5FFA8D0U,
            0x9942BF46U, 0xA34FB2F2U, 0x6FCBFCE7U, 0x53B5B8C6U, 0xA450A12FU, 0xD72D6AE8U, 0xB26613ACU, 0x2936FDAAU,
            0x024959CAU, 0xDF89FB73U, 0x96134274U, 0x4E1ED7D5U, 0xC88EE408U, 0xDAED7223U, 0x3D7EA0B9U, 0x80444D8CU,
            0xECE9C429U, 0x5E9D2852U, 0x3E3A1B69U, 0xBA8FAD84U, 0x57A6E3F4U, 0xE0A1F836U, 0x7C227E6EU, 0xA3F27233U,
            0x0AC1261EU, 0x294F21E3U, 0x33B1B401U, 0xACB35586U, 0xDB8EC9E3U, 0x0FF094F2U, 0x31CA5E00U, 0x39A0D5E6U,
            0xBCBB319CU, 0xF1B0E360U, 0xE251C10DU, 0x20124793U, 0x798A0935U, 0x4BCDCB8AU, 0x64A06C01U, 0xBD87EA7FU,
            0xE05208AAU
        ];
        for (var length = 0; length <= 256; length++)
        {
            var data = Enumerable.Range(0, length).Select(i => unchecked((byte)(i * 37 + length))).ToArray();
            new XxHash32().ComputeHash(data).Should().Be(System.IO.Hashing.XxHash32.HashToUInt32(data));
            ulong fnv = 2166136261;
            uint crc = uint.MaxValue;
            foreach (var octet in data)
            {
                fnv = ((fnv ^ octet) * 16777619) & uint.MaxValue;
                crc ^= octet;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0x82F63B78U;
                }
            }
            new Fnv1aHash().ComputeHash(data).Should().Be((uint)fnv);
            Crc32C.HashToUInt32(data).Should().Be(~crc);
            if (length < murmur.Length)
            {
                new Murmur3().ComputeHash(data).Should().Be(murmur[length]);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OrderedStores_OverwriteHistoriesMatchSortedDictionary(int implementation)
    {
        IBulkKeyValueStore<int, int> store = implementation switch
        {
            0 => new BTree<int, int>(2),
            1 => new BPlusTree<int, int>(2),
            2 => new RedBlackTree<int, int>(512),
            _ => new SkipListMemTable<int, int>(512, random: new Random(39))
        };
        var expected = new SortedDictionary<int, int>();
        var random = new Random(7301);
        for (var step = 1; step <= 350; step++)
        {
            var key = random.Next(1, 81);
            expected[key] = step;
            await store.SetAsync(key, step).ConfigureAwait(true);
            var actual = await store.GetAllItemsAsync().ConfigureAwait(true);
            actual.OrderBy(pair => pair.Key).Should().Equal(expected.Select(pair => (pair.Key, pair.Value)));
            for (var probe = 1; probe <= 80; probe++)
            {
                var result = await store.TryGetValueAsync(probe).ConfigureAwait(true);
                result.Found.Should().Be(expected.ContainsKey(probe));
                if (result.Found) { result.Value.Should().Be(expected[probe]); }
            }
        }
        await store.ClearAsync().ConfigureAwait(true);
        (await store.GetAllItemsAsync().ConfigureAwait(true)).Should().BeEmpty();
        (store as IDisposable)?.Dispose();
    }

    [Fact]
    public void BloomFamilies_NoFalseNegativesAcrossDuplicateAndRemovalHistories()
    {
        using var bloom = new BloomFilter<int>(1000, 0.01, BitConverter.GetBytes);
        using var counting = new CountingBloomFilter<int>(1000, 0.01, BitConverter.GetBytes);
        for (var value = 1; value <= 1000; value++)
        {
            bloom.Add(value);
            counting.Add(value);
            counting.Add(value);
        }
        for (var value = 1; value <= 1000; value++)
        {
            counting.Remove(value);
            bloom.Contains(value).Should().BeTrue();
            counting.Contains(value).Should().BeTrue();
            counting.GetCount(value).Should().BeGreaterThanOrEqualTo(1);
        }
        // A finite deterministic corpus sanity check, not a statistical guarantee.
        Enumerable.Range(1001, 10_000).Count(bloom.Contains).Should().BeLessThan(300);
        bloom.Clear();
        counting.Clear();
        Enumerable.Range(1, 1000).Should().OnlyContain(value => !bloom.Contains(value) && !counting.Contains(value));
    }

    [Fact]
    public void LruCache_WeightedHistoryMatchesListModel()
    {
        var cache = new LruBlockCache(23, shardCount: 1);
        var model = new List<(int Key, byte[] Value)>();
        var random = new Random(911);
        for (var step = 0; step < 1000; step++)
        {
            var key = random.Next(12);
            var found = model.FindIndex(item => item.Key == key);
            if (step % 3 == 0)
            {
                cache.TryGet(new BlockCacheKey(1, key), out var actual).Should().Be(found >= 0);
                if (found >= 0)
                {
                    var item = model[found];
                    actual.Should().Equal(item.Value);
                    model.RemoveAt(found);
                    model.Add(item);
                }
            }
            else
            {
                var bytes = Enumerable.Repeat((byte)key, random.Next(1, 12)).ToArray();
                cache.Put(new BlockCacheKey(1, key), bytes, bytes.Length);
                if (found >= 0) { model.RemoveAt(found); }
                model.Add((key, bytes));
                while (model.Sum(item => item.Value.Length) > 23) { model.RemoveAt(0); }
            }
        }
    }

    [Fact]
    public async Task SecondaryIndex_MembershipMatchesIndependentRelation()
    {
        using var index = new SecondaryIndex<int, int, int>(value => value);
        var relation = new HashSet<(int Key, int Group)>();
        var random = new Random(710);
        for (var step = 0; step < 400; step++)
        {
            var key = random.Next(1, 31);
            var group = random.Next(1, 6);
            if (step % 3 == 0)
            {
                relation.Remove((key, group));
                await index.RemoveAsync(key, group).ConfigureAwait(true);
            }
            else
            {
                relation.Add((key, group));
                await index.IndexAsync(key, group).ConfigureAwait(true);
            }
            for (var g = 1; g <= 5; g++)
            {
                (await index.LookupAsync(g).ConfigureAwait(true)).Order()
                    .Should().Equal(relation.Where(pair => pair.Group == g).Select(pair => pair.Key).Order());
            }
        }
    }
}
