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
using System.Numerics;
using System.Security.Cryptography;

namespace Boutquin.Storage.Infrastructure.DataStructures;

/// <summary>
/// A Merkle tree implementation using SHA-256 for hashing.
///
/// <para>The tree is stored as a flat array where the root is at index 0. For a tree with <c>n</c> leaf nodes
/// (padded to the next power of 2), the internal nodes occupy indices <c>0</c> through <c>n - 2</c>, and the
/// leaf nodes occupy indices <c>n - 1</c> through <c>2n - 2</c>.</para>
///
/// <para>When the number of data blocks is not a power of 2, the remaining leaf positions are filled with
/// the SHA-256 hash of an empty byte array to maintain a complete binary tree structure.</para>
///
/// <para><b>Thread safety:</b> This class is not thread-safe. External synchronization is required for concurrent access.</para>
/// </summary>
public sealed class MerkleTree : IMerkleTree
{
    /// <summary>
    /// The flat array of hashes representing the tree. Index 0 is the root.
    /// </summary>
    private byte[][] _tree = [];

    /// <summary>
    /// The number of leaf nodes (always a power of 2).
    /// </summary>
    private int _leafCount;

    /// <summary>
    /// The original number of data blocks before padding.
    /// </summary>
    private int _originalBlockCount;

    /// <inheritdoc />
    public byte[] RootHash => _tree.Length > 0 ? (byte[])_tree[0].Clone() : [];

    /// <inheritdoc />
    public void Build(IReadOnlyList<byte[]> dataBlocks)
    {
        ArgumentNullException.ThrowIfNull(dataBlocks);

        if (dataBlocks.Count == 0)
        {
            throw new ArgumentException("Data blocks cannot be empty.", nameof(dataBlocks));
        }

        for (var i = 0; i < dataBlocks.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(dataBlocks[i], $"dataBlocks[{i}]");
        }

        _originalBlockCount = dataBlocks.Count;

        // Handle single block case: root hash is the hash of the block.
        if (dataBlocks.Count == 1)
        {
            _leafCount = 1;
            _tree = [ComputeHash(dataBlocks[0])];
            return;
        }

        // Pad to the next power of 2.
        _leafCount = NextPowerOf2(dataBlocks.Count);
        var totalNodes = 2 * _leafCount - 1;
        _tree = new byte[totalNodes][];

        var emptyHash = ComputeHash([]);

        // Fill leaf nodes (second half of the array).
        var leafStart = _leafCount - 1;
        for (var i = 0; i < _leafCount; i++)
        {
            _tree[leafStart + i] = i < dataBlocks.Count
                ? ComputeHash(dataBlocks[i])
                : emptyHash;
        }

        // Build internal nodes from bottom to top.
        for (var i = leafStart - 1; i >= 0; i--)
        {
            var leftChild = 2 * i + 1;
            var rightChild = 2 * i + 2;
            _tree[i] = ComputeCombinedHash(_tree[leftChild], _tree[rightChild]);
        }
    }

    /// <inheritdoc />
    public bool Verify(IReadOnlyList<byte[]> dataBlocks)
    {
        ArgumentNullException.ThrowIfNull(dataBlocks);

        if (_tree.Length == 0)
        {
            return false;
        }

        var verificationTree = new MerkleTree();
        verificationTree.Build(dataBlocks);

        return verificationTree.RootHash.AsSpan().SequenceEqual(RootHash);
    }

    /// <inheritdoc />
    public IReadOnlyList<byte[]> GetProof(int leafIndex)
    {
        if (_tree.Length == 0)
        {
            throw new InvalidOperationException("The Merkle tree has not been built. Call Build() first.");
        }

        if (leafIndex < 0 || leafIndex >= _originalBlockCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leafIndex),
                leafIndex,
                $"Leaf index must be between 0 and {_originalBlockCount - 1}.");
        }

        // Single block: no proof needed (but return empty list which is still valid).
        if (_leafCount == 1)
        {
            return [];
        }

        var proof = new List<byte[]>();
        var nodeIndex = _leafCount - 1 + leafIndex; // Convert leaf index to tree array index.

        while (nodeIndex > 0)
        {
            // Sibling index: if even, sibling is index - 1; if odd, sibling is index + 1.
            var siblingIndex = (nodeIndex % 2 == 0) ? nodeIndex - 1 : nodeIndex + 1;
            proof.Add(_tree[siblingIndex]);

            // Move to parent.
            nodeIndex = (nodeIndex - 1) / 2;
        }

        return proof.AsReadOnly();
    }

    /// <inheritdoc />
    public static bool VerifyProof(byte[] leafHash, IReadOnlyList<byte[]> proof, byte[] rootHash, int leafIndex)
    {
        ArgumentNullException.ThrowIfNull(leafHash);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(rootHash);
        ArgumentOutOfRangeException.ThrowIfNegative(leafIndex);

        var computedHash = leafHash;
        var currentIndex = leafIndex;

        foreach (var siblingHash in proof)
        {
            // If current index is even, this node was a left child — place it on the left, sibling on the right.
            // If current index is odd (left child), sibling is on the right.
            computedHash = (currentIndex % 2 == 0)
                ? ComputeCombinedHash(computedHash, siblingHash)
                : ComputeCombinedHash(siblingHash, computedHash);

            currentIndex /= 2;
        }

        return computedHash.AsSpan().SequenceEqual(rootHash);
    }

    /// <summary>
    /// Computes the SHA-256 hash of the given data.
    /// </summary>
    /// <param name="data">The data to hash.</param>
    /// <returns>The SHA-256 hash as a byte array.</returns>
    private static byte[] ComputeHash(byte[] data)
    {
        return SHA256.HashData(data);
    }

    /// <summary>
    /// Computes the SHA-256 hash of the concatenation of two hashes.
    /// </summary>
    /// <param name="left">The left child hash.</param>
    /// <param name="right">The right child hash.</param>
    /// <returns>The combined SHA-256 hash.</returns>
    private static byte[] ComputeCombinedHash(byte[] left, byte[] right)
    {
        Span<byte> combined = stackalloc byte[left.Length + right.Length];
        left.CopyTo(combined);
        right.CopyTo(combined[left.Length..]);
        return SHA256.HashData(combined);
    }

    /// <summary>
    /// Returns the smallest power of 2 that is greater than or equal to the given value.
    /// </summary>
    /// <param name="value">The input value.</param>
    /// <returns>The next power of 2.</returns>
    private static int NextPowerOf2(int value)
    {
        return (int)BitOperations.RoundUpToPowerOf2((uint)value);
    }
}
