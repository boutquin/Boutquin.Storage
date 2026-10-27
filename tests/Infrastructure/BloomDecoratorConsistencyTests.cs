// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

#nullable disable

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the one forbidden failure direction of <see cref="BulkKeyValueStoreWithBloomFilter{TKey, TValue}"/>:
/// the filter must stay a superset of the inner store's keys on every mutation path, including
/// the paths where the inner store throws. A surplus filter bit only costs a false positive (the
/// read falls through to the inner store); a missing bit makes the decorator report a durably
/// stored key as absent.
/// </summary>
public sealed class BloomDecoratorConsistencyTests
{
    private static readonly SerializableWrapper<int> s_key1 = new(1);
    private static readonly SerializableWrapper<int> s_key2 = new(2);
    private static readonly SerializableWrapper<string> s_value1 = new("one");
    private static readonly SerializableWrapper<string> s_value2 = new("two");

    private static BloomFilter<SerializableWrapper<int>> NewRealFilter() =>
        new(expectedElements: 1000, falsePositiveProbability: 0.01, itemToBytes: k => BitConverter.GetBytes(k.Value));

    /// <summary>
    /// An inner store that answers reads for keys 1 and 2 as present — the state after those keys
    /// were durably written. Every test below makes one mutation path throw and then asks the
    /// decorator about a key the inner store holds.
    /// </summary>
    private static Mock<ICompactableBulkStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>> NewInnerHoldingKeys()
    {
        var inner = new Mock<ICompactableBulkStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        inner.Setup(s => s.TryGetValueAsync(s_key1, It.IsAny<CancellationToken>())).ReturnsAsync((s_value1, true));
        inner.Setup(s => s.TryGetValueAsync(s_key2, It.IsAny<CancellationToken>())).ReturnsAsync((s_value2, true));
        inner.Setup(s => s.ContainsKeyAsync(s_key1, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        inner.Setup(s => s.ContainsKeyAsync(s_key2, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return inner;
    }

    [Fact]
    public async Task CompactAsync_RebuildReadFails_FilterStillCoversStoredKeys()
    {
        var inner = NewInnerHoldingKeys();
        inner.SetupSequence(s => s.GetAllItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([(s_key1, s_value1), (s_key2, s_value2)]) // CreateAsync seed
            .ThrowsAsync(new IOException("simulated read failure during the post-compaction rebuild"));

        using var filter = NewRealFilter();
        using var store = await BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>
            .CreateAsync(inner.Object, filter);

        var compact = () => store.CompactAsync();
        await compact.Should().ThrowAsync<IOException>();

        var (_, found) = await store.TryGetValueAsync(s_key1);
        found.Should().BeTrue("a failed rebuild must leave the previous covering filter in place, not an emptied one");
        (await store.ContainsKeyAsync(s_key2)).Should().BeTrue();
    }

    [Fact]
    public async Task CompactAsync_RebuildEnumerationFailsMidway_FilterStillCoversStoredKeys()
    {
        // GetAllItemsAsync is allowed to return a lazy sequence; a fault surfaced during its
        // enumeration is a rebuild failure just as much as a faulted task.
        static IEnumerable<(SerializableWrapper<int> Key, SerializableWrapper<string> Value)> FaultsAfterFirstItem()
        {
            yield return (s_key1, s_value1);
            throw new IOException("simulated read failure while enumerating the post-compaction key set");
        }

        var inner = NewInnerHoldingKeys();
        inner.SetupSequence(s => s.GetAllItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([(s_key1, s_value1), (s_key2, s_value2)]) // CreateAsync seed
            .ReturnsAsync(FaultsAfterFirstItem());

        using var filter = NewRealFilter();
        using var store = await BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>
            .CreateAsync(inner.Object, filter);

        var compact = () => store.CompactAsync();
        await compact.Should().ThrowAsync<IOException>();

        (await store.ContainsKeyAsync(s_key2)).Should().BeTrue(
            "key 2 was never re-read by the faulted rebuild, so only the previous filter can still cover it");
    }

    [Fact]
    public async Task SetBulkAsync_InnerThrowsAfterPartialApply_PersistedKeysStayVisible()
    {
        var inner = NewInnerHoldingKeys();
        inner.Setup(s => s.GetAllItemsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        // The shipped append-only engines validate per item inside their write loop, so an
        // invalid later item throws after earlier items were already appended.
        inner.Setup(s => s.SetBulkAsync(
                It.IsAny<IEnumerable<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentNullException("Value"));

        using var filter = NewRealFilter();
        using var store = await BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>
            .CreateAsync(inner.Object, filter);

        var write = () => store.SetBulkAsync([new(s_key1, s_value1), new(s_key2, null)]);
        await write.Should().ThrowAsync<ArgumentNullException>();

        var (_, found) = await store.TryGetValueAsync(s_key1);
        found.Should().BeTrue("a key the inner store persisted before the batch failed must not be hidden by the filter");
    }

    [Fact]
    public async Task SetAsync_InnerThrowsAfterPersisting_KeyStaysVisible()
    {
        var inner = NewInnerHoldingKeys();
        inner.Setup(s => s.GetAllItemsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        // A write that reached the file but failed afterwards (e.g. the durability flush) leaves
        // the key readable from the inner store even though the call threw.
        inner.Setup(s => s.SetAsync(s_key1, s_value1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("simulated failure after the record was appended"));

        using var filter = NewRealFilter();
        using var store = await BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>
            .CreateAsync(inner.Object, filter);

        var write = () => store.SetAsync(s_key1, s_value1);
        await write.Should().ThrowAsync<IOException>();

        (await store.ContainsKeyAsync(s_key1)).Should().BeTrue(
            "the filter must be updated before the inner write so a write that lands and then throws is still covered");
    }
}
