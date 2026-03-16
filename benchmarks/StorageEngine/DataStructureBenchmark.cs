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
namespace Boutquin.Storage.BenchMark;

/// <summary>
/// Compares insert and search performance across BTree, BPlusTree, RedBlackTree, and SkipListMemTable.
/// These data structures all serve as in-memory sorted containers — the classic Kleppmann comparison.
/// </summary>
/// <remarks>
/// <para>
/// All four data structures expose async APIs (SetAsync/TryGetValueAsync) but complete synchronously
/// (returning Task.CompletedTask or Task.FromResult). BenchmarkDotNet cannot reliably measure async
/// methods that complete synchronously — the async state machine overhead dominates and produces NA
/// results. We therefore call .GetAwaiter().GetResult() to measure the actual algorithmic work.
/// </para>
/// <para>
/// This class is not sealed because BenchmarkDotNet requires benchmark classes to be inheritable.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class DataStructureBenchmark
{
    /// <summary>
    /// Number of items to insert and search.
    /// </summary>
    [Params(100, 1000, 10000)]
    public int ItemCount;

    // Pre-populated copies for search benchmarks.
    private BTree<int, string> _btreePopulated = null!;
    private BPlusTree<int, string> _bPlusTreePopulated = null!;
    private RedBlackTree<int, string> _redBlackTreePopulated = null!;
    private SkipListMemTable<int, string> _skipListPopulated = null!;

    /// <summary>
    /// Pre-populates data structures for search benchmarks.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _btreePopulated = new BTree<int, string>(minimumDegree: 4);
        _bPlusTreePopulated = new BPlusTree<int, string>(order: 4);
        _redBlackTreePopulated = new RedBlackTree<int, string>(maxSize: ItemCount + 1000);
        _skipListPopulated = new SkipListMemTable<int, string>(maxSize: ItemCount + 1000);

        for (var i = 0; i < ItemCount; i++)
        {
            var value = $"Value {i}";
            _btreePopulated.SetAsync(i, value).GetAwaiter().GetResult();
            _bPlusTreePopulated.SetAsync(i, value).GetAwaiter().GetResult();
            _redBlackTreePopulated.SetAsync(i, value).GetAwaiter().GetResult();
            _skipListPopulated.SetAsync(i, value).GetAwaiter().GetResult();
        }
    }

    // --- Insert benchmarks ---

    /// <summary>
    /// Benchmarks BTree insertion of all items.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Insert")]
    public void BTreeInsert()
    {
        var tree = new BTree<int, string>(minimumDegree: 4);
        for (var i = 0; i < ItemCount; i++)
        {
            tree.SetAsync(i, $"Value {i}").GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Benchmarks B+Tree insertion of all items.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Insert")]
    public void BPlusTreeInsert()
    {
        var tree = new BPlusTree<int, string>(order: 4);
        for (var i = 0; i < ItemCount; i++)
        {
            tree.SetAsync(i, $"Value {i}").GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Benchmarks RedBlackTree insertion of all items.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Insert")]
    public void RedBlackTreeInsert()
    {
        var tree = new RedBlackTree<int, string>(maxSize: ItemCount + 1000);
        for (var i = 0; i < ItemCount; i++)
        {
            tree.SetAsync(i, $"Value {i}").GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Benchmarks SkipListMemTable insertion of all items.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Insert")]
    public void SkipListInsert()
    {
        var list = new SkipListMemTable<int, string>(maxSize: ItemCount + 1000);
        for (var i = 0; i < ItemCount; i++)
        {
            list.SetAsync(i, $"Value {i}").GetAwaiter().GetResult();
        }
    }

    // --- Search benchmarks ---

    /// <summary>
    /// Benchmarks BTree search for all items.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Search")]
    public void BTreeSearch()
    {
        for (var i = 0; i < ItemCount; i++)
        {
            _btreePopulated.TryGetValueAsync(i).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Benchmarks B+Tree search for all items.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Search")]
    public void BPlusTreeSearch()
    {
        for (var i = 0; i < ItemCount; i++)
        {
            _bPlusTreePopulated.TryGetValueAsync(i).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Benchmarks RedBlackTree search for all items.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Search")]
    public void RedBlackTreeSearch()
    {
        for (var i = 0; i < ItemCount; i++)
        {
            _redBlackTreePopulated.TryGetValueAsync(i).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Benchmarks SkipListMemTable search for all items.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Search")]
    public void SkipListSearch()
    {
        for (var i = 0; i < ItemCount; i++)
        {
            _skipListPopulated.TryGetValueAsync(i).GetAwaiter().GetResult();
        }
    }
}
