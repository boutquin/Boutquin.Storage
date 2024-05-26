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
namespace Boutquin.Storage.Infrastructure;

/// <summary>
/// Provides a binary implementation of the <see cref="IEntrySerializer{TKey, TValue}"/> interface.
/// </summary>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <typeparam name="TValue">The type of the value.</typeparam>
public class BinaryEntrySerializer<TKey, TValue> : IEntrySerializer<TKey, TValue>
    where TKey : IComparable<TKey>, ISerializable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var serializer = new BinaryEntrySerializer&lt;MyKey, MyValue&gt;();
    /// using var stream = new MemoryStream();
    /// var key = new MyKey { Id = 1 };
    /// var value = new MyValue { Name = "value1" };
    /// await serializer.WriteEntryAsync(stream, key, value);
    /// </code>
    /// </example>
    public async Task WriteEntryAsync(Stream stream, TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => stream);
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        cancellationToken.ThrowIfCancellationRequested();

        await using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        key.Serialize(writer);
        value.Serialize(writer);
        writer.Flush();
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var serializer = new BinaryEntrySerializer&lt;MyKey, MyValue&gt;();
    /// using var stream = new MemoryStream();
    /// // Assuming the stream has data
    /// var entry = serializer.ReadEntry(stream);
    /// if (entry.HasValue)
    /// {
    ///     var (key, value) = entry.Value;
    ///     Console.WriteLine($"Key: {key}, Value: {value}");
    /// }
    /// </code>
    /// </example>
    public (TKey Key, TValue Value)? ReadEntry(Stream stream)
    {
        Guard.AgainstNullOrDefault(() => stream);

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!CanRead(stream))
        {
            return null;
        }

        var key = TKey.Deserialize(reader);
        var value = TValue.Deserialize(reader);
        return (key, value);
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var serializer = new BinaryEntrySerializer&lt;MyKey, MyValue&gt;();
    /// using var stream = new MemoryStream();
    /// bool canRead = serializer.CanRead(stream);
    /// Console.WriteLine($"Can read: {canRead}");
    /// </code>
    /// </example>
    public bool CanRead(Stream stream)
    {
        Guard.AgainstNullOrDefault(() => stream);

        return stream.Position < stream.Length;
    }
}