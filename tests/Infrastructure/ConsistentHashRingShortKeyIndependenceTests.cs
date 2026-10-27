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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Salt-fold tests for the <see cref="ConsistentHashRing{TNode}"/> composite 64-bit ring key on
/// SHORT (1–3 byte) inputs. The composite key is built from two 32-bit hashes — one over the raw
/// bytes (the high half), one over a salt-XORed copy (the low half). For the two halves to behave
/// like an independent 64-bit hash, the salt fold must mix the FULL 64-bit golden-ratio salt into
/// every input byte. The earlier fold selected salt byte <c>i</c> for input byte <c>i</c>
/// (<c>salt &gt;&gt; ((i &amp; 7) * 8)</c>), so a key shorter than 8 bytes only ever touched the
/// low salt bytes and never the well-mixed high ones — short keys saw a weakly-salted low half.
/// </summary>
/// <remarks>
/// Murmur3's avalanche masks the deficiency at the routing layer (an end-to-end distribution test
/// over short keys cannot distinguish the two folds — both spread acceptably), so the fold's
/// short-key dependence on the full salt is pinned directly here against the reference computation.
/// This is the discriminating evidence per the heuristic-nit decision rule (fix-exactly preferred;
/// the fix carries no routing-behavior change beyond closing the finding).
/// </remarks>
public sealed class ConsistentHashRingShortKeyIndependenceTests
{
    // The golden-ratio salt constant ConsistentHashRing folds into the low-half input.
    private const ulong SecondHashSalt = 0x9E3779B97F4A7C15UL;

    /// <summary>
    /// The low half of the composite key for a short key must equal the configured hash of a
    /// salted copy whose per-position salt byte is derived from the FULL 64-bit salt
    /// (<c>(salt × (i + 1)) &gt;&gt; 56</c>), NOT from salt byte <c>i</c> alone. For a 1-byte key
    /// the two folds pick different salt bytes (full-salt MSB <c>0x9E</c> vs salt byte 0
    /// <c>0x15</c>), so this reference comparison goes RED if the fold reverts to the per-byte
    /// form and stays GREEN only while the full-64-bit fold is in place.
    /// </summary>
    [Theory]
    [InlineData("a")]
    [InlineData("z")]
    [InlineData("ab")]
    [InlineData("xyz")]
    public void ShortKeyLowHalf_UsesFull64BitSaltFold(string key)
    {
        var ring = new ConsistentHashRing<string>(hashAlgorithm: new Murmur3());

        var composite = unchecked((ulong)ring.ComputeHashForTesting(key));
        var actualLow = (uint)(composite & 0xFFFFFFFFUL);

        // Reference low half computed with the full-64-bit salt fold.
        var bytes = System.Text.Encoding.UTF8.GetBytes(key);
        var saltedFullFold = new byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var saltByte = (byte)((SecondHashSalt * (ulong)(i + 1)) >> 56);
            saltedFullFold[i] = (byte)(bytes[i] ^ saltByte);
        }
        var expectedLow = new Murmur3().ComputeHash(saltedFullFold);

        actualLow.Should().Be(expectedLow,
            "the short-key low half must fold the full 64-bit salt (per-position MSB of "
            + "salt × (i+1)); the per-byte salt-index fold would diverge here for short keys");

        // Independence sanity: the salted copy genuinely differs from the raw bytes (the salt is
        // not a no-op), so the low half is not a copy of the high half.
        saltedFullFold.Should().NotEqual(bytes,
            "the salt fold must perturb the input so the two composite halves are distinct");
    }
}
