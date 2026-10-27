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
namespace Boutquin.Storage.Infrastructure.DataStructures;

/// <summary>
/// A skip list-based MemTable implementation for LSM-tree storage engines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a skip list?</b> Skip lists provide the same O(log n) expected performance as balanced trees
/// (red-black, AVL) but are simpler to implement and more amenable to concurrent access. LevelDB and
/// RocksDB use skip lists as their default memtable implementation.
/// </para>
///
/// <para>
/// <b>Why randomized levels?</b> Each node is assigned a random level on insertion using a geometric
/// distribution (each level has probability p of being promoted). This produces an expected O(log n)
/// height without any rebalancing operations. The probability parameter controls the space-time trade-off:
/// p=0.5 gives optimal search time; p=0.25 uses less memory per node at the cost of slightly deeper searches.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is not thread-safe. External synchronization is required for concurrent use.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The type of the keys.</typeparam>
/// <typeparam name="TValue">The type of the values.</typeparam>
public sealed class SkipListMemTable<TKey, TValue> : ISkipListMemTable<TKey, TValue>
    where TKey : IComparable<TKey>, IEquatable<TKey>
{
    private sealed class SkipListNode(TKey key, TValue value, int level)
    {
        public TKey Key { get; } = key;
        public TValue Value { get; set; } = value;
        public SkipListNode[] Forward { get; } = new SkipListNode[level + 1];
    }

    private readonly int _maxSize;
    private readonly double _probability;
    private readonly Random _random;
    private SkipListNode _head;
    private int _count;
    private int _currentLevel;

    /// <inheritdoc/>
    public bool IsFull => _count >= _maxSize;

    /// <inheritdoc/>
    public int MaxLevel { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SkipListMemTable{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="maxSize">The maximum number of elements the MemTable can hold.</param>
    /// <param name="maxLevel">The maximum number of levels in the skip list. Pass <c>0</c> (the
    /// default) to derive a size-appropriate cap of <c>⌈log_{1/probability}(maxSize)⌉</c>, which
    /// keeps search/insert at the expected O(log n) for the configured capacity (finding #17). A
    /// fixed too-small cap throttles search toward O(n / 2^maxLevel) once n exceeds 2^maxLevel.</param>
    /// <param name="probability">The probability of promoting a node to the next level. Must be in
    /// the open interval (0, 1): a value ≥ 1 promotes every node to <see cref="MaxLevel"/> and a
    /// value ≤ 0 never promotes, both collapsing the skip list to an O(n) linked list.</param>
    public SkipListMemTable(int maxSize, int maxLevel = 0, double probability = 0.5, Random? random = null)
    {
        if (maxSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSize), "Maximum size must be at least 1.");
        }
        if (maxLevel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLevel), "Maximum level must be non-negative (0 derives it from maxSize).");
        }
        if (probability is <= 0.0 or >= 1.0 || double.IsNaN(probability))
        {
            throw new ArgumentOutOfRangeException(nameof(probability), probability,
                "Promotion probability must be in the open interval (0, 1).");
        }

        _maxSize = maxSize;
        _random = random ?? Random.Shared;
        // A cap of 0 means "derive from capacity": the expected number of levels for n elements at
        // promotion probability p is log_{1/p}(n). Capping there keeps the structure O(log n) at
        // its configured size without over-allocating sentinel forward-pointer arrays.
        MaxLevel = maxLevel > 0
            ? maxLevel
            : Math.Max(1, (int)Math.Ceiling(Math.Log(maxSize) / Math.Log(1.0 / probability)));
        _probability = probability;
        // Why default(TKey)! for head? The head node is a sentinel that is never accessed for its key/value.
        // It serves only as the entry point for traversal. Using null-forgiving avoids nullable complications
        // for a node that has no semantic key.
        _head = new SkipListNode(default!, default!, MaxLevel);
        _count = 0;
        _currentLevel = 0;
    }

    /// <inheritdoc/>
    public Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);

        if (_count >= _maxSize && !ContainsKey(key))
        {
            throw new InvalidOperationException("The MemTable is full.");
        }

        var update = new SkipListNode[MaxLevel + 1];
        var current = _head;

        // Traverse from the highest level down to level 0, recording the last node at each level
        // before the insertion point. These are the nodes whose forward pointers need updating.
        for (var i = _currentLevel; i >= 0; i--)
        {
            while (current.Forward[i] != null && KeyOrder<TKey>.Compare(current.Forward[i].Key, key) < 0)
            {
                current = current.Forward[i];
            }
            update[i] = current;
        }

        current = current.Forward[0];

        if (current != null && KeyOrder<TKey>.Compare(current.Key, key) == 0)
        {
            // Key exists — update value in place
            current.Value = value;
        }
        else
        {
            var newLevel = RandomLevel();
            if (newLevel > _currentLevel)
            {
                for (var i = _currentLevel + 1; i <= newLevel; i++)
                {
                    update[i] = _head;
                }
                _currentLevel = newLevel;
            }

            var newNode = new SkipListNode(key, value, newLevel);
            for (var i = 0; i <= newLevel; i++)
            {
                newNode.Forward[i] = update[i].Forward[i];
                update[i].Forward[i] = newNode;
            }

            _count++;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);

        var current = _head;
        for (var i = _currentLevel; i >= 0; i--)
        {
            while (current.Forward[i] != null && KeyOrder<TKey>.Compare(current.Forward[i].Key, key) < 0)
            {
                current = current.Forward[i];
            }
        }

        current = current.Forward[0];
        if (current != null && KeyOrder<TKey>.Compare(current.Key, key) == 0)
        {
            return Task.FromResult((current.Value, true));
        }

        return Task.FromResult((default(TValue)!, false));
    }

    /// <inheritdoc/>
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        var (_, found) = await TryGetValueAsync(key, cancellationToken).ConfigureAwait(false);
        return found;
    }

    /// <inheritdoc/>
    public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        // Why NotSupportedException? In LSM-tree architectures, deletes are represented as tombstone
        // entries (a special marker value written via SetAsync), not as physical removal from the
        // memtable. The actual removal happens during compaction when tombstones cancel out values.
        throw new NotSupportedException("Remove is not supported in LSM-tree MemTable. Use tombstone entries instead.");
    }

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _head = new SkipListNode(default!, default!, MaxLevel);
        _count = 0;
        _currentLevel = 0;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<(TKey Key, TValue Value)>();
        var current = _head.Forward[0];
        while (current != null)
        {
            items.Add((current.Key, current.Value));
            current = current.Forward[0];
        }

        return Task.FromResult<IEnumerable<(TKey Key, TValue Value)>>(items);
    }

    /// <inheritdoc/>
    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        foreach (var item in items)
        {
            await SetAsync(item.Key, item.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool ContainsKey(TKey key)
    {
        var current = _head;
        for (var i = _currentLevel; i >= 0; i--)
        {
            while (current.Forward[i] != null && KeyOrder<TKey>.Compare(current.Forward[i].Key, key) < 0)
            {
                current = current.Forward[i];
            }
        }

        current = current.Forward[0];
        return current != null && KeyOrder<TKey>.Compare(current.Key, key) == 0;
    }

    private int RandomLevel()
    {
        var level = 0;
        while (_random.NextDouble() < _probability && level < MaxLevel)
        {
            level++;
        }
        return level;
    }
}
