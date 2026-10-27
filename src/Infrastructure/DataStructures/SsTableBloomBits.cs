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
/// Byte-keyed bloom filter backing the v2 SSTable footer. Sized once at write time and
/// persisted in full; a reopen rehydrates the filter from bytes with zero rebuild cost
/// (DDIA 2e pp. 122–123 — persisted bloom filters eliminate the post-restart degraded-read
/// window the v2 design calls out).
/// </summary>
/// <remarks>
/// Uses Murmur3 + XxHash32 double-hashing (same algorithm family as
/// <see cref="Boutquin.Storage.Infrastructure.DataStructures.BloomFilter{T}"/>) so the
/// existing in-memory bloom filter and the persisted on-disk bloom filter agree on
/// contains-semantics. Two constructors: one for writer-side sizing via expected count
/// + false-positive probability, one for reader-side reconstruction from
/// <c>(bitsCount, hashCount, bits)</c>.
/// </remarks>
internal sealed class SsTableBloomBits
{
    // Upper bound on hash function count for the deserialization constructor. A forged
    // hashCount = 2_000_000_000 would make each MightContain call iterate ~2 billion times —
    // a CPU DoS via a crafted SSTable footer. 64 hash functions exceeds any realistic
    // false-positive target (optimal k for p=1e-19 is ~63) while bounding iteration to a
    // constant that never dominates point-lookup latency.
    private const int MaxHashFunctions = 64;

    private readonly byte[] _bits;
    private readonly int _bitsCount;
    private readonly int _hashCount;
    private readonly Murmur3 _h1 = new();
    private readonly XxHash32 _h2 = new();

    public int BitsCount => _bitsCount;
    public int HashCount => _hashCount;
    public ReadOnlySpan<byte> Bits => _bits;

    public SsTableBloomBits(int expectedEntries, double falsePositiveProbability = 0.01)
    {
        if (expectedEntries < 1)
        {
            expectedEntries = 1;
        }

        var requiredBits = Math.Ceiling(-expectedEntries * Math.Log(falsePositiveProbability) / Math.Pow(Math.Log(2), 2));
        if (!double.IsFinite(requiredBits) || requiredBits > int.MaxValue - 7
            || falsePositiveProbability <= 0 || falsePositiveProbability >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(falsePositiveProbability), "Bloom sizing exceeds the supported bit count.");
        }
        _bitsCount = OptimalBits(expectedEntries, falsePositiveProbability);
        _hashCount = OptimalHashes(expectedEntries, _bitsCount);
        if (_hashCount > MaxHashFunctions)
        {
            throw new ArgumentOutOfRangeException(nameof(falsePositiveProbability), "Bloom sizing exceeds 64 hash functions.");
        }
        _bits = new byte[(_bitsCount + 7) >> 3];
    }

    public SsTableBloomBits(int bitsCount, int hashCount, byte[] bits)
    {
        if (bitsCount < 0 || hashCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bitsCount),
                $"Bloom descriptor malformed: bitsCount={bitsCount}, hashCount={hashCount}.");
        }

        if (hashCount > MaxHashFunctions)
        {
            throw new InvalidDataException(
                $"Bloom descriptor malformed: hashCount={hashCount} exceeds maximum {MaxHashFunctions}.");
        }
        var expectedBytes = ((long)bitsCount + 7) >> 3;
        if (bits.Length != expectedBytes)
        {
            throw new ArgumentException(
                $"Bloom bits length {bits.Length} does not match declared bitsCount {bitsCount} (expected {expectedBytes} bytes).",
                nameof(bits));
        }

        _bitsCount = bitsCount;
        _hashCount = hashCount;
        _bits = bits;
    }

    public void Add(ReadOnlySpan<byte> key)
    {
        if (_bitsCount == 0)
        {
            return;
        }

        // The hashers accept ReadOnlySpan<byte> directly — pass the key span through with no
        // per-probe byte[] copy. This is the SSTable point-lookup hot path; a ToArray() here
        // allocated on every Add/MightContain call.
        var h1 = unchecked((uint)_h1.ComputeHash(key));
        var h2 = unchecked((uint)_h2.ComputeHash(key));
        for (var i = 0; i < _hashCount; i++)
        {
            var bitIndex = unchecked((int)((h1 + (uint)i * h2) % (uint)_bitsCount));
            _bits[bitIndex >> 3] |= (byte)(1 << (bitIndex & 7));
        }
    }

    public bool MightContain(ReadOnlySpan<byte> key)
    {
        if (_bitsCount == 0)
        {
            return true;
        }

        // Span-through, no per-probe ToArray() — see Add.
        var h1 = unchecked((uint)_h1.ComputeHash(key));
        var h2 = unchecked((uint)_h2.ComputeHash(key));
        for (var i = 0; i < _hashCount; i++)
        {
            var bitIndex = unchecked((int)((h1 + (uint)i * h2) % (uint)_bitsCount));
            if ((_bits[bitIndex >> 3] & (byte)(1 << (bitIndex & 7))) == 0)
            {
                return false;
            }
        }
        return true;
    }

    private static int OptimalBits(int n, double p)
    {
        return (int)Math.Max(8, Math.Ceiling((-n * Math.Log(p)) / (Math.Log(2) * Math.Log(2))));
    }

    private static int OptimalHashes(int n, int m)
    {
        return (int)Math.Max(1, Math.Round((double)m / n * Math.Log(2)));
    }
}
