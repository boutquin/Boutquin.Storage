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
/// A grow-only set CRDT. Elements can be added but never removed. Merge is set union.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 5.</para>
/// </summary>
/// <typeparam name="T">The type of elements in the set.</typeparam>
public interface IGSet<T> : ICrdt<IReadOnlySet<T>>
{
    /// <summary>
    /// Adds an element to the set.
    /// </summary>
    /// <param name="item">The item to add.</param>
    void Add(T item);

    /// <summary>
    /// Checks whether the set contains the specified element.
    /// </summary>
    /// <param name="item">The item to check.</param>
    /// <returns>True if the item is in the set; false otherwise.</returns>
    bool Contains(T item);
}
