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
/// Interface for a red-black tree data structure.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the tree.</typeparam>
/// <typeparam name="TValue">The type of the values in the tree.</typeparam>
public interface IRedBlackTree<TKey, TValue> : IMemTable<TKey, TValue>
{
    /// <summary>
    /// Gets a value indicating whether the red-black tree (MemTable) is full.
    /// </summary>
    /// <value>
    /// <c>true</c> if the red-black tree is full; otherwise, <c>false</c>.
    /// </value>
    /// <remarks>
    /// The IsFull property indicates whether the red-black tree has reached its maximum capacity.
    /// This is particularly useful in the context of an LSM-tree where the MemTable needs to be flushed to disk
    /// as an SSTable when it becomes full. By checking this property, the system can decide when to trigger the flush operation.
    /// </remarks>
    bool IsFull { get; }
}