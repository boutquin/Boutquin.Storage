// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
namespace Boutquin.Storage.BenchMark;

/// <summary>
/// Benchmark class for testing key-value store operations.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the key-value store.</typeparam>
/// <typeparam name="TValue">The type of the values in the key-value store.</typeparam>
public class StorageEngineBenchmark<TKey, TValue> where TKey : IComparable<TKey>
{
    private IStorageEngine<TKey, TValue> _store;
    private List<TKey> _keys;
    private List<TValue> _values;

    /// <summary>
    /// Number of items to be used in the benchmark.
    /// </summary>
    [Params(100, 1000, 10000)]
    public int ItemCount;

    /// <summary>
    /// Sets up the benchmark by initializing the key-value store with a specified number of items.
    /// </summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _keys = new List<TKey>(ItemCount);
        _values = new List<TValue>(ItemCount);

        // Initialize keys and values
        for (int i = 0; i < ItemCount; i++)
        {
            _keys.Add((TKey)(object)new SerializableWrapper<int>(i)); // Use implicit conversion
            _values.Add((TValue)(object)new SerializableWrapper<string>($"Value {i}")); // Use implicit conversion
        }

        // Clear the store
        _store.ClearAsync();

        // Populate the store with the initial data
        for (int i = 0; i < ItemCount; i++)
        {
            await _store.SetAsync(_keys[i], _values[i]);
        }
    }

    /// <summary>
    /// Sets the key-value store to be benchmarked.
    /// </summary>
    /// <param name="store">The key-value store instance.</param>
    public void SetStore(IStorageEngine<TKey, TValue> store)
    {
        _store = store;
    }

    /// <summary>
    /// Benchmark for writing items to the store.
    /// </summary>
    [Benchmark]
    public async Task Write()
    {
        for (int i = 0; i < ItemCount; i++)
        {
            await _store.SetAsync(_keys[i], _values[i]);
        }
    }

    /// <summary>
    /// Benchmark for reading items from the store.
    /// </summary>
    [Benchmark]
    public async Task Read()
    {
        for (int i = 0; i < ItemCount; i++)
        {
            await _store.TryGetValueAsync(_keys[i]);
        }
    }

    /// <summary>
    /// Benchmark for searching existing items in the store.
    /// </summary>
    [Benchmark]
    public async Task SearchExisting()
    {
        for (int i = 0; i < ItemCount; i++)
        {
            await _store.ContainsKeyAsync(_keys[i]);
        }
    }

    /// <summary>
    /// Benchmark for searching non-existing items in the store.
    /// </summary>
    [Benchmark]
    public async Task SearchNonExisting()
    {
        for (int i = ItemCount; i < ItemCount * 2; i++)
        {
            await _store.ContainsKeyAsync((TKey)(object)new SerializableWrapper<int>(i)); // Use implicit conversion
        }
    }
}
