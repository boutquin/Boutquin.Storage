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
/// Implementation of FNV-1a 32-bit hash algorithm.
/// </summary>
public class Fnv1aHash : IHashAlgorithm
{
    // Constants for the FNV-1a hash algorithm
    private const uint FnvPrime = 16777619;
    private const uint OffsetBasis = 2166136261;

    /// <summary>
    /// Computes the FNV-1a hash for the given input.
    /// </summary>
    /// <param name="data">The input data as a read-only span of bytes.</param>
    /// <returns>The computed 32-bit hash value.</returns>
    public uint ComputeHash(ReadOnlySpan<byte> data)
    {
        uint hash = OffsetBasis; // Initialize hash with the offset basis

        // Process each byte in the input data
        for (int i = 0; i < data.Length; i++)
        {
            hash ^= data[i]; // XOR hash with the current byte
            hash *= FnvPrime; // Multiply hash by the FNV prime
        }

        return hash; // Return the computed hash value
    }
}