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
/// Finding #18 — the existing hash tests asserted only determinism + non-zero, so a future
/// constant typo that produced a non-standard-but-deterministic hash would pass silently. These
/// pin canonical reference vectors. Likewise the red-black tree had no invariant validation: a
/// regression in the insert fix-up that degraded the tree to O(n) would still pass behavioural
/// sorted-iteration tests. This asserts the structural invariants after randomised insertion.
/// </summary>
public sealed class HashReferenceVectorAndRbInvariantTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    // ───── Canonical hash reference vectors ──────────────────────────────────

    [Fact]
    public void Fnv1a32_Hello_MatchesCanonicalVector()
        => new Fnv1aHash().ComputeHash(Utf8("Hello")).Should().Be(0xF55C314BU);

    // The entire on-disk integrity layer (WAL frames, SSTable blocks, value-log, manifests) rests on
    // this one polynomial constant being Castagnoli (CRC-32C / iSCSI), not IEEE CRC32. The canonical
    // check string "123456789" hashes to 0xE3069283 under Castagnoli and 0xCBF43926 under IEEE, so
    // this single vector catches a polynomial/finalization regression that swapped the two — which
    // every round-trip test would otherwise still pass.
    [Fact]
    public void Crc32C_CheckString_MatchesCastagnoliVector_NotIeee()
    {
        Crc32C.HashToUInt32(Utf8("123456789")).Should().Be(0xE3069283U);
        Crc32C.HashToUInt32(Utf8("123456789")).Should().NotBe(0xCBF43926U, "0xCBF43926 is the IEEE CRC32 value");
    }

    [Fact]
    public void XxHash32_Seed0_MatchesCanonicalVectors()
    {
        var h = new XxHash32();
        h.ComputeHash(ReadOnlySpan<byte>.Empty).Should().Be(0x02CC5D05U);
        h.ComputeHash(Utf8("abc")).Should().Be(0x32D153FFU);
    }

    [Fact]
    public void Murmur3_Hello_MatchesImplementationVector()
    {
        // The implementation seeds with 0xc58f1a7b; this pins its byte-exact output so a future
        // constant/rotation/finalizer typo is caught even though it stays deterministic.
        new Murmur3().ComputeHash(Utf8("Hello")).Should().Be(0xA13FACCCU);
    }

    // ───── Red-black tree invariants after insertion ─────────────────────────

    [Fact]
    public async Task RedBlackTree_RandomisedInsertion_PreservesInvariants()
    {
        var tree = new RedBlackTree<int, int>(maxSize: 2000);
        var rng = new Random(20260609);
        for (var i = 0; i < 1500; i++)
        {
            await tree.SetAsync(rng.Next(1, 100_000), i + 1);
        }

        tree.SatisfiesRedBlackInvariants().Should().BeTrue();
    }

    [Fact]
    public async Task RedBlackTree_SequentialInsertion_StaysBalanced()
    {
        // Ascending keys are the worst case for an unbalanced BST; a correct RB fix-up keeps the
        // black-height balanced and the structure valid.
        var tree = new RedBlackTree<int, int>(maxSize: 1100);
        for (var i = 1; i <= 1000; i++)
        {
            await tree.SetAsync(i, i);
        }

        tree.SatisfiesRedBlackInvariants().Should().BeTrue();
    }
}
