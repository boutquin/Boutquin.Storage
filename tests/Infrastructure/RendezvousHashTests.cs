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
using Boutquin.Storage.Infrastructure.Partitioning;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Unit tests for the RendezvousHash class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class RendezvousHashTests
{
    /// <summary>
    /// Test that GetNode returns consistent node for the same key.
    /// </summary>
    [Fact]
    public void GetNode_ReturnsConsistentNode_ForSameKey()
    {
        // Arrange
        var hash = new RendezvousHash<string>();
        hash.AddNode("server1");
        hash.AddNode("server2");
        hash.AddNode("server3");

        // Act
        var node1 = hash.GetNode("myKey");
        var node2 = hash.GetNode("myKey");

        // Assert
        Assert.Equal(node1, node2);
    }

    /// <summary>
    /// Test that adding a node redistributes minimally.
    /// </summary>
    [Fact]
    public void AddNode_RedistributesMinimally()
    {
        // Arrange
        var hash = new RendezvousHash<string>();
        hash.AddNode("server1");
        hash.AddNode("server2");

        var keys = Enumerable.Range(0, 100).Select(i => $"key{i}").ToList();
        var before = keys.Select(hash.GetNode).ToList();

        // Act — add a third node
        hash.AddNode("server3");
        var after = keys.Select(hash.GetNode).ToList();

        // Assert — most keys should stay on the same node
        var unchanged = before.Zip(after, (b, a) => b == a).Count(x => x);
        Assert.True(unchanged > 50, $"Expected >50% keys unchanged, but only {unchanged}/100 stayed.");
    }

    /// <summary>
    /// Test that removing a node redistributes minimally.
    /// </summary>
    [Fact]
    public void RemoveNode_RedistributesMinimally()
    {
        // Arrange
        var hash = new RendezvousHash<string>();
        hash.AddNode("server1");
        hash.AddNode("server2");
        hash.AddNode("server3");

        var keys = Enumerable.Range(0, 100).Select(i => $"key{i}").ToList();
        var before = keys.Select(hash.GetNode).ToList();

        // Act
        hash.RemoveNode("server3");
        var after = keys.Select(hash.GetNode).ToList();

        // Assert — keys NOT on server3 should be unchanged
        var unchanged = before.Zip(after, (b, a) => b == a).Count(x => x);
        Assert.True(unchanged > 50, $"Expected >50% keys unchanged, but only {unchanged}/100 stayed.");
    }

    /// <summary>
    /// Test that GetNodes returns correct count.
    /// </summary>
    [Fact]
    public void GetNodes_ReturnsRequestedCount()
    {
        // Arrange
        var hash = new RendezvousHash<string>();
        hash.AddNode("server1");
        hash.AddNode("server2");
        hash.AddNode("server3");

        // Act
        var nodes = hash.GetNodes("testKey", 2);

        // Assert
        Assert.Equal(2, nodes.Count);
    }

    /// <summary>
    /// Test that AddNode and RemoveNode work correctly.
    /// </summary>
    [Fact]
    public void AddNode_RemoveNode_WorkCorrectly()
    {
        // Arrange
        var hash = new RendezvousHash<string>();
        hash.AddNode("server1");
        hash.AddNode("server2");

        // Act
        hash.RemoveNode("server1");
        var node = hash.GetNode("anyKey");

        // Assert — only server2 remains
        Assert.Equal("server2", node);
    }

    /// <summary>
    /// Test that empty node list throws InvalidOperationException.
    /// </summary>
    [Fact]
    public void GetNode_EmptyNodeList_ThrowsInvalidOperationException()
    {
        // Arrange
        var hash = new RendezvousHash<string>();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => hash.GetNode("testKey"));
    }

    /// <summary>
    /// Test that single node always returns that node.
    /// </summary>
    [Fact]
    public void GetNode_SingleNode_AlwaysReturnsThatNode()
    {
        // Arrange
        var hash = new RendezvousHash<string>();
        hash.AddNode("onlyServer");

        // Act & Assert — multiple keys should all return the same node
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal("onlyServer", hash.GetNode($"key{i}"));
        }
    }

    // ========== Tier 3: Duplicate node handling ==========

    /// <summary>
    /// Test that adding the same node multiple times doesn't affect results.
    /// Verifies O(1) duplicate detection with HashSet backing.
    /// </summary>
    [Fact]
    public void AddNode_Duplicate_DoesNotAffectGetNodes()
    {
        // Arrange
        var hash = new RendezvousHash<string>();
        hash.AddNode("server1");
        hash.AddNode("server2");

        var nodeBefore = hash.GetNode("testKey");

        // Act — add duplicate nodes
        hash.AddNode("server1");
        hash.AddNode("server2");
        hash.AddNode("server1");

        var nodeAfter = hash.GetNode("testKey");
        var allNodes = hash.GetNodes("testKey", 10);

        // Assert — same result, only 2 unique nodes
        Assert.Equal(nodeBefore, nodeAfter);
        Assert.Equal(2, allNodes.Count);
    }

    /// <summary>
    /// Test that RemoveNode works correctly after duplicate add attempts.
    /// </summary>
    [Fact]
    public void RemoveNode_AfterDuplicateAdds_RemovesCompletely()
    {
        // Arrange
        var hash = new RendezvousHash<string>();
        hash.AddNode("server1");
        hash.AddNode("server1"); // duplicate
        hash.AddNode("server2");

        // Act — remove server1
        hash.RemoveNode("server1");

        // Assert — only server2 remains
        var node = hash.GetNode("anyKey");
        Assert.Equal("server2", node);
    }

    // ========== Tier 4: Null argument validation ==========

    /// <summary>
    /// Test that AddNode with null throws ArgumentNullException.
    /// </summary>
    [Fact]
    public void AddNode_Null_ThrowsArgumentNullException()
    {
        // Arrange
        var hash = new RendezvousHash<string>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => hash.AddNode(null!));
    }

    /// <summary>
    /// Test that RemoveNode with null throws ArgumentNullException.
    /// </summary>
    [Fact]
    public void RemoveNode_Null_ThrowsArgumentNullException()
    {
        // Arrange
        var hash = new RendezvousHash<string>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => hash.RemoveNode(null!));
    }

    /// <summary>
    /// Test that GetNode with null key throws ArgumentNullException.
    /// ThrowIfNullOrWhiteSpace(null) throws ArgumentNullException, not ArgumentException.
    /// </summary>
    [Fact]
    public void GetNode_NullKey_ThrowsArgumentNullException()
    {
        // Arrange
        var hash = new RendezvousHash<string>();
        hash.AddNode("server1");

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => hash.GetNode(null!));
    }

    /// <summary>
    /// Test that GetNodes with null key throws ArgumentNullException.
    /// ThrowIfNullOrWhiteSpace(null) throws ArgumentNullException, not ArgumentException.
    /// </summary>
    [Fact]
    public void GetNodes_NullKey_ThrowsArgumentNullException()
    {
        // Arrange
        var hash = new RendezvousHash<string>();
        hash.AddNode("server1");

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => hash.GetNodes(null!, 1));
    }
}
