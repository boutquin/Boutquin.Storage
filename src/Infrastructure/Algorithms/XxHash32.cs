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
/// Implementation of xxHash 32-bit hash algorithm.
/// </summary>
public class XxHash32 : IHashAlgorithm
{
    // Constants for the xxHash algorithm
    private const uint Prime1 = 2654435761U;
    private const uint Prime2 = 2246822519U;
    private const uint Prime3 = 3266489917U;
    private const uint Prime4 = 668265263U;
    private const uint Prime5 = 374761393U;

    /// <summary>
    /// Computes the xxHash for the given input.
    /// </summary>
    /// <param name="data">The input data as a read-only span of bytes.</param>
    /// <returns>The computed 32-bit hash value.</returns>
    public uint ComputeHash(ReadOnlySpan<byte> data)
    {
        int length = data.Length; // Get the length of the input data
        uint hash;

        // If the input length is 16 bytes or more, process in blocks
        if (length >= 16)
        {
            // Initialize hash values
            uint v1 = unchecked(Prime1 + Prime2);
            uint v2 = Prime2;
            uint v3 = 0;
            uint v4 = unchecked((uint)-Prime1);

            // Process each 16-byte block using unsafe code
            unsafe
            {
                fixed (byte* ptr = data)
                {
                    uint* blocksPtr = (uint*)ptr;
                    int blocks = length / 16; // Calculate the number of 16-byte blocks

                    for (int i = 0; i < blocks; i++)
                    {
                        v1 = Round(v1, blocksPtr[i * 4 + 0]);
                        v2 = Round(v2, blocksPtr[i * 4 + 1]);
                        v3 = Round(v3, blocksPtr[i * 4 + 2]);
                        v4 = Round(v4, blocksPtr[i * 4 + 3]);
                    }
                }
            }

            // Combine hash values
            hash = unchecked(RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18));
        }
        else
        {
            hash = Prime5; // Use Prime5 if input is less than 16 bytes
        }

        hash += (uint)length; // Add the length of the input to the hash

        // Process the remaining bytes
        ref byte dataRefByte = ref MemoryMarshal.GetReference(data);
        int remainder = length % 16;
        for (int i = length - remainder; i < length; i++)
        {
            hash ^= unchecked((uint)Unsafe.Add(ref dataRefByte, i) * Prime5);
            hash = unchecked(RotateLeft(hash, 11) * Prime1);
        }

        hash = FMix(hash); // Final mixing of the hash

        return hash; // Return the computed hash value
    }

    /// <summary>
    /// Mixes a block into the hash.
    /// </summary>
    /// <param name="hash">The current hash value.</param>
    /// <param name="input">The input block to mix into the hash.</param>
    /// <returns>The updated hash value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Round(uint hash, uint input)
    {
        hash = unchecked(hash + input * Prime2);
        hash = RotateLeft(hash, 13);
        hash = unchecked(hash * Prime1);
        return hash;
    }

    /// <summary>
    /// Rotates a 32-bit integer left by the specified number of bits.
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
    /// </summary>
    /// <param name="hash">The hash value to mix.</param>
    /// <returns>The final mixed hash value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint FMix(uint hash)
    {
        hash ^= hash >> 15;
        hash = unchecked(hash * Prime2);
        hash ^= hash >> 13;
        hash = unchecked(hash * Prime3);
        hash ^= hash >> 16;
        return hash;
    }
}