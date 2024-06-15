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
namespace Boutquin.Storage.Infrastructure.Algorithms;

/// <summary>
/// Implementation of Murmur3 32-bit hash algorithm.
/// MurmurHash is a non-cryptographic hash function suitable for general hash-based lookup.
/// It offers a good compromise between hash quality and performance.
/// </summary>
public class Murmur3 : IHashAlgorithm
{
    private const uint Seed = 0xc58f1a7b; // Seed value for the Murmur3 hash, can be any constant.

    /// <summary>
    /// Computes the Murmur3 hash for the given input.
    /// </summary>
    /// <param name="data">The input data as a read-only span of bytes.</param>
    /// <returns>The computed 32-bit hash value.</returns>
    public uint ComputeHash(ReadOnlySpan<byte> data)
    {
        var hash = Seed; // Initialize hash with the seed value.
        var length = data.Length; // Get the length of the input data.
        var remainder = length & 3; // Calculate the remainder of the data length divided by 4.
        var blocks = length >> 2; // Calculate the number of 4-byte blocks.

        // Process each 4-byte block.
        for (var i = 0; i < blocks; i++)
        {
            var k = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4, 4)); // Read 4 bytes as uint.
            k *= 0xcc9e2d51; // Mix the block with the first constant.
            k = RotateLeft(k, 15); // Rotate left by 15 bits.
            k *= 0x1b873593; // Mix the block with the second constant.
            hash ^= k; // XOR the hash with the mixed block.
            hash = RotateLeft(hash, 13); // Rotate left by 13 bits.
            hash = hash * 5 + 0xe6546b64; // Mix the hash with constants.
        }

        // Process the remaining bytes.
        if (remainder > 0)
        {
            uint k1 = 0;
            for (var i = length - remainder; i < length; i++)
            {
                k1 ^= (uint)data[i] << ((i & 3) << 3); // Combine remaining bytes into k1.
            }
            k1 *= 0xcc9e2d51; // Mix k1 with the first constant.
            k1 = RotateLeft(k1, 15); // Rotate left by 15 bits.
            k1 *= 0x1b873593; // Mix k1 with the second constant.
            hash ^= k1; // XOR the hash with k1.
        }

        hash ^= (uint)length; // XOR the hash with the length of the input data.
        hash = FMix(hash); // Final mixing of the hash.

        return hash; // Return the computed hash value.
    }

    /// <summary>
    /// Rotates a 32-bit integer left by the specified number of bits.
    /// Bitwise rotation ensures that every bit can influence every other bit, contributing to the hash's avalanche effect.
    /// </summary>
    /// <param name="value">The value to rotate.</param>
    /// <param name="count">The number of bits to rotate left.</param>
    /// <returns>The rotated value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateLeft(uint value, int count)
    {
        return (value << count) | (value >> (32 - count));
    }

    /// <summary>
    /// Final mixing step of the hash.
    /// This step is crucial to ensure that the hash value has a good distribution,
    /// making it suitable for use in hash tables by minimizing collisions.
    /// </summary>
    /// <param name="hash">The hash value to mix.</param>
    /// <returns>The final mixed hash value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint FMix(uint hash)
    {
        hash ^= hash >> 16;
        hash *= 0x85ebca6b;
        hash ^= hash >> 13;
        hash *= 0xc2b2ae35;
        hash ^= hash >> 16;
        return hash;
    }
}
