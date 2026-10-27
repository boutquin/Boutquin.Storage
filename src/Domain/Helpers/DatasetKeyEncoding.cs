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
namespace Boutquin.Storage.Domain.Helpers;

/// <summary>
/// Validates the encoding that identifies a dataset in time-series segments and coverage indexes.
/// </summary>
/// <remarks>
/// Dataset encodings must preserve comparison identity across persistence and reproduce their own
/// bytes. Otherwise two comparer-distinct datasets can share a persisted identity, merging coverage
/// or letting a tombstone delete another dataset's rows. Validation rejects that encoding before
/// its bytes enter a store; comparer-equal datasets with different, stable bytes remain distinct.
/// </remarks>
public static class DatasetKeyEncoding
{
    /// <summary>
    /// Serializes a dataset and verifies that decoding consumes the full encoding, preserves its
    /// comparison identity and reproduces exactly the same bytes when encoded again.
    /// </summary>
    /// <typeparam name="TKey">The comparable, serializable dataset key. Its comparison must be a
    /// total order; its decoder must reconstruct the encoded dataset without losing state.</typeparam>
    /// <param name="key">The dataset whose encoding will be used for storage or lookup.</param>
    /// <returns>A fresh byte array containing the validated encoding.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">The encoding cannot be fully decoded, changes comparison
    /// identity, or changes bytes when its decoded value is serialized again.</exception>
    /// <remarks>Validation uses O(n) time and temporary space for an n-byte encoding, in addition to
    /// the key's own comparison and serialization costs. The caller's key is never canonicalized.</remarks>
    public static byte[] SerializeValidated<TKey>(TKey key)
        where TKey : IComparable<TKey>, ISerializable<TKey>
    {
        ArgumentNullException.ThrowIfNull(key);
        using var encoded = new MemoryStream();
        key.Serialize(encoded);
        var bytes = encoded.ToArray();
        using var reader = new MemoryStream(bytes, writable: false);
        TKey restored;
        try
        {
            restored = TKey.Deserialize(reader);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            throw new ArgumentException("The dataset key's serialized bytes cannot be decoded.", nameof(key), ex);
        }

        if (restored is null || reader.Position != reader.Length
            || key.CompareTo(restored) != 0 || restored.CompareTo(key) != 0)
        {
            throw new ArgumentException(
                "The dataset key's encoding must be fully consumed and preserve comparison identity after deserialization.", nameof(key));
        }

        using var reencoded = new MemoryStream();
        restored.Serialize(reencoded);
        if (!bytes.AsSpan().SequenceEqual(reencoded.GetBuffer().AsSpan(0, checked((int)reencoded.Length))))
        {
            throw new ArgumentException(
                "The dataset key's encoding must reproduce identical bytes after deserialization.", nameof(key));
        }
        return bytes;
    }
}
