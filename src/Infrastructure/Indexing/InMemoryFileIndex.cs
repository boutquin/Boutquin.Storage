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
namespace Boutquin.Storage.Infrastructure.Indexing;

/// <summary>
/// Provides an in-memory implementation of the <see cref="IFileStorageIndex{TKey}"/> interface
/// using a sorted dictionary for efficient lookups, insertions, and deletions.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the index.</typeparam>
/// <remarks>
/// <para>This class provides an in-memory storage index for managing file locations efficiently.
/// It uses a <see cref="SortedDictionary{TKey, TValue}"/> to maintain the key-value pairs,
/// where the value is a <see cref="FileLocation"/> representing the offset and length of an entry in a file.
/// The sorted dictionary ensures that keys are maintained in a sorted order, providing O(log n) time complexity for insertion,
/// deletion, and lookup operations.</para>
///
/// <para>Example usage:</para>
/// <code>
/// public readonly record struct Key(long Value) : IComparable&lt;Key&gt;
/// {
///     public int CompareTo(Key other)
///     {
///         return Value.CompareTo(other.Value);
///     }
/// }
///
/// var index = new InMemoryFileIndex&lt;Key&gt;();
///
/// // Add a file location to the index
/// var fileLocation = new FileLocation(offset: 1024, count: 128);
/// await index.SetAsync(new Key(123456), fileLocation);
///
/// // Retrieve the file location from the index
/// var (location, found) = await index.TryGetValueAsync(new Key(123456));
/// if (found)
/// {
///     Console.WriteLine($"Offset: {location.Offset}, Count: {location.Count}");
/// }
///
/// // Check if a key exists in the index
/// bool exists = await index.ContainsKeyAsync(new Key(123456));
/// Console.WriteLine($"Key exists: {exists}");
///
/// // Remove a key from the index
/// await index.RemoveAsync(new Key(123456));
/// </code>
///
/// <para>This class is designed for scenarios where fast in-memory access to file locations is required, such as in
/// append-only file storage engines. The use of a sorted dictionary provides efficient and scalable performance
/// for managing a large number of keys and their associated file locations.</para>
/// </remarks>
public sealed class InMemoryFileIndex<TKey> :
    InMemoryStorageIndex<TKey, FileLocation>, IFileStorageIndex<TKey> where TKey : IComparable<TKey>;