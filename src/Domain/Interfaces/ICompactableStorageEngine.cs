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
/// Provides an interface for a storage engine that supports data compaction with asynchronous operations.
/// This interface extends the functionality of storage engines by including a method for compacting data,
/// which can help optimize storage usage and improve performance.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the storage engine.</typeparam>
/// <typeparam name="TValue">The type of the values in the storage engine.</typeparam>
/// <remarks>
/// <para><b>Usage and Applications:</b></para>
/// <para>This interface is designed for storage solutions that benefit from periodic data compaction.
/// Implementations can vary widely and may include features such as reclaiming space, merging data,
/// and optimizing data layout for improved performance.</para>
///
/// <para><b>Typical Implementations:</b></para>
/// <para>- **Log-Structured Storage Engines:** Perform compaction to merge and clean up old data segments,
/// reducing fragmentation and improving write performance.</para>
/// <para>- **Page-Oriented Storage Engines:** Compact data within fixed-size pages to reduce fragmentation
/// and maintain efficient random access performance.</para>
///
/// <para><b>Methods:</b></para>
/// <para>- <see cref="CompactAsync"/>: Compacts the data in the storage engine to optimize space and performance.</para>
/// </remarks>
public interface ICompactableStorageEngine<TKey, TValue>
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Compacts the data in the storage engine to optimize space and performance.
    /// This operation may involve merging data, reclaiming space, and optimizing data layout.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous compaction operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
    Task CompactAsync(CancellationToken cancellationToken = default);
}