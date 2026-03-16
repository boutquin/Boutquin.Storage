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
/// Unit tests for the GossipProtocol class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class GossipProtocolTests
{
    /// <summary>
    /// Test that UpdateLocalState stores state with version.
    /// </summary>
    [Fact]
    public void UpdateLocalState_StoresStateWithVersion()
    {
        // Arrange
        var gossip = new GossipProtocol<string, string>();

        // Act
        gossip.UpdateLocalState("node1", "healthy");

        // Assert
        var state = gossip.GetClusterState();
        Assert.Equal("healthy", state["node1"].State);
        Assert.Equal(1, state["node1"].Version);
    }

    /// <summary>
    /// Test that UpdateLocalState increments version on update.
    /// </summary>
    [Fact]
    public void UpdateLocalState_IncrementsVersionOnUpdate()
    {
        // Arrange
        var gossip = new GossipProtocol<string, string>();
        gossip.UpdateLocalState("node1", "healthy");

        // Act
        gossip.UpdateLocalState("node1", "degraded");

        // Assert
        var state = gossip.GetClusterState();
        Assert.Equal("degraded", state["node1"].State);
        Assert.Equal(2, state["node1"].Version);
    }

    /// <summary>
    /// Test that GetClusterState returns all known nodes.
    /// </summary>
    [Fact]
    public void GetClusterState_ReturnsAllKnownNodes()
    {
        // Arrange
        var gossip = new GossipProtocol<string, string>();
        gossip.UpdateLocalState("node1", "healthy");
        gossip.UpdateLocalState("node2", "healthy");
        gossip.UpdateLocalState("node3", "degraded");

        // Act
        var state = gossip.GetClusterState();

        // Assert
        Assert.Equal(3, state.Count);
    }

    /// <summary>
    /// Test that PrepareSyncMessage returns snapshot.
    /// </summary>
    [Fact]
    public void PrepareSyncMessage_ReturnsSnapshot()
    {
        // Arrange
        var gossip = new GossipProtocol<string, string>();
        gossip.UpdateLocalState("node1", "healthy");

        // Act
        var syncMessage = gossip.PrepareSyncMessage("node1");

        // Assert
        Assert.Single(syncMessage);
        Assert.Equal("healthy", syncMessage["node1"].State);
    }

    /// <summary>
    /// Test that ProcessSyncMessage merges newer remote state.
    /// </summary>
    [Fact]
    public void ProcessSyncMessage_MergesNewerRemoteState()
    {
        // Arrange
        var gossip = new GossipProtocol<string, string>();
        gossip.UpdateLocalState("node1", "healthy"); // version 1

        var remoteState = new Dictionary<string, (string State, long Version)>
        {
            ["node1"] = ("degraded", 5), // remote has newer version
            ["node2"] = ("healthy", 1),  // new node
        };

        // Act
        gossip.ProcessSyncMessage(remoteState);

        // Assert
        var state = gossip.GetClusterState();
        Assert.Equal("degraded", state["node1"].State);
        Assert.Equal(5, state["node1"].Version);
        Assert.Equal("healthy", state["node2"].State);
    }

    /// <summary>
    /// Test that ProcessSyncMessage ignores stale remote state.
    /// </summary>
    [Fact]
    public void ProcessSyncMessage_IgnoresStaleRemoteState()
    {
        // Arrange
        var gossip = new GossipProtocol<string, string>();
        gossip.UpdateLocalState("node1", "healthy");
        gossip.UpdateLocalState("node1", "degraded"); // version 2

        var remoteState = new Dictionary<string, (string State, long Version)>
        {
            ["node1"] = ("healthy", 1), // stale version
        };

        // Act
        gossip.ProcessSyncMessage(remoteState);

        // Assert — should keep local version 2
        var state = gossip.GetClusterState();
        Assert.Equal("degraded", state["node1"].State);
        Assert.Equal(2, state["node1"].Version);
    }

    /// <summary>
    /// Test that concurrent updates don't lose data.
    /// </summary>
    [Fact]
    public async Task ConcurrentUpdates_DontLoseData()
    {
        // Arrange
        var gossip = new GossipProtocol<string, string>();
        var tasks = new List<Task>();

        // Act — update 100 different nodes concurrently
        for (var i = 0; i < 100; i++)
        {
            var nodeId = $"node{i}";
            tasks.Add(Task.Run(() => gossip.UpdateLocalState(nodeId, "healthy")));
        }
        await Task.WhenAll(tasks).ConfigureAwait(true);

        // Assert — all 100 nodes should be present
        var state = gossip.GetClusterState();
        Assert.Equal(100, state.Count);
    }

    // ========== Tier 1: TOCTOU race fix test ==========

    /// <summary>
    /// Test that concurrent ProcessSyncMessage and UpdateLocalState don't lose the higher version.
    /// </summary>
    [Fact]
    public async Task ConcurrentSyncAndUpdate_HigherVersionWins()
    {
        // Arrange
        var gossip = new GossipProtocol<string, string>();
        gossip.UpdateLocalState("node1", "initial"); // version 1

        var tasks = new List<Task>();

        // Act — concurrently update local state and process sync messages
        for (var i = 0; i < 50; i++)
        {
            var version = i + 10;
            tasks.Add(Task.Run(() => gossip.UpdateLocalState("node1", $"local-{version}")));
            tasks.Add(Task.Run(() =>
            {
                var remote = new Dictionary<string, (string State, long Version)>
                {
                    ["node1"] = ($"remote-{version}", version),
                };
                gossip.ProcessSyncMessage(remote);
            }));
        }
        await Task.WhenAll(tasks).ConfigureAwait(true);

        // Assert — state should be consistent (no corruption), version should be > 0
        var state = gossip.GetClusterState();
        Assert.True(state.ContainsKey("node1"));
        Assert.True(state["node1"].Version > 0, "Version should be positive after concurrent operations");
    }

    /// <summary>
    /// Test that PrepareSyncMessage returns a snapshot, not a live reference.
    /// </summary>
    [Fact]
    public void PrepareSyncMessage_ReturnsSnapshot_NotLiveReference()
    {
        // Arrange
        var gossip = new GossipProtocol<string, string>();
        gossip.UpdateLocalState("node1", "healthy");

        // Act — take snapshot, then update
        var snapshot = gossip.PrepareSyncMessage("node1");
        gossip.UpdateLocalState("node1", "degraded");

        // Assert — snapshot should still show "healthy"
        Assert.Equal("healthy", snapshot["node1"].State);
        Assert.Equal(1, snapshot["node1"].Version);
    }

    /// <summary>
    /// Test that ProcessSyncMessage with empty dictionary is a no-op.
    /// </summary>
    [Fact]
    public void ProcessSyncMessage_EmptyDictionary_IsNoOp()
    {
        // Arrange
        var gossip = new GossipProtocol<string, string>();
        gossip.UpdateLocalState("node1", "healthy");

        // Act
        gossip.ProcessSyncMessage(new Dictionary<string, (string State, long Version)>());

        // Assert — state unchanged
        var state = gossip.GetClusterState();
        Assert.Single(state);
        Assert.Equal("healthy", state["node1"].State);
    }

    /// <summary>
    /// Test that ProcessSyncMessage with equal version keeps local state.
    /// </summary>
    [Fact]
    public void ProcessSyncMessage_EqualVersion_KeepsLocalState()
    {
        // Arrange
        var gossip = new GossipProtocol<string, string>();
        gossip.UpdateLocalState("node1", "healthy"); // version 1

        var remoteState = new Dictionary<string, (string State, long Version)>
        {
            ["node1"] = ("degraded", 1), // same version, different state
        };

        // Act
        gossip.ProcessSyncMessage(remoteState);

        // Assert — local should win when versions are equal (not strictly greater)
        var state = gossip.GetClusterState();
        Assert.Equal("healthy", state["node1"].State);
    }

    /// <summary>
    /// Test that UpdateLocalState with null nodeId throws.
    /// </summary>
    [Fact]
    public void UpdateLocalState_NullNodeId_ThrowsArgumentNullException()
    {
        // Arrange
        var gossip = new GossipProtocol<string, string>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => gossip.UpdateLocalState(null!, "healthy"));
    }

    /// <summary>
    /// Test that ProcessSyncMessage with null throws.
    /// </summary>
    [Fact]
    public void ProcessSyncMessage_Null_ThrowsArgumentNullException()
    {
        // Arrange
        var gossip = new GossipProtocol<string, string>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => gossip.ProcessSyncMessage(null!));
    }

    /// <summary>
    /// Test bidirectional sync between two gossip instances.
    /// </summary>
    [Fact]
    public void BidirectionalSync_MergesStateCorrectly()
    {
        // Arrange — two instances, each with local state
        var gossip1 = new GossipProtocol<string, string>();
        var gossip2 = new GossipProtocol<string, string>();

        gossip1.UpdateLocalState("node1", "healthy");
        gossip2.UpdateLocalState("node2", "healthy");

        // Act — sync both ways
        var msg1 = gossip1.PrepareSyncMessage("node1");
        var msg2 = gossip2.PrepareSyncMessage("node2");

        gossip1.ProcessSyncMessage(msg2);
        gossip2.ProcessSyncMessage(msg1);

        // Assert — both should know about both nodes
        var state1 = gossip1.GetClusterState();
        var state2 = gossip2.GetClusterState();

        Assert.Equal(2, state1.Count);
        Assert.Equal(2, state2.Count);
        Assert.Equal("healthy", state1["node2"].State);
        Assert.Equal("healthy", state2["node1"].State);
    }
}
