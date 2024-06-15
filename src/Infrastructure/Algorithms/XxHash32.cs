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
/// Implements the XXHash32 hashing algorithm.
/// XXHash is a fast, non-cryptographic hash algorithm, working at speeds close to RAM limits.
/// It is highly efficient for short strings and provides a decent distribution and avalanche effect.
/// </summary>
public class XxHash32 : IHashAlgorithm
{
    // Prime values are constants used in the algorithm to ensure the hash has good dispersion.
    // These specific values are chosen based on their properties in relation to the algorithm.
    private const uint Prime1 = 2654435761U;
    private const uint Prime2 = 2246822519U;
    private const uint Prime3 = 3266489917U;
    private const uint Prime4 = 668265263U;
    private const uint Prime5 = 374761393U;

    /// <summary>
    /// Computes the XXHash32 hash for the given input.
    /// </summary>
    /// <param name="data">The input data as a read-only span of bytes.</param>
    /// <returns>The computed 32-bit hash value.</returns>
    public uint ComputeHash(ReadOnlySpan<byte> data)
    {
        var length = data.Length;
        uint hash;

        // If the input is large enough, process it in 16-byte blocks to utilize the algorithm's efficiency.
        if (length >= 16)
        {
            // Initialize variables with prime values. These are part of the algorithm's core calculations.
            var v1 = unchecked(Prime1 + Prime2);
            var v2 = Prime2;
            uint v3 = 0;
            var v4 = unchecked((uint)-Prime1);

            // Cast the byte data to uint for processing 4 bytes at a time.
            var blocks = MemoryMarshal.Cast<byte, uint>(data);
            var blocksCount = length / 16;

            // Process each 16-byte block in 4 uint steps, applying the algorithm's operations.
            for (var i = 0; i < blocksCount * 4; i += 4)
            {
                v1 = Round(v1, blocks[i]);
                v2 = Round(v2, blocks[i + 1]);
                v3 = Round(v3, blocks[i + 2]);
                v4 = Round(v4, blocks[i + 3]);
            }

            // Combine the processed variables to form the basis of the hash.
            hash = unchecked(RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18));
            // Slice the processed data off, leaving any remaining bytes.
            data = data.Slice(blocksCount * 16);
        }
        else
        {
            // For data smaller than 16 bytes, start with a different base hash value.
            hash = Prime5;
        }

        // Add the length of the data to the hash. This ensures that strings of different lengths
        // have a different hash even if they are identical for the first few characters.
        hash += (uint)length;

        // Process the remaining bytes that are less than 16 bytes in the input.
        while (data.Length >= 4)
        {
            // Read 4 bytes at a time, apply the algorithm's operations.
            hash = unchecked(hash + BinaryPrimitives.ReadUInt32LittleEndian(data) * Prime3);
            hash = RotateLeft(hash, 17) * Prime4;
            data = data.Slice(4); // Move to the next 4 bytes.
        }

        // Process any bytes that are left after processing 4-byte blocks.
        foreach (var b in data)
        {
            hash ^= b * Prime5;
            hash = RotateLeft(hash, 11) * Prime1;
        }

        // Final mix of the hash to ensure the avalanche effect, making sure that a small change
        // in input significantly changes the output hash.
        hash = FMix(hash);

        return hash;
    }

    /// <summary>
    /// Performs a single round of the XXHash algorithm on the given hash and input.
    /// This involves a mix of addition, multiplication, and rotation operations.
    /// </summary>
    /// <param name="hash">The current hash value.</param>
    /// <param name="input">The input value to be hashed.</param>
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
    /// Performs a left rotation on the given value.
    /// Bitwise rotation ensures that every bit can influence every other bit.
    /// </summary>
    /// <param name="value">The value to rotate.</param>
    /// <param name="count">The number of bits to rotate by.</param>
    /// <returns>The rotated value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateLeft(uint value, int count)
    {
        return (value << count) | (value >> (32 - count));
    }

    /// <summary>
    /// Final mix function to ensure the avalanche effect on the hash.
    /// This step is crucial to ensure that the hash value has a good distribution,
    /// making it suitable for use in hash tables by minimizing collisions.
    /// </summary>
    /// <param name="hash">The current hash value.</param>
    /// <returns>The final hash value with a good distribution.</returns>
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
