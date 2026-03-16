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
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Represents a counting Bloom filter, an extension of the standard Bloom filter that supports element removal.
///
/// <para>A counting Bloom filter replaces the single-bit positions in a standard Bloom filter with counters.
/// Each position maintains a count of how many elements have been hashed to that location. This enables
/// removal of elements (by decrementing counters) without causing false negatives for other elements.</para>
///
/// <para><b>Trade-offs vs standard Bloom filter:</b></para>
/// <para>- <b>Memory:</b> Uses an int[] (32 bits per position) instead of a BitArray (1 bit per position),
///   so memory usage is ~32x higher for the same number of positions.</para>
/// <para>- <b>Removal:</b> Supports removal at the cost of higher memory. Standard Bloom filters cannot
///   remove elements because clearing a bit may affect other elements that hash to the same position.</para>
/// <para>- <b>Counter overflow:</b> Extremely unlikely in practice — requires 2^31 distinct elements hashing
///   to the same position — but implementations should floor counters at 0 on removal.</para>
///
/// <para><b>Complexity (where m = counter array size, k = number of hash functions):</b></para>
/// <para>- <b>Add:</b> O(k) — computes k hash positions and increments k counters.</para>
/// <para>- <b>Remove:</b> O(k) — computes k hash positions and decrements k counters.</para>
/// <para>- <b>Contains:</b> O(k) — computes k hash positions and checks k counters &gt; 0.</para>
/// <para>- <b>GetCount:</b> O(k) — computes k hash positions and returns the minimum counter value.</para>
/// <para>- <b>Clear:</b> O(m) — resets all counters to 0.</para>
/// <para>- <b>Space:</b> O(m) — the counter array dominates memory usage.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 3 — "Storage and Retrieval",
/// section on LSM-tree optimizations. Counting Bloom filters extend the standard Bloom filter concept to support
/// deletion, useful in scenarios where keys are removed from the dataset and the filter must reflect those removals.</para>
/// </summary>
/// <typeparam name="T">The type of elements to be stored in the counting Bloom filter.</typeparam>
public interface ICountingBloomFilter<T> : IBloomFilter<T>
{
    /// <summary>
    /// Removes an element from the counting Bloom filter by decrementing the counters at each hash position.
    /// Counters are floored at 0 to prevent underflow.
    /// </summary>
    /// <param name="item">The item to remove.</param>
    void Remove(T item);

    /// <summary>
    /// Returns the estimated count of how many times the element has been added.
    /// This is the minimum value across all counter positions for the element.
    /// </summary>
    /// <param name="item">The item to query.</param>
    /// <returns>The minimum counter value across all hash positions for the item.</returns>
    int GetCount(T item);
}
