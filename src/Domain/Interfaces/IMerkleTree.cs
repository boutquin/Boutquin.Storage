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
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Represents a Merkle tree, a hash-based data structure used to efficiently verify the integrity of data.
///
/// <para>A Merkle tree is a binary tree of hashes where each leaf node contains the hash of a data block,
/// and each internal node contains the hash of its two child nodes. The root hash (Merkle root) provides
/// a single compact digest that represents the integrity of all data blocks.</para>
///
/// <para><b>Typical Uses:</b></para>
/// <para>Merkle trees are widely used in distributed systems, blockchain technology, and storage engines
/// to verify data integrity efficiently. They enable logarithmic-time proof generation and verification,
/// allowing a client to confirm that a specific data block belongs to a dataset without downloading the
/// entire dataset.</para>
///
/// <para><b>Key Operations:</b></para>
/// <list type="bullet">
/// <item>
/// <description><b>Build:</b> Constructs the tree from a list of data blocks. Data blocks that do not
/// form a perfect power-of-2 count are padded with empty hashes.</description>
/// </item>
/// <item>
/// <description><b>Verify:</b> Rebuilds the tree from provided data blocks and compares the resulting
/// root hash against the stored root hash.</description>
/// </item>
/// <item>
/// <description><b>GetProof:</b> Returns the sibling hashes along the path from a leaf to the root
/// (an audit proof), enabling verification of a single leaf's inclusion.</description>
/// </item>
/// <item>
/// <description><b>VerifyProof:</b> Statically verifies a single leaf's inclusion in the tree using
/// the audit proof and the root hash.</description>
/// </item>
/// </list>
///
/// <para><b>Complexity (where n = number of data blocks):</b></para>
/// <para>- <b>Build:</b> O(n) — hashes each leaf then computes O(n) internal nodes.</para>
/// <para>- <b>Verify:</b> O(n) — rebuilds the tree and compares root hashes.</para>
/// <para>- <b>GetProof:</b> O(log n) — returns the sibling hashes along the path from leaf to root.</para>
/// <para>- <b>VerifyProof:</b> O(log n) — recomputes hashes along the proof path.</para>
/// <para>- <b>Space:</b> O(n) — stores 2n - 1 hash nodes.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 5 — "Replication",
/// section on anti-entropy and Merkle trees. Dynamo-style databases use Merkle trees to detect inconsistencies
/// between replicas by comparing root hashes, then drilling down to identify differing data blocks.</para>
/// </summary>
public interface IMerkleTree
{
    /// <summary>
    /// Gets the root hash of the Merkle tree.
    /// </summary>
    /// <value>
    /// A byte array containing the root hash, or an empty array if the tree has not been built.
    /// </value>
    byte[] RootHash { get; }

    /// <summary>
    /// Builds the Merkle tree from the provided data blocks.
    /// </summary>
    /// <param name="dataBlocks">The data blocks to build the tree from.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="dataBlocks"/> is empty.</exception>
    void Build(IReadOnlyList<byte[]> dataBlocks);

    /// <summary>
    /// Verifies the integrity of the provided data blocks by rebuilding the tree
    /// and comparing the resulting root hash against the stored root hash.
    /// </summary>
    /// <param name="dataBlocks">The data blocks to verify.</param>
    /// <returns><c>true</c> if the root hash matches; otherwise, <c>false</c>.</returns>
    bool Verify(IReadOnlyList<byte[]> dataBlocks);

    /// <summary>
    /// Gets the audit proof (sibling hashes along the path from leaf to root) for the specified leaf index.
    /// </summary>
    /// <param name="leafIndex">The zero-based index of the leaf to get the proof for.</param>
    /// <returns>A read-only list of sibling hashes forming the proof.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the tree has not been built.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="leafIndex"/> is out of range.</exception>
    IReadOnlyList<byte[]> GetProof(int leafIndex);

    /// <summary>
    /// Verifies a single leaf's inclusion in the Merkle tree using the provided audit proof and root hash.
    /// </summary>
    /// <param name="leafHash">The hash of the leaf to verify.</param>
    /// <param name="proof">The audit proof (sibling hashes).</param>
    /// <param name="rootHash">The expected root hash.</param>
    /// <param name="leafIndex">The zero-based index of the leaf in the tree.</param>
    /// <returns><c>true</c> if the proof is valid; otherwise, <c>false</c>.</returns>
    static abstract bool VerifyProof(byte[] leafHash, IReadOnlyList<byte[]> proof, byte[] rootHash, int leafIndex);
}
