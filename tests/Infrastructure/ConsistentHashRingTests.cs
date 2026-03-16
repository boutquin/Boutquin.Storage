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
/// Unit tests for the ConsistentHashRing class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class ConsistentHashRingTests
{
    /// <summary>
    /// Test that AddNode places virtual nodes on the ring.
    /// </summary>
    [Fact]
    public void AddNode_PlacesVirtualNodesOnRing()
    {
        // Arrange
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 10);

        // Act
        ring.AddNode("server1");

        // Assert — should be able to look up keys
        var node = ring.GetNode("testKey");
        Assert.Equal("server1", node);
    }

    /// <summary>
    /// Test that RemoveNode removes virtual nodes from ring.
    /// </summary>
    [Fact]
    public void RemoveNode_RemovesVirtualNodes()
    {
        // Arrange
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 10);
        ring.AddNode("server1");
        ring.AddNode("server2");

        // Act
        ring.RemoveNode("server1");

        // Assert — all keys should now go to server2
        var node = ring.GetNode("testKey");
        Assert.Equal("server2", node);
    }

    /// <summary>
    /// Test that GetNode returns consistent node for the same key.
    /// </summary>
    [Fact]
    public void GetNode_ReturnsConsistentNode_ForSameKey()
    {
        // Arrange
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 50);
        ring.AddNode("server1");
        ring.AddNode("server2");
        ring.AddNode("server3");

        // Act
        var node1 = ring.GetNode("myKey");
        var node2 = ring.GetNode("myKey");

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
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 50);
        ring.AddNode("server1");
        ring.AddNode("server2");

        // Record assignments before adding server3
        var keys = Enumerable.Range(0, 100).Select(i => $"key{i}").ToList();
        var before = keys.Select(ring.GetNode).ToList();

        // Act — add a third node
        ring.AddNode("server3");
        var after = keys.Select(ring.GetNode).ToList();

        // Assert — most keys should stay on the same node
        var unchanged = before.Zip(after, (b, a) => b == a).Count(x => x);
        Assert.True(unchanged > 50, $"Expected >50% keys unchanged, but only {unchanged}/100 stayed.");
    }

    /// <summary>
    /// Test that GetNodes returns requested count of distinct nodes.
    /// </summary>
    [Fact]
    public void GetNodes_ReturnsDistinctPhysicalNodes()
    {
        // Arrange
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 50);
        ring.AddNode("server1");
        ring.AddNode("server2");
        ring.AddNode("server3");

        // Act
        var nodes = ring.GetNodes("testKey", 2);

        // Assert
        Assert.Equal(2, nodes.Count);
        Assert.NotEqual(nodes[0], nodes[1]);
    }

    /// <summary>
    /// Test that GetNodes with count > available returns all nodes.
    /// </summary>
    [Fact]
    public void GetNodes_CountExceedsAvailable_ReturnsAllNodes()
    {
        // Arrange
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 50);
        ring.AddNode("server1");
        ring.AddNode("server2");

        // Act
        var nodes = ring.GetNodes("testKey", 5);

        // Assert
        Assert.Equal(2, nodes.Count);
    }

    /// <summary>
    /// Test that GetNode on empty ring throws InvalidOperationException.
    /// </summary>
    [Fact]
    public void GetNode_EmptyRing_ThrowsInvalidOperationException()
    {
        // Arrange
        var ring = new ConsistentHashRing<string>();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => ring.GetNode("testKey"));
    }

    /// <summary>
    /// Test that removing the only node leaves the ring empty and GetNode throws.
    /// </summary>
    [Fact]
    public void RemoveNode_LastNode_LeavesRingEmpty()
    {
        // Arrange
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 10);
        ring.AddNode("server1");

        // Act
        ring.RemoveNode("server1");

        // Assert
        Assert.Throws<InvalidOperationException>(() => ring.GetNode("testKey"));
    }

    /// <summary>
    /// Test that adding the same node twice is idempotent.
    /// </summary>
    [Fact]
    public void AddNode_Duplicate_IsIdempotent()
    {
        // Arrange
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 10);
        ring.AddNode("server1");

        // Act — add same node again
        ring.AddNode("server1");

        // Assert — still works, only one physical node
        var node = ring.GetNode("testKey");
        Assert.Equal("server1", node);
    }

    /// <summary>
    /// Test that removing a non-existent node is a no-op.
    /// </summary>
    [Fact]
    public void RemoveNode_NonExistent_IsNoOp()
    {
        // Arrange
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 10);
        ring.AddNode("server1");

        // Act — remove a node that doesn't exist
        ring.RemoveNode("server99");

        // Assert — original node still works
        var node = ring.GetNode("testKey");
        Assert.Equal("server1", node);
    }

    /// <summary>
    /// Test that keys distribute across nodes (not all on one node).
    /// </summary>
    [Fact]
    public void GetNode_MultipleKeys_DistributeAcrossNodes()
    {
        // Arrange
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 150);
        ring.AddNode("server1");
        ring.AddNode("server2");
        ring.AddNode("server3");

        // Act — check 100 keys
        var nodeCounts = new Dictionary<string, int>();
        for (var i = 0; i < 100; i++)
        {
            var node = ring.GetNode($"key-{i}");
            nodeCounts.TryGetValue(node, out var count);
            nodeCounts[node] = count + 1;
        }

        // Assert — all 3 nodes should have some keys (distribution)
        Assert.Equal(3, nodeCounts.Count);
        Assert.All(nodeCounts.Values, count => Assert.True(count > 0));
    }

    /// <summary>
    /// Test that constructor throws for virtualNodeCount less than 1.
    /// </summary>
    [Fact]
    public void Constructor_ZeroVirtualNodes_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConsistentHashRing<string>(virtualNodeCount: 0));
    }

    /// <summary>
    /// Test that GetNode with null key throws.
    /// </summary>
    [Fact]
    public void GetNode_NullKey_ThrowsArgumentException()
    {
        // Arrange
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 10);
        ring.AddNode("server1");

        // Act & Assert — ThrowIfNullOrWhiteSpace throws ArgumentNullException for null input
        Assert.Throws<ArgumentNullException>(() => ring.GetNode(null!));
    }

    /// <summary>
    /// Test that AddNode with null throws ArgumentNullException.
    /// </summary>
    [Fact]
    public void AddNode_Null_ThrowsArgumentNullException()
    {
        // Arrange
        var ring = new ConsistentHashRing<string>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => ring.AddNode(null!));
    }

    /// <summary>
    /// Test that GetNodes on empty ring throws.
    /// </summary>
    [Fact]
    public void GetNodes_EmptyRing_ThrowsInvalidOperationException()
    {
        // Arrange
        var ring = new ConsistentHashRing<string>();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => ring.GetNodes("key", 2));
    }
}
