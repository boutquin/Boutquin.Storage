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
/// An observed-remove set CRDT. Supports both add and remove operations. Each add is tagged with a unique
/// identifier; remove only removes the tags that have been observed. This means concurrent add and remove
/// of the same element results in the element being present (add wins).
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 5.</para>
/// </summary>
/// <typeparam name="T">The type of elements in the set.</typeparam>
public interface IORSet<T> : ICrdt<IReadOnlySet<T>>
{
    /// <summary>
    /// Adds an element to the set, tagged with the specified node identifier for uniqueness.
    /// </summary>
    /// <param name="nodeId">The identifier of the node performing the add.</param>
    /// <param name="item">The item to add.</param>
    void Add(string nodeId, T item);

    /// <summary>
    /// Removes an element from the set by removing all observed tags for the element.
    /// </summary>
    /// <param name="item">The item to remove.</param>
    void Remove(T item);

    /// <summary>
    /// Checks whether the set contains the specified element.
    /// </summary>
    /// <param name="item">The item to check.</param>
    /// <returns>True if the item has at least one active tag; false otherwise.</returns>
    bool Contains(T item);
}
