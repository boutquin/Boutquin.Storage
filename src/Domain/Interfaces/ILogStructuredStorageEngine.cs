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
/// Provides an interface for a log-structured storage engine with asynchronous operations,
/// building on the basic storage engine capabilities. This interface includes additional
/// methods specific to log-structured storage systems, such as compaction.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the storage engine.</typeparam>
/// <typeparam name="TValue">The type of the values in the storage engine.</typeparam>
/// <remarks>
/// <para><b>Usage and Applications:</b></para>
/// <para>This interface is designed for storage systems that use a log-structured approach
/// to manage data. Log-structured storage engines are optimized for write-heavy workloads
/// and are particularly effective in scenarios where data is frequently appended and updated.</para>
///
/// <para><b>Typical Implementations:</b></para>
/// <para>- **Log-Structured Merge Trees (LSM-trees):** These structures are designed to handle
/// high write throughput by batching and sequentially writing updates, followed by periodic
/// compaction to merge and reorganize data for efficient read access.</para>
///
/// <para><b>Methods:</b></para>
/// <para>- <see cref="IKeyValueStore{TKey, TValue}.SetAsync"/>: Sets or updates the value for a specified key.</para>
/// <para>- <see cref="IKeyValueStore{TKey, TValue}.TryGetValueAsync"/>: Attempts to retrieve the value associated with a specified key.</para>
/// <para>- <see cref="IKeyValueStore{TKey, TValue}.ContainsKeyAsync"/>: Checks whether the store contains the specified key.</para>
/// <para>- <see cref="IKeyValueStore{TKey, TValue}.RemoveAsync"/>: Removes the value associated with the specified key.</para>
/// <para>- <see cref="CompactAsync"/>: Performs compaction to merge and clean up data, improving read performance and space efficiency.</para>
/// </remarks>
public interface ILogStructuredStorageEngine<in TKey, TValue> : IStorageEngine<TKey, TValue> where TKey : IComparable<TKey>
{
    /// <summary>
    /// Performs compaction to merge and clean up data, improving read performance and space efficiency.
    /// Compaction is an I/O-intensive operation that reorganizes and reduces the number of storage files,
    /// eliminating redundant data and reclaiming space.
    /// </summary>
    /// <returns>A task representing the asynchronous compaction operation.</returns>
    /// <exception cref="IOException">Thrown when an I/O error occurs during compaction.</exception>
    Task CompactAsync();
}