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
namespace Boutquin.Storage.Infrastructure.DataStructures;

/// <summary>
/// The Bloom-filter sizing math shared by <see cref="BloomFilter{T}"/> and
/// <see cref="CountingBloomFilter{T}"/>. One implementation, not two: the two filter types
/// advertise identical double-hashing behaviour, and duplicated formulas silently drift (the
/// counting filter once lacked the k-cap, deriving k = 33 where the plain filter derived 30
/// for the same inputs).
/// </summary>
internal static class BloomFilterSizing
{
    /// <summary>
    /// Optimal array size (bits for the plain filter, counters for the counting filter).
    /// </summary>
    /// <remarks>
    /// Derived from the Bloom filter false positive probability formula
    /// p = (1 − e^(−kn/m))^k. Solving for m given n (expected elements) and p (target false
    /// positive rate) yields m = −(n · ln p) / (ln 2)² — the minimum number of slots needed
    /// to achieve the target rate (Bloom 1970).
    /// </remarks>
    internal static int OptimalArraySize(int expectedElements, double falsePositiveProbability)
    {
        var size = Math.Ceiling((-expectedElements * Math.Log(falsePositiveProbability)) / (Math.Log(2) * Math.Log(2)));

        // Guard against int overflow: for large expectedElements + small FPP the required
        // array exceeds Array.MaxLength (2,147,483,591). Casting a large double to int silently
        // overflows to a negative value and produces a cryptic array constructor failure.
        if (size > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedElements),
                expectedElements,
                $"The required filter array size ({size:F0} slots) exceeds Array.MaxLength ({Array.MaxLength}). " +
                $"Reduce expectedElements or increase falsePositiveProbability.");
        }

        return (int)size;
    }

    /// <summary>Optimal number of hash functions for an m-slot array holding n elements.</summary>
    /// <remarks>
    /// k = (m/n) · ln 2 minimizes the false positive probability for given m and n. Capped at
    /// 30: beyond ~30 hash functions the false-positive benefit is negligible while per-element
    /// cost grows linearly (standard implementations use 20–30 as the practical ceiling).
    /// Floored at 1 so degenerate inputs still hash.
    /// </remarks>
    internal static int OptimalHashFunctionCount(int n, int m)
        => Math.Min(30, (int)Math.Max(1, Math.Round((double)m / n * Math.Log(2))));
}
