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
namespace Boutquin.Storage.Infrastructure.Serialization;

/// <summary>
/// Provides a binary implementation of the <see cref="IEntrySerializer{TKey, TValue}"/> interface.
/// </summary>
/// <typeparam name="TKey">The type of the key.</typeparam>
/// <typeparam name="TValue">The type of the value.</typeparam>
/// <remarks>
/// <para>
/// <b>Why binary serialization?</b> Binary format is the most compact representation, minimizing
/// disk I/O and storage footprint. Each entry is the key's wire bytes followed by the value's wire
/// bytes, written directly to the caller's stream — no delimiters, headers, or padding. Framing is
/// the responsibility of the key/value wire formats themselves (both are self-delimiting). The
/// trade-off is that the format is not human-readable and is sensitive to changes in serialization
/// order.
/// </para>
/// <para>
/// <b>Stream ownership.</b> The stream is owned by the caller (the storage engine), not by the
/// serializer: the serializer never closes it, so sequential operations (writing multiple entries
/// to the same stream) work without surprises.
/// </para>
/// </remarks>
public sealed class BinaryEntrySerializer<TKey, TValue> : IEntrySerializer<TKey, TValue>
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <inheritdoc/>
    public void WriteEntry(Stream stream, TKey key, TValue value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        key.Serialize(stream);
        value.Serialize(stream);
    }

    /// <inheritdoc/>
    public Task WriteEntryAsync(Stream stream, TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        // The key/value wire formats expose synchronous Serialize only; the async surface here
        // exists for interface symmetry.
        key.Serialize(stream);
        value.Serialize(stream);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public (TKey Key, TValue Value)? ReadEntry(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!CanRead(stream))
        {
            return null;
        }

        // One exception contract across both IEntrySerializer implementations: the CSV sibling
        // wraps every parse fault as DeserializationException, so callers switching serializers
        // never need format-specific corruption handling. A torn entry (crash mid-append) or a
        // corrupt field otherwise leaks whatever the key/value Deserialize happens to throw
        // (EndOfStreamException, InvalidDataException, …).
        try
        {
            var key = TKey.Deserialize(stream);
            var value = TValue.Deserialize(stream);
            return (key, value);
        }
        catch (Exception ex) when (ex is not DeserializationException)
        {
            throw new DeserializationException(
                "Failed to deserialize binary entry — torn or corrupt record.", ex);
        }
    }

    /// <inheritdoc/>
    public bool CanRead(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // The binary wire is not self-delimiting, so end-of-log detection genuinely needs
        // Position/Length. Surface the contract by name rather than leaking the BCL's opaque
        // "Specified method is not supported" from the Position getter.
        if (!stream.CanSeek)
        {
            throw new NotSupportedException(
                "BinaryEntrySerializer requires a seekable stream: the binary wire is not " +
                "self-delimiting, so end-of-log detection compares Position against Length. " +
                "Buffer non-seekable input into a seekable stream first, or use CsvEntrySerializer.");
        }

        return stream.Position < stream.Length;
    }
}
