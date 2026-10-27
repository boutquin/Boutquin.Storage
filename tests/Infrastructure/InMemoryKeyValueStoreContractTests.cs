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

#nullable disable

using System.Collections.Concurrent;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the bulk-write contract of <see cref="InMemoryKeyValueStore{TKey, TValue}"/>: the batch is
/// enumerated exactly once, and a batch containing an invalid entry is rejected whole, leaving
/// the store untouched (the same all-or-nothing behavior as the LSM and append-only engines).
/// </summary>
public sealed class InMemoryKeyValueStoreContractTests
{
    private static InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>> NewStore() => new();

    private static async Task<List<(int Key, string Value)>> SnapshotAsync(
        InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>> store)
    {
        var all = await store.GetAllItemsAsync();
        return all.Select(i => (i.Key.Value, i.Value.Value)).ToList();
    }

    private static IEnumerable<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>> OneShot(
        IReadOnlyList<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>> source)
    {
        // Refuses a second traversal: models a lazily-produced or network-backed batch.
        var enumerated = 0;
        return Iterate();

        IEnumerable<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>> Iterate()
        {
            if (Interlocked.Increment(ref enumerated) > 1)
            {
                throw new InvalidOperationException("one-shot sequence enumerated twice");
            }

            foreach (var item in source)
            {
                yield return item;
            }
        }
    }

    [Fact]
    public async Task SetBulkAsync_ConsumingEnumerable_StoresEveryItem()
    {
        var store = NewStore();
        using var queue = new BlockingCollection<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(1, "a"),
            new(2, "b"),
            new(3, "c")
        };
        queue.CompleteAdding();

        await store.SetBulkAsync(queue.GetConsumingEnumerable());

        (await SnapshotAsync(store)).Should().Equal((1, "a"), (2, "b"), (3, "c"));
    }

    [Fact]
    public async Task SetBulkAsync_OneShotEnumerable_StoresEveryItem()
    {
        var store = NewStore();
        var batch = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(5, "e"),
            new(4, "d")
        };

        await store.SetBulkAsync(OneShot(batch));

        (await SnapshotAsync(store)).Should().Equal((4, "d"), (5, "e"));
    }

    [Fact]
    public async Task SetBulkAsync_BatchWithNullValue_RejectsWholeBatchAndStoresNothing()
    {
        var store = NewStore();
        var batch = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(1, "ten"),
            new(2, null)
        };

        var act = async () => await store.SetBulkAsync(batch);

        await act.Should().ThrowAsync<ArgumentNullException>();
        (await SnapshotAsync(store)).Should().BeEmpty();
    }

    [Fact]
    public async Task SetBulkAsync_BatchWithNullKeyAfterValidItems_LeavesExistingEntriesUntouched()
    {
        var store = NewStore();
        await store.SetAsync(1, "original");
        var batch = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(1, "overwritten"),
            new(3, "new"),
            new(null, "orphan")
        };

        var act = async () => await store.SetBulkAsync(batch);

        await act.Should().ThrowAsync<ArgumentNullException>();
        (await SnapshotAsync(store)).Should().Equal((1, "original"));
    }

    [Fact]
    public async Task SetBulkAsync_RejectedOneShotBatch_StoresNothing()
    {
        var store = NewStore();
        var batch = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(1, "ok"),
            new(2, null)
        };

        var act = async () => await store.SetBulkAsync(OneShot(batch));

        await act.Should().ThrowAsync<ArgumentNullException>();
        (await SnapshotAsync(store)).Should().BeEmpty();
    }

    [Fact]
    public async Task SetBulkAsync_EmptyBatch_IsRejected()
    {
        var store = NewStore();

        var act = async () => await store.SetBulkAsync([]);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SetBulkAsync_NullBatch_ThrowsArgumentNullException()
    {
        var store = NewStore();

        var act = async () => await store.SetBulkAsync(null);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SetBulkAsync_CancelledToken_StoresNothing()
    {
        var store = NewStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var batch = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>> { new(1, "a") };

        var act = async () => await store.SetBulkAsync(batch, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await SnapshotAsync(store)).Should().BeEmpty();
    }
}
