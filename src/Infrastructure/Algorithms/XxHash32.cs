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
namespace Boutquin.Storage.Infrastructure.Algorithms;

/// <summary>
/// Implements the XXHash32 hashing algorithm.
/// XXHash is a fast, non-cryptographic hash algorithm, working at speeds close to RAM limits.
/// It is highly efficient for short strings and provides a decent distribution and avalanche effect.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why xxHash32?</b> Used as the second hash function in the Bloom filter's double-hashing scheme.
/// xxHash was chosen because it is one of the fastest non-cryptographic hash functions available
/// (operates near RAM bandwidth limits) and produces a different hash distribution than Murmur3 —
/// critical for double hashing where the two hash functions must be independent.
/// </para>
/// <para>
/// <b>Why these specific Prime constants?</b> These are Yann Collet's original xxHash constants,
/// selected through extensive testing to maximize avalanche quality. Prime1 through Prime5 each serve
/// a specific role: Prime1 and Prime2 are used in the main accumulation rounds, Prime3 and Prime4
/// handle remaining bytes, and Prime5 is the small-input seed. They must not be changed without
/// re-validating hash quality.
/// </para>
/// <para>
/// <b>Why 16-byte block processing?</b> xxHash processes input in 16-byte stripes using 4 independent
/// accumulators (v1-v4). This enables instruction-level parallelism on modern CPUs — the 4
/// multiply-rotate chains can execute concurrently in the CPU pipeline, achieving throughput close to
/// memory bandwidth.
/// </para>
/// <para>
/// <b>Why <c>unchecked</c> arithmetic?</b> The algorithm intentionally relies on unsigned integer
/// overflow for mixing. C# checks for overflow by default, which would throw exceptions. The
/// <c>unchecked</c> context allows the wrapping behavior that the algorithm requires.
/// </para>
/// <para>
/// <b>Thread safety:</b> This class is thread-safe. <see cref="ComputeHash"/> uses only local variables
/// and has no mutable instance state, so it can be called concurrently from multiple threads without
/// synchronization.
/// </para>
///
/// <para>
/// <b>Reference:</b> Yann Collet, xxHash specification (2012). See also Kleppmann, <i>Designing Data-Intensive
/// Applications</i> (O'Reilly, 2017), Ch. 3 — hash functions are used in Bloom filters and hash indexes
/// to map keys to positions.
/// </para>
///
/// <para>
/// <b>Why different initialization for small vs large inputs?</b> Inputs &lt; 16 bytes cannot fill
/// all 4 accumulators, so they use a simplified path starting from Prime5. This avoids wasting cycles
/// on accumulator initialization when there's nothing to accumulate.
/// </para>
/// </remarks>
public class XxHash32 : IHashAlgorithm
{
    // Why: These are Yann Collet's original xxHash constants, each serving a specific role in the
    // algorithm. Prime1+Prime2 drive the main accumulation rounds, Prime3+Prime4 handle remaining
    // bytes, and Prime5 seeds the small-input path. Do not change without re-validating hash quality.
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

        // Why: Inputs >= 16 bytes use 4 independent accumulators for instruction-level parallelism.
        // Inputs < 16 bytes take a simplified path starting from Prime5 (see else branch below).
        if (length >= 16)
        {
            // Initialize variables with prime values. These are part of the algorithm's core calculations.
            var v1 = unchecked(Prime1 + Prime2);
            var v2 = Prime2;
            uint v3 = 0;
            var v4 = unchecked((uint)-Prime1);

            var blocksCount = length / 16;

            // Why BinaryPrimitives instead of MemoryMarshal.Cast? MemoryMarshal.Cast<byte, uint>
            // requires 4-byte alignment, which isn't guaranteed for arbitrary byte spans (e.g.,
            // slices starting at odd offsets). BinaryPrimitives handles unaligned reads safely.
            for (var i = 0; i < blocksCount; i++)
            {
                var offset = i * 16;
                v1 = Round(v1, BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset)));
                v2 = Round(v2, BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4)));
                v3 = Round(v3, BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 8)));
                v4 = Round(v4, BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 12)));
            }

            // Combine the processed variables to form the basis of the hash.
            hash = unchecked(RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18));
            // Slice the processed data off, leaving any remaining bytes.
            data = data.Slice(blocksCount * 16);
        }
        else
        {
            // Why: Inputs < 16 bytes cannot fill all 4 accumulators, so they skip the main
            // accumulation loop and start from Prime5 to avoid wasting cycles on initialization.
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
            hash = unchecked(hash + b * Prime5);
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
        return System.Numerics.BitOperations.RotateLeft(value, count);
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
