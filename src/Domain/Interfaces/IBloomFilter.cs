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
/// Represents a Bloom filter, a probabilistic data structure used to test whether an element is a member of a set.
/// 
/// <para>A Bloom filter is highly space-efficient and allows for fast insertions and membership checks.
/// It can report false positives (i.e., it may indicate that an element is in the set when it is not),
/// but it will never report false negatives (i.e., it will never indicate that an element is not in the set when it is).</para>
/// 
/// <para>Typical Uses:</para>
/// <para>In the context of a storage engine, a Bloom filter is often used to reduce the number of disk lookups.
/// Before performing a disk read operation to fetch a value, the Bloom filter can be checked to see if the key likely exists in the dataset.
/// If the Bloom filter indicates that the key does not exist, the costly disk read can be skipped, thereby improving performance.</para>
/// 
/// <para>Implementation Choices:</para>
/// <para>The Bloom filter implementation typically involves the following parameters:
/// <list type="bullet">
/// <item>
/// <description><b>Bit Array Size (m):</b> The number of bits in the Bloom filter. A larger bit array size reduces the probability of false positives but requires more memory.</description>
/// </item>
/// <item>
/// <description><b>Number of Hash Functions (k):</b> The number of different hash functions used. More hash functions reduce the probability of false positives but increase the computational cost of insertions and lookups.</description>
/// </item>
/// </list>
/// </para>
/// 
/// <para>Example:</para>
/// <code>
/// var expectedElements = 1000;
/// var falsePositiveProbability = 0.01; // 1% false positive rate
/// var bloomFilter = new BloomFilter&lt;string&gt;(expectedElements, falsePositiveProbability);
/// 
/// bloomFilter.Add("exampleKey");
/// bool exists = bloomFilter.Contains("exampleKey"); // True
/// </code>
/// </summary>
/// <typeparam name="T">The type of elements to be stored in the Bloom filter.</typeparam>
public interface IBloomFilter<T>
{
    /// <summary>
    /// Adds an element to the Bloom filter.
    /// </summary>
    /// <param name="item">The item to add.</param>
    void Add(T item);

    /// <summary>
    /// Checks if an element is possibly in the Bloom filter.
    /// </summary>
    /// <param name="item">The item to check.</param>
    /// <returns>True if the item is possibly in the filter; false if the item is definitely not in the filter.</returns>
    bool Contains(T item);

    /// <summary>
    /// Clears all elements from the Bloom filter.
    /// </summary>
    void Clear();
}