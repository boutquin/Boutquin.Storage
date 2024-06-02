﻿// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
/// Interface for a segmented storage engine that supports appending, bulk operations, and compaction.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
public interface ILogSegmentedStorageEngine<TKey, TValue> : 
    ICompactableBulkStorageEngine<TKey, TValue>
        where TKey : ISerializable<TKey>, IComparable<TKey>, new()
        where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Sets a key-value pair in the storage engine, creating a new segment if necessary.
    /// </summary>
    /// <param name="key">The key to set.</param>
    /// <param name="value">The value to set.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the entry serializer used by the storage engine.
    /// </summary>
    IEntrySerializer<TKey, TValue> EntrySerializer { get; }
}