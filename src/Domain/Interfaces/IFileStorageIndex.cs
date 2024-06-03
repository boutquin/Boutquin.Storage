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
/// Defines an interface for a file storage index that supports key-value operations,
/// where the value represents the location of an entry in a file.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the index.</typeparam>
/// <remarks>
/// <para>The <see cref="IFileStorageIndex{TKey}"/> interface extends the <see cref="IStorageIndex{TKey, TValue}"/> interface,
/// providing functionality specific to managing file locations. The index stores key-value pairs, where the value is of type <see cref="FileLocation"/>,
/// representing the offset and length of an entry in a file. This allows for efficient retrieval of entries by reducing the amount of file I/O required.</para>
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
/// <para>This interface is typically implemented by classes that need to provide fast access to file-based data, such as append-only file storage engines.
/// Implementations can leverage in-memory data structures like <see cref="SortedDictionary{TKey, TValue}"/> for efficient lookups,
/// insertions, and deletions.</para>
/// </remarks>
public interface IFileStorageIndex<in TKey> : 
    IStorageIndex<TKey, FileLocation> where TKey : IComparable<TKey>
{
    // Additional methods specific to the file storage index can be added here.
}