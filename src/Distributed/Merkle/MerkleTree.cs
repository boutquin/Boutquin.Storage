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
namespace Boutquin.Storage.Distributed.Merkle;

/// <summary>
/// A Merkle tree implementation using SHA-256 for hashing.
///
/// <para>The tree is stored as a flat array where the root is at index 0. For a tree with <c>n</c> leaf nodes
/// (padded to the next power of 2), the internal nodes occupy indices <c>0</c> through <c>n - 2</c>, and the
/// leaf nodes occupy indices <c>n - 1</c> through <c>2n - 2</c>.</para>
///
/// <para><b>Domain separation (RFC 6962 §2.1).</b> Leaf hashes are <c>SHA-256(0x00 ‖ data)</c>,
/// internal-node hashes are <c>SHA-256(0x01 ‖ left ‖ right)</c>, and padding leaves use the
/// dedicated constant <c>SHA-256(0x02)</c>. The distinct prefixes keep the three hash domains
/// disjoint, so a crafted data block cannot impersonate an internal node (a 64-byte block equal
/// to <c>left ‖ right</c> no longer collides with the node above those children) and a padding
/// leaf cannot collide with a real — even empty — data block. Without them, "equal root" would
/// not imply "equal data", which is the entire invariant anti-entropy relies on.</para>
///
/// <para>When the number of data blocks is not a power of 2, the remaining leaf positions are filled with
/// the padding-domain constant <c>SHA-256(0x02)</c> to maintain a complete binary tree structure.</para>
///
/// <para><b>Thread safety:</b> This class is not thread-safe. External synchronization is required for concurrent access.</para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. The Merkle-tree hash structure
/// is implemented for educational fidelity to DDIA 2e Ch. 6 (anti-entropy), but production-grade extensions
/// — incremental / streaming hashing and sparse-diff range queries for large trees — are not included. For production use, prefer the Merkle implementations of Cassandra / DynamoDB or a dedicated anti-entropy library.
/// </para>
/// </remarks>
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

        // Handle single block case: root hash is the leaf hash of the block.
        if (dataBlocks.Count == 1)
        {
            _leafCount = 1;
            _tree = [HashLeaf(dataBlocks[0])];
            return;
        }

        // Pad to the next power of 2.
        _leafCount = NextPowerOf2(dataBlocks.Count);
        var totalNodes = 2 * _leafCount - 1;
        _tree = new byte[totalNodes][];

        var paddingHash = ComputePaddingHash();

        // Fill leaf nodes (second half of the array).
        var leafStart = _leafCount - 1;
        for (var i = 0; i < _leafCount; i++)
        {
            _tree[leafStart + i] = i < dataBlocks.Count
                ? HashLeaf(dataBlocks[i])
                : paddingHash;
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
            // Defensive copy — same convention as RootHash: returning the internal array lets a
            // caller's mutation corrupt the tree in place, and the padding hash is ONE shared
            // array across all padded leaves, so a single mutation would corrupt every padded
            // position at once.
            proof.Add((byte[])_tree[siblingIndex].Clone());

            // Move to parent.
            nodeIndex = (nodeIndex - 1) / 2;
        }

        return proof.AsReadOnly();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Position binding is the caller's responsibility.</b> Verification consumes
    /// <paramref name="leafIndex"/> only for left/right parity at each level (RFC 6962-style
    /// audit paths additionally bind the tree size; this reference implementation does not),
    /// so two indices with identical parity chains — e.g. 0 and 2 in a two-leaf tree —
    /// validate the same proof. Domain separation still prevents cross-domain forgery (a leaf
    /// can never impersonate an internal node), so DATA integrity holds; only the claimed
    /// position is unauthenticated. Callers must supply the true index.
    /// </remarks>
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

    // RFC 6962 §2.1 domain-separation prefixes: leaves, internal nodes, and padding hash in
    // disjoint domains so no value from one domain can impersonate a value from another.
    private const byte LeafDomainPrefix = 0x00;
    private const byte InternalDomainPrefix = 0x01;
    private const byte PaddingDomainPrefix = 0x02;

    /// <summary>
    /// Computes the leaf-domain hash of a data block: <c>SHA-256(0x00 ‖ data)</c>
    /// (RFC 6962 §2.1 domain separation). This is the hash callers pass to
    /// <see cref="VerifyProof"/> as the <c>leafHash</c> argument.
    /// </summary>
    /// <param name="data">The data block to hash.</param>
    /// <returns>The 32-byte leaf hash.</returns>
    public static byte[] HashLeaf(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var buffer = new byte[data.Length + 1];
        buffer[0] = LeafDomainPrefix;
        data.CopyTo(buffer.AsSpan(1));
        return SHA256.HashData(buffer);
    }

    /// <summary>
    /// Computes the internal-node-domain hash of two child hashes:
    /// <c>SHA-256(0x01 ‖ left ‖ right)</c> (RFC 6962 §2.1 domain separation).
    /// </summary>
    /// <param name="left">The left child hash.</param>
    /// <param name="right">The right child hash.</param>
    /// <returns>The combined SHA-256 hash.</returns>
    private static byte[] ComputeCombinedHash(byte[] left, byte[] right)
    {
        // left/right reach here from caller-supplied proof sibling hashes (public static
        // VerifyProof), so their lengths are attacker-controllable. An unbounded
        // stackalloc over them overflows the stack and crashes the process uncatchably.
        // Bound the buffer: stackalloc only below a small threshold, otherwise rent from the
        // pooled heap. Mirrors the OrderedByteHelpers UTF-8 buffer-sizing idiom.
        var combinedLength = 1 + left.Length + right.Length;
        byte[]? rented = null;
        Span<byte> combined = combinedLength <= StackallocThreshold
            ? stackalloc byte[combinedLength]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(combinedLength)).AsSpan(0, combinedLength);
        try
        {
            combined[0] = InternalDomainPrefix;
            left.CopyTo(combined[1..]);
            right.CopyTo(combined[(1 + left.Length)..]);
            return SHA256.HashData(combined);
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    // A combined buffer of two 32-byte SHA-256 hashes plus the 1-byte domain prefix is 65 bytes,
    // so honest internal-node hashing always takes the stackalloc fast path. The threshold caps
    // the on-stack size so a caller-supplied oversized sibling hash routes to the pooled heap.
    private const int StackallocThreshold = 256;

    /// <summary>
    /// The padding-domain constant <c>SHA-256(0x02)</c> used to fill leaf positions beyond the
    /// original block count. Lives in its own domain so it can never collide with the leaf hash
    /// of a real data block — not even an empty one.
    /// </summary>
    private static byte[] ComputePaddingHash()
    {
        return SHA256.HashData([PaddingDomainPrefix]);
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
