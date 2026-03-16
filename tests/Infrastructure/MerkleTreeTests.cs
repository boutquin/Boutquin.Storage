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
#nullable disable

namespace Boutquin.Storage.Infrastructure.Tests;

using System.Security.Cryptography;

/// <summary>
/// This class contains unit tests for the MerkleTree class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class MerkleTreeTests
{
    /// <summary>
    /// Helper to create data blocks from string values.
    /// </summary>
    private static List<byte[]> CreateBlocks(params string[] values)
    {
        return values.Select(Encoding.UTF8.GetBytes).ToList();
    }

    /// <summary>
    /// Helper to compute SHA-256 hash of a byte array.
    /// </summary>
    private static byte[] Hash(byte[] data)
    {
        return SHA256.HashData(data);
    }

    /// <summary>
    /// Test that Build with a single block produces a root hash equal to the hash of that block.
    /// </summary>
    [Fact]
    public void Build_WithSingleBlock_ShouldProduceRootHashEqualToBlockHash()
    {
        // Arrange
        var tree = new MerkleTree();
        var blocks = CreateBlocks("hello");
        var expectedHash = Hash(Encoding.UTF8.GetBytes("hello"));

        // Act
        tree.Build(blocks);

        // Assert
        tree.RootHash.Should().BeEquivalentTo(expectedHash);
    }

    /// <summary>
    /// Test that Build with 4 blocks produces a deterministic root hash.
    /// </summary>
    [Fact]
    public void Build_With4Blocks_ShouldProduceDeterministicRootHash()
    {
        // Arrange
        var tree = new MerkleTree();
        var blocks = CreateBlocks("a", "b", "c", "d");

        // Act
        tree.Build(blocks);
        var rootHash1 = tree.RootHash.ToArray();

        tree.Build(blocks);
        var rootHash2 = tree.RootHash.ToArray();

        // Assert
        rootHash1.Should().BeEquivalentTo(rootHash2);
        rootHash1.Should().HaveCount(32); // SHA-256 produces 32 bytes
    }

    /// <summary>
    /// Test that Build with non-power-of-2 block counts (3, 5, 7) succeeds.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void Build_WithNonPowerOf2Blocks_ShouldSucceed(int blockCount)
    {
        // Arrange
        var tree = new MerkleTree();
        var blocks = Enumerable.Range(0, blockCount)
            .Select(i => Encoding.UTF8.GetBytes($"block{i}"))
            .ToList();

        // Act
        tree.Build(blocks);

        // Assert
        tree.RootHash.Should().NotBeNull();
        tree.RootHash.Should().HaveCount(32);
    }

    /// <summary>
    /// Test that Verify returns true when the same data blocks are used.
    /// </summary>
    [Fact]
    public void Verify_WithSameData_ShouldReturnTrue()
    {
        // Arrange
        var tree = new MerkleTree();
        var blocks = CreateBlocks("alpha", "beta", "gamma", "delta");
        tree.Build(blocks);

        // Act
        var result = tree.Verify(blocks);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// Test that Verify returns false when the data has been modified.
    /// </summary>
    [Fact]
    public void Verify_WithModifiedData_ShouldReturnFalse()
    {
        // Arrange
        var tree = new MerkleTree();
        var blocks = CreateBlocks("alpha", "beta", "gamma", "delta");
        tree.Build(blocks);

        var modifiedBlocks = CreateBlocks("alpha", "TAMPERED", "gamma", "delta");

        // Act
        var result = tree.Verify(modifiedBlocks);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// Test that GetProof returns a valid proof for each leaf index.
    /// </summary>
    [Fact]
    public void GetProof_ShouldReturnValidProofForEachLeaf()
    {
        // Arrange
        var tree = new MerkleTree();
        var blocks = CreateBlocks("w", "x", "y", "z");
        tree.Build(blocks);

        // Act & Assert
        for (var i = 0; i < blocks.Count; i++)
        {
            var proof = tree.GetProof(i);
            proof.Should().NotBeEmpty();

            var leafHash = Hash(blocks[i]);
            var isValid = MerkleTree.VerifyProof(leafHash, proof, tree.RootHash, i);
            isValid.Should().BeTrue($"proof for leaf index {i} should be valid");
        }
    }

    /// <summary>
    /// Test that VerifyProof succeeds with a valid proof.
    /// </summary>
    [Fact]
    public void VerifyProof_WithValidProof_ShouldReturnTrue()
    {
        // Arrange
        var tree = new MerkleTree();
        var blocks = CreateBlocks("one", "two", "three", "four", "five");
        tree.Build(blocks);

        var leafIndex = 2;
        var leafHash = Hash(blocks[leafIndex]);
        var proof = tree.GetProof(leafIndex);

        // Act
        var result = MerkleTree.VerifyProof(leafHash, proof, tree.RootHash, leafIndex);

        // Assert
        result.Should().BeTrue();
    }

    /// <summary>
    /// Test that VerifyProof fails when the proof has been tampered with.
    /// </summary>
    [Fact]
    public void VerifyProof_WithTamperedProof_ShouldReturnFalse()
    {
        // Arrange
        var tree = new MerkleTree();
        var blocks = CreateBlocks("one", "two", "three", "four");
        tree.Build(blocks);

        var leafIndex = 1;
        var leafHash = Hash(blocks[leafIndex]);
        var proof = tree.GetProof(leafIndex).ToList();

        // Tamper with the first proof element
        var tampered = new byte[proof[0].Length];
        Array.Fill(tampered, (byte)0xFF);
        proof[0] = tampered;

        // Act
        var result = MerkleTree.VerifyProof(leafHash, proof, tree.RootHash, leafIndex);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// Test that Build with empty data blocks throws ArgumentException.
    /// </summary>
    [Fact]
    public void Build_WithEmptyDataBlocks_ShouldThrowArgumentException()
    {
        // Arrange
        var tree = new MerkleTree();
        var blocks = new List<byte[]>();

        // Act
        var act = () => tree.Build(blocks);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Test that GetProof throws when called before Build.
    /// </summary>
    [Fact]
    public void GetProof_BeforeBuild_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var tree = new MerkleTree();

        // Act
        var act = () => tree.GetProof(0);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Test that GetProof throws for out-of-range leaf index.
    /// </summary>
    [Fact]
    public void GetProof_WithInvalidIndex_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        var tree = new MerkleTree();
        var blocks = CreateBlocks("a", "b", "c", "d");
        tree.Build(blocks);

        // Act
        var act = () => tree.GetProof(10);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Test that different data produces different root hashes.
    /// </summary>
    [Fact]
    public void Build_DifferentData_ShouldProduceDifferentRootHashes()
    {
        // Arrange
        var tree1 = new MerkleTree();
        var tree2 = new MerkleTree();

        var blocks1 = CreateBlocks("a", "b", "c", "d");
        var blocks2 = CreateBlocks("e", "f", "g", "h");

        // Act
        tree1.Build(blocks1);
        tree2.Build(blocks2);

        // Assert
        tree1.RootHash.Should().NotBeEquivalentTo(tree2.RootHash);
    }

    /// <summary>
    /// Test that VerifyProof with wrong leaf hash returns false.
    /// </summary>
    [Fact]
    public void VerifyProof_WithWrongLeafHash_ShouldReturnFalse()
    {
        // Arrange
        var tree = new MerkleTree();
        var blocks = CreateBlocks("a", "b", "c", "d");
        tree.Build(blocks);

        var proof = tree.GetProof(0);
        var wrongLeafHash = Hash(Encoding.UTF8.GetBytes("wrong"));

        // Act
        var result = MerkleTree.VerifyProof(wrongLeafHash, proof, tree.RootHash, 0);

        // Assert
        result.Should().BeFalse();
    }

    /// <summary>
    /// Test that VerifyProof throws ArgumentOutOfRangeException for a negative leaf index (H14).
    /// </summary>
    [Fact]
    public void VerifyProof_WithNegativeLeafIndex_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        var tree = new MerkleTree();
        var blocks = CreateBlocks("a", "b");
        tree.Build(blocks);
        var proof = tree.GetProof(0);
        var leafHash = Hash(Encoding.UTF8.GetBytes("a"));

        // Act
        var act = () => MerkleTree.VerifyProof(leafHash, proof, tree.RootHash, -1);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Test that RootHash returns a defensive copy so mutating it does not affect internal state (M20).
    /// </summary>
    [Fact]
    public void RootHash_ShouldReturnDefensiveCopy()
    {
        // Arrange
        var tree = new MerkleTree();
        tree.Build(CreateBlocks("test"));

        // Act: mutate the returned hash
        var hash1 = tree.RootHash;
        hash1[0] ^= 0xFF;

        // Assert: a second retrieval should be unaffected
        var hash2 = tree.RootHash;
        hash2.Should().NotBeEquivalentTo(hash1, "mutating returned hash should not affect internal state");
    }

    /// <summary>
    /// Test that Build throws ArgumentException when a data block is null (M23).
    /// </summary>
    [Fact]
    public void Build_WithNullBlock_ShouldThrowArgumentNullException()
    {
        // Arrange
        var tree = new MerkleTree();
        var blocks = new List<byte[]> { Encoding.UTF8.GetBytes("a"), null, Encoding.UTF8.GetBytes("c") };

        // Act
        var act = () => tree.Build(blocks);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }
}
