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
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Interface for the storage index, which manages the mapping of keys to their respective file offsets and sizes.
/// 
/// <para>Implementation Choices for IStorageIndex Algorithms:</para>
/// 
/// <para>1. B+ Tree:</para>
/// <para>- <strong>Description</strong>: A balanced tree data structure that maintains sorted data and allows searches, 
/// sequential access, insertions, and deletions in logarithmic time.</para>
/// <para>- <strong>Pros</strong>:</para>
/// <para>  - Efficient range queries due to the sorted order of keys.</para>
/// <para>  - Good performance for both read and write operations.</para>
/// <para>  - Well-suited for disk-based storage due to minimized I/O operations.</para>
/// <para>- <strong>Cons</strong>:</para>
/// <para>  - More complex to implement compared to simpler data structures.</para>
/// <para>  - Requires rebalancing operations on insertions and deletions, which can be computationally expensive.</para>
/// 
/// <para>2. Hash Table:</para>
/// <para>- <strong>Description</strong>: A data structure that maps keys to values using a hash function to compute 
/// an index into an array of buckets.</para>
/// <para>- <strong>Pros</strong>:</para>
/// <para>  - Very fast lookups on average (O(1) time complexity).</para>
/// <para>  - Simple and straightforward to implement.</para>
/// <para>- <strong>Cons</strong>:</para>
/// <para>  - Does not maintain order, making range queries inefficient.</para>
/// <para>  - Performance can degrade if the hash function results in many collisions or if the table needs to be resized frequently.</para>
/// 
/// <para>3. Skip List:</para>
/// <para>- <strong>Description</strong>: A probabilistic data structure that allows fast search, insertion, and 
/// deletion operations, similar to a balanced tree but easier to implement.</para>
/// <para>- <strong>Pros</strong>:</para>
/// <para>  - Easier to implement than balanced trees.</para>
/// <para>  - Good average-case performance for search, insertion, and deletion (O(log n) time complexity).</para>
/// <para>- <strong>Cons</strong>:</para>
/// <para>  - Performance can degrade in the worst case.</para>
/// <para>  - Requires additional space for the levels of the skip list.</para>
/// 
/// <para>4. Trie (Prefix Tree):</para>
/// <para>- <strong>Description</strong>: A tree-like data structure that stores a dynamic set of strings, where the keys 
/// are usually strings or sequences of characters.</para>
/// <para>- <strong>Pros</strong>:</para>
/// <para>  - Very efficient for prefix-based searches and autocomplete functionality.</para>
/// <para>  - Fast lookups with predictable time complexity (O(m), where m is the length of the key).</para>
/// <para>- <strong>Cons</strong>:</para>
/// <para>  - Can be memory-intensive, especially for large datasets with long common prefixes.</para>
/// <para>  - More complex to implement and manage compared to hash tables.</para>
/// 
/// <para>5. AVL Tree:</para>
/// <para>- <strong>Description</strong>: A self-balancing binary search tree where the difference in heights between 
/// left and right subtrees is at most one.</para>
/// <para>- <strong>Pros</strong>:</para>
/// <para>  - Guarantees O(log n) time complexity for search, insertion, and deletion.</para>
/// <para>  - Ensures the tree remains balanced, improving performance over unbalanced trees.</para>
/// <para>- <strong>Cons</strong>:</para>
/// <para>  - Requires rebalancing on insertions and deletions, which can be computationally expensive.</para>
/// <para>  - More complex to implement compared to simpler data structures.</para>
/// 
/// <para>Each of these algorithms offers different trade-offs in terms of performance, complexity, and suitability 
/// for various use cases. The choice of algorithm will depend on the specific requirements of your application, 
/// such as the need for fast lookups, efficient range queries, memory usage constraints, and ease of implementation.</para>
/// </summary>
/// <typeparam name="TKey">The type of the keys in the storage index.</typeparam>
public interface IStorageIndex<in TKey> where TKey : IComparable<TKey>
{
    /// <summary>
    /// Asynchronously adds or updates the key, offset, and count to the index.
    /// </summary>
    /// <param name="key">The key to save.</param>
    /// <param name="offset">The file offset associated with the key.</param>
    /// <param name="count">The size of the value in bytes associated with the key.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>A task representing the asynchronous add or update operation.</returns>
    Task AddOrUpdateAsync(TKey key, long offset, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves the offset and count for the specified key from the index.
    /// </summary>
    /// <param name="key">The key to retrieve the offset and count for.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>
    /// A task representing the asynchronous retrieve operation. The task result contains a tuple with a boolean indicating 
    /// if the key was found, the offset, and the count. If the key is not found, the boolean is false, and the offset and count are default values.
    /// </returns>
    Task<(bool found, long offset, int count)> RetrieveAsync(TKey key, CancellationToken cancellationToken = default);
}
