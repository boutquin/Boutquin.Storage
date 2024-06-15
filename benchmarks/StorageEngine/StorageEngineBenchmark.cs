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
/// Benchmarking class for the Storage Engine, used to measure performance of various operations
/// like writing, reading, and searching items in the key-value store.
/// </summary>
/// <typeparam name="TKey">Type of the key, must implement ISerializable, IComparable, and have a parameterless constructor.</typeparam>
/// <typeparam name="TValue">Type of the value, must implement ISerializable and have a parameterless constructor.</typeparam>
public class StorageEngineBenchmark<TKey, TValue>
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    private IStorageEngine<TKey, TValue> _store; // Instance of the key-value store being benchmarked
    private List<TKey> _keys; // List to store keys used in the benchmark
    private List<TValue> _values; // List to store values used in the benchmark

    /// <summary>
    /// Number of items to be used in the benchmark.
    /// </summary>
    [Params(10, 100, 1000)]
    public int ItemCount;

    /// <summary>
    /// Sets up the benchmark by initializing the key-value store with a specified number of items.
    /// This method is executed once per benchmark run to set up the test data.
    /// </summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _keys = new List<TKey>(ItemCount); // Initialize the list of keys with the specified capacity
        _values = new List<TValue>(ItemCount); // Initialize the list of values with the specified capacity

        // Populate the keys and values lists with test data
        for (var i = 0; i < ItemCount; i++)
        {
            _keys.Add((TKey)(object)new SerializableWrapper<int>(i)); // Convert integer to TKey
            _values.Add((TValue)(object)new SerializableWrapper<string>($"Value {i}")); // Convert string to TValue
        }

        await _store.ClearAsync(); // Clear any existing data in the store

        // Populate the store with the initial data
        var tasks = new List<Task>();
        for (var i = 0; i < ItemCount; i++)
        {
            tasks.Add(_store.SetAsync(_keys[i], _values[i])); // Add tasks to set key-value pairs in the store
        }
        await Task.WhenAll(tasks); // Wait for all tasks to complete
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
    /// Measures the time taken to write all items in the keys and values lists to the store.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Write")]
    public async Task Write()
    {
        var tasks = new List<Task>();
        for (var i = 0; i < ItemCount; i++)
        {
            tasks.Add(_store.SetAsync(_keys[i], _values[i])); // Add tasks to set key-value pairs in the store
        }
        await Task.WhenAll(tasks); // Wait for all tasks to complete
    }

    /// <summary>
    /// Benchmark for reading items from the store.
    /// Measures the time taken to read all items in the keys list from the store.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Read")]
    public async Task Read()
    {
        var tasks = new List<Task>();
        for (var i = 0; i < ItemCount; i++)
        {
            tasks.Add(_store.TryGetValueAsync(_keys[i])); // Add tasks to get values for keys from the store
        }
        await Task.WhenAll(tasks); // Wait for all tasks to complete
    }

    /// <summary>
    /// Benchmark for searching existing items in the store.
    /// Measures the time taken to check the existence of all items in the keys list in the store.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("SearchExisting")]
    public async Task SearchExisting()
    {
        var tasks = new List<Task>();
        for (var i = 0; i < ItemCount; i++)
        {
            tasks.Add(_store.ContainsKeyAsync(_keys[i])); // Add tasks to check if keys exist in the store
        }
        await Task.WhenAll(tasks); // Wait for all tasks to complete
    }

    /// <summary>
    /// Benchmark for searching non-existing items in the store.
    /// Measures the time taken to check the existence of items that are not in the store.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("SearchNonExisting")]
    public async Task SearchNonExisting()
    {
        var tasks = new List<Task>();
        for (var i = ItemCount; i < ItemCount * 2; i++)
        {
            tasks.Add(_store.ContainsKeyAsync((TKey)(object)new SerializableWrapper<int>(i))); // Add tasks to check non-existing keys in the store
        }
        await Task.WhenAll(tasks); // Wait for all tasks to complete
    }
}