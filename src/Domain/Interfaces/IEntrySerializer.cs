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
/// Defines an interface for serializing and deserializing key-value entries.
/// </summary>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <typeparam name="TValue">The type of the value.</typeparam>
public interface IEntrySerializer<TKey, TValue>
    where TKey : IComparable<TKey>, ISerializable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Asynchronously writes a key-value entry to the specified stream.
    /// </summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if the stream, key, or value is null.</exception>
    /// <exception cref="ArgumentException">Thrown if the key or value is the default value.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
    Task WriteEntryAsync(Stream stream, TKey key, TValue value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a key-value entry from the specified stream.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <returns>A tuple containing the key and value, or null if no more entries can be read.</returns>
    /// <exception cref="ArgumentNullException">Thrown if the stream is null.</exception>
    (TKey Key, TValue Value)? ReadEntry(Stream stream);

    /// <summary>
    /// Determines whether the specified stream can be read.
    /// </summary>
    /// <param name="stream">The stream to check.</param>
    /// <returns>True if the stream can be read; otherwise, false.</returns>
    /// <exception cref="ArgumentNullException">Thrown if the stream is null.</exception>
    bool CanRead(Stream stream);
}