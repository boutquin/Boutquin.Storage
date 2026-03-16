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
/// Implementation of FNV-1a 32-bit hash algorithm.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why FNV-1a?</b> FNV-1a is the simplest hash function in this library — a single XOR and
/// multiply per byte. It serves as a baseline implementation and is useful for scenarios where hash
/// speed matters more than distribution quality (e.g., quick hash-table lookups on small keys). For
/// Bloom filters, Murmur3 and xxHash32 are preferred due to better avalanche properties.
/// </para>
/// <para>
/// <b>Why FNV-1a (XOR-then-multiply) instead of FNV-1 (multiply-then-XOR)?</b> FNV-1a XORs the byte
/// into the hash before multiplying. This produces better avalanche behavior because the XOR affects
/// more bits before the multiply spreads them. FNV-1 (multiply first) causes the lowest byte to only
/// be mixed by the final XOR, leaving it weakly distributed.
/// </para>
/// <para>
/// <b>Why these specific constants?</b> The FNV prime (16777619) and offset basis (2166136261) are
/// the official 32-bit FNV parameters published by Fowler, Noll, and Vo. The prime was chosen for its
/// dispersion properties with the XOR-fold step, and the offset basis is a non-zero starting point
/// that ensures empty inputs don't hash to zero.
/// </para>
/// </remarks>
public class Fnv1aHash : IHashAlgorithm
{
    // Why: Official 32-bit FNV parameters from Fowler, Noll, and Vo. The prime was chosen for its
    // dispersion properties with XOR-fold, and the offset basis ensures empty inputs don't hash to zero.
    private const uint FnvPrime = 16777619;
    private const uint OffsetBasis = 2166136261;

    /// <summary>
    /// Computes the FNV-1a hash for the given input.
    /// </summary>
    /// <param name="data">The input data as a read-only span of bytes.</param>
    /// <returns>The computed 32-bit hash value.</returns>
    public uint ComputeHash(ReadOnlySpan<byte> data)
    {
        var hash = OffsetBasis; // Initialize hash with the offset basis

        // Why unchecked? The FNV-1a multiply intentionally overflows — the wrapping behavior is
        // part of the hash algorithm's mixing strategy. Without unchecked, C# would throw
        // OverflowException on uint overflow in checked contexts.
        unchecked
        {
            foreach (var b in data)
            {
                hash ^= b; // XOR hash with the current byte
                hash *= FnvPrime; // Multiply hash by the FNV prime
            }
        }

        return hash; // Return the computed hash value
    }
}
