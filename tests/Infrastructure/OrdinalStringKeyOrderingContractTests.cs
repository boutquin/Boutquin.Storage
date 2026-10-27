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

using System.Globalization;

using Boutquin.Storage.Domain.ValueObjects;

/// <summary>
/// Pins the string-key identity and order contract of the in-memory ordered structures: two keys are
/// one key exactly when they are the same string, and keys order by Unicode code point (the UTF-8
/// byte order of the repository's on-disk key formats) whatever the calling thread's culture is.
/// <c>string.CompareTo</c> follows the current culture, which reports ordinally distinct strings as
/// equal (a soft hyphen, an embedded NUL, canonically equivalent forms) and reorders keys per process.
/// </summary>
public sealed class OrdinalStringKeyOrderingContractTests
{
    /// <summary>Pairs of distinct strings that the en-US culture comparison reports as equal.</summary>
    public static TheoryData<string, string> CultureEqualDistinctPairs => new()
    {
        { "co­op", "coop" },
        { "a\u0000b", "ab" },
        { "é", "é" },
    };

    // Written out in code-point order by hand: 'B' (U+0042) < 'a' (U+0061) < U+E000 < U+1F600. The
    // culture order puts 'a' before 'B'; UTF-16 ordinal order puts the U+1F600 surrogate pair before
    // U+E000. Only the code-point order matches the UTF-8 bytes the key formats persist.
    private static readonly string[] s_codePointOrder = ["B", "a", "", "\U0001F600"];

    [Theory]
    [MemberData(nameof(CultureEqualDistinctPairs))]
    public async Task BTree_KeepsCultureEqualDistinctStringKeysDistinct(string first, string second)
    {
        var tree = new BTree<string, string>(2);
        await AssertDistinctAsync(tree, first, second);
        (await tree.GetAllItemsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task BTree_KeepsCultureEqualDistinctStringKeysDistinctWhenTheRootIsFull()
    {
        // Three keys fill a minimum-degree-2 root, so the next write takes the full-root update probe
        // rather than the leaf insert.
        var tree = new BTree<string, string>(2);
        using (new CultureScope("en-US"))
        {
            await tree.SetAsync("a", "a");
            await tree.SetAsync("co­op", "first");
            await tree.SetAsync("z", "z");
            await tree.SetAsync("coop", "second");
            (await tree.TryGetValueAsync("co­op")).Should().Be(("first", true));
            (await tree.TryGetValueAsync("coop")).Should().Be(("second", true));
        }

        (await tree.GetAllItemsAsync()).Select(item => item.Key).Should().Equal("a", "coop", "co­op", "z");
    }

    [Fact]
    public async Task BTree_OrdersStringKeysByCodePointUnderAnyCulture()
    {
        var tree = new BTree<string, string>(2);
        (await InsertAndListAsync(tree)).Should().Equal(s_codePointOrder);
    }

    [Fact]
    public async Task BTree_KeepsTupleKeysWithCultureEqualStringComponentsDistinct()
    {
        var tree = new BTree<(string Name, int Id), string>(2);
        using (new CultureScope("en-US"))
        {
            await tree.SetAsync(("co­op", 1), "first");
            await tree.SetAsync(("coop", 1), "second");
            (await tree.TryGetValueAsync(("co­op", 1))).Should().Be(("first", true));
        }

        (await tree.GetAllItemsAsync()).Should().HaveCount(2);
    }

    [Theory]
    [MemberData(nameof(CultureEqualDistinctPairs))]
    public async Task BPlusTree_KeepsCultureEqualDistinctStringKeysDistinct(string first, string second)
    {
        var tree = new BPlusTree<string, string>(2);
        await AssertDistinctAsync(tree, first, second);
        (await tree.GetAllItemsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task BPlusTree_OrdersStringKeysAndRangeScansByCodePointUnderAnyCulture()
    {
        var tree = new BPlusTree<string, string>(2);
        (await InsertAndListAsync(tree)).Should().Equal(s_codePointOrder);

        using (new CultureScope("en-US"))
        {
            // [B, U+E000] in code-point order holds B, a and U+E000; the culture order would place 'a'
            // before the lower bound and drop it.
            var range = await tree.RangeQueryAsync("B", "");
            range.Select(item => item.Key).Should().Equal("B", "a", "");
        }
    }

    [Theory]
    [MemberData(nameof(CultureEqualDistinctPairs))]
    public async Task RedBlackTree_KeepsCultureEqualDistinctStringKeysDistinct(string first, string second)
    {
        var tree = new RedBlackTree<string, string>(16);
        await AssertDistinctAsync(tree, first, second);
        (await tree.GetAllItemsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task RedBlackTree_OrdersStringKeysByCodePointUnderAnyCulture()
    {
        var tree = new RedBlackTree<string, string>(16);
        (await InsertAndListAsync(tree)).Should().Equal(s_codePointOrder);
    }

    [Theory]
    [MemberData(nameof(CultureEqualDistinctPairs))]
    public async Task SkipListMemTable_KeepsCultureEqualDistinctStringKeysDistinct(string first, string second)
    {
        var table = new SkipListMemTable<string, string>(16, random: new Random(7));
        await AssertDistinctAsync(table, first, second);
        (await table.GetAllItemsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task SkipListMemTable_OrdersStringKeysByCodePointUnderAnyCulture()
    {
        var table = new SkipListMemTable<string, string>(16, random: new Random(7));
        (await InsertAndListAsync(table)).Should().Equal(s_codePointOrder);
    }

    [Theory]
    [MemberData(nameof(CultureEqualDistinctPairs))]
    public async Task InMemoryStorageIndex_KeepsCultureEqualDistinctStringKeysDistinct(string first, string second)
    {
        var index = new InMemoryStorageIndex<string, string>();
        await AssertDistinctAsync(index, first, second);
    }

    [Theory]
    [MemberData(nameof(CultureEqualDistinctPairs))]
    public async Task InMemoryFileIndex_KeepsCultureEqualDistinctStringKeysDistinct(string first, string second)
    {
        var index = new InMemoryFileIndex<string>();
        using (new CultureScope("en-US"))
        {
            await index.SetAsync(first, new FileLocation(0, 10));
            await index.SetAsync(second, new FileLocation(10, 20));
            (await index.TryGetValueAsync(first)).Should().Be((new FileLocation(0, 10), true));
            (await index.TryGetValueAsync(second)).Should().Be((new FileLocation(10, 20), true));
        }
    }

    private static async Task AssertDistinctAsync(IKeyValueStore<string, string> store, string first, string second)
    {
        using var scope = new CultureScope("en-US");
        await store.SetAsync(first, "first");
        await store.SetAsync(second, "second");
        (await store.TryGetValueAsync(first)).Should().Be(("first", true));
        (await store.TryGetValueAsync(second)).Should().Be(("second", true));
    }

    private static async Task<string[]> InsertAndListAsync(IBulkKeyValueStore<string, string> store)
    {
        // Insert under one culture and list under another: the order must be neither's.
        using (new CultureScope("tr-TR"))
        {
            foreach (var key in new[] { "\U0001F600", "a", "", "B" })
            {
                await store.SetAsync(key, key);
            }
        }

        using (new CultureScope("en-US"))
        {
            return (await store.GetAllItemsAsync()).Select(item => item.Key).ToArray();
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
