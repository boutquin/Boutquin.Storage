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

using System.Collections;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the bulk-write snapshot contract: a batch handed to <c>SetBulkAsync</c> as a live
/// <see cref="IReadOnlyCollection{T}"/> (a concurrent queue or dictionary that another thread
/// keeps writing to) is snapshotted exactly once, and that one snapshot is what is validated,
/// applied and added to the Bloom filter.
/// </summary>
public sealed class LiveCollectionBulkWriteSnapshotTests
{
    private static KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>> Entry(int key, string value) =>
        new(new SerializableWrapper<int>(key), value is null ? null : new SerializableWrapper<string>(value));

    /// <summary>
    /// Models a concurrent collection another thread keeps writing to: every enumeration after the
    /// first <c>stableEnumerations</c> takes a fresh moment-in-time snapshot that additionally
    /// contains a late arrival.
    /// </summary>
    private sealed class LiveCollection : IReadOnlyCollection<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
    {
        private readonly List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>> _initial;
        private readonly KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>> _lateArrival;
        private readonly int _stableEnumerations;
        private int _enumerations;

        public LiveCollection(
            List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>> initial,
            KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>> lateArrival,
            int stableEnumerations)
        {
            _initial = initial;
            _lateArrival = lateArrival;
            _stableEnumerations = stableEnumerations;
        }

        public int Enumerations => Volatile.Read(ref _enumerations);

        public int Count => _initial.Count;

        public IEnumerator<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>> GetEnumerator()
        {
            var snapshot = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>(_initial);
            if (Interlocked.Increment(ref _enumerations) > _stableEnumerations)
            {
                snapshot.Add(_lateArrival);
            }

            return snapshot.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task BloomDecorator_SetBulkAsync_WithLiveCollection_AddsEveryKeyTheInnerStoreReceives()
    {
        var inner = new Mock<ICompactableBulkStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        var bloom = new Mock<IBloomFilter<SerializableWrapper<int>>>();
        var added = new List<int>();
        bloom.Setup(f => f.Add(It.IsAny<SerializableWrapper<int>>()))
            .Callback<SerializableWrapper<int>>(k => added.Add(k.Value));

        var received = new List<int>();
        inner.Setup(s => s.SetBulkAsync(
                It.IsAny<IEnumerable<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>, CancellationToken>(
                (items, _) => received.AddRange(items.Select(i => i.Key.Value)))
            .Returns(Task.CompletedTask);

        using var store = await BulkKeyValueStoreWithBloomFilter<SerializableWrapper<int>, SerializableWrapper<string>>
            .CreateAsync(inner.Object, bloom.Object);

        var live = new LiveCollection(
            [Entry(1, "a"), Entry(2, "b")],
            Entry(99, "late"),
            stableEnumerations: 1);

        await store.SetBulkAsync(live);

        received.Should().NotBeEmpty();
        added.Should().Contain(received,
            "every key the inner engine durably stores must be in the filter, or the key reads back as absent");
        live.Enumerations.Should().Be(1, "the decorator snapshots a live collection exactly once");
    }

    [Fact]
    public async Task InMemoryStore_SetBulkAsync_WithLiveCollection_StoresOnlyTheValidatedSnapshot()
    {
        var store = new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>>();

        // The late arrival (a null value that SetAsync would reject) only shows up from the third
        // enumeration on, i.e. after the emptiness guard and the validate pass have both run.
        var live = new LiveCollection(
            [Entry(1, "a"), Entry(2, "b")],
            Entry(-7, null),
            stableEnumerations: 2);

        await store.SetBulkAsync(live);

        var all = (await store.GetAllItemsAsync()).Select(i => (i.Key.Value, i.Value?.Value)).ToList();
        all.Should().Equal((1, "a"), (2, "b"));
        live.Enumerations.Should().Be(1, "the store snapshots a live collection exactly once");
    }
}
