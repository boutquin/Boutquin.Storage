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
using Boutquin.Storage.Infrastructure.Replication;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Unit tests for the SingleLeaderReplication class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class SingleLeaderReplicationTests
{
    /// <summary>
    /// Test that writing to the leader and reading from the leader succeeds.
    /// </summary>
    [Fact]
    public async Task WriteAndReadFromLeader_Succeeds()
    {
        // Arrange
        var replication = new SingleLeaderReplication<string, int>();

        // Act
        await replication.WriteAsync("key1", 42).ConfigureAwait(true);
        var (value, found) = await replication.ReadAsync("key1").ConfigureAwait(true);

        // Assert
        Assert.True(found);
        Assert.Equal(42, value);
    }

    /// <summary>
    /// Test that writing to the leader, syncing a follower, and reading from the follower succeeds.
    /// </summary>
    [Fact]
    public async Task WriteToLeader_SyncFollower_ReadFromFollower_Succeeds()
    {
        // Arrange
        var replication = new SingleLeaderReplication<string, int>();
        replication.AddFollower("follower-1");

        // Act
        await replication.WriteAsync("key1", 42).ConfigureAwait(true);
        await replication.SyncFollowerAsync("follower-1").ConfigureAwait(true);
        var (value, found) = await replication.ReadAsync("key1", "follower-1").ConfigureAwait(true);

        // Assert
        Assert.True(found);
        Assert.Equal(42, value);
    }

    /// <summary>
    /// Test that a follower returns stale data before sync.
    /// </summary>
    [Fact]
    public async Task ReadFromFollower_BeforeSync_ReturnsNotFound()
    {
        // Arrange
        var replication = new SingleLeaderReplication<string, int>();
        replication.AddFollower("follower-1");
        await replication.WriteAsync("key1", 42).ConfigureAwait(true);

        // Act — read from follower WITHOUT syncing first
        var (_, found) = await replication.ReadAsync("key1", "follower-1").ConfigureAwait(true);

        // Assert
        Assert.False(found);
    }

    /// <summary>
    /// Test that GetReplicationLag shows correct lag per follower.
    /// </summary>
    [Fact]
    public async Task GetReplicationLag_ShowsCorrectLag()
    {
        // Arrange
        var replication = new SingleLeaderReplication<string, int>();
        replication.AddFollower("follower-1");
        replication.AddFollower("follower-2");

        await replication.WriteAsync("key1", 1).ConfigureAwait(true);
        await replication.WriteAsync("key2", 2).ConfigureAwait(true);
        await replication.WriteAsync("key3", 3).ConfigureAwait(true);

        // Sync follower-1 partially (sync after 3 writes)
        await replication.SyncFollowerAsync("follower-1").ConfigureAwait(true);

        // Act
        var lag = replication.GetReplicationLag();

        // Assert — follower-1 is caught up (lag 0), follower-2 has lag of 3
        Assert.Equal(0, lag["follower-1"]);
        Assert.Equal(3, lag["follower-2"]);
    }

    /// <summary>
    /// Test that AddFollower and RemoveFollower work correctly.
    /// </summary>
    [Fact]
    public async Task AddAndRemoveFollower_WorkCorrectly()
    {
        // Arrange
        var replication = new SingleLeaderReplication<string, int>();
        replication.AddFollower("follower-1");

        // Act — remove follower, then try to read from it
        replication.RemoveFollower("follower-1");

        // Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => replication.ReadAsync("key1", "follower-1")).ConfigureAwait(true);
    }

    /// <summary>
    /// Test that multiple writes and sync preserves order.
    /// </summary>
    [Fact]
    public async Task MultipleWritesAndSync_PreservesLatestValue()
    {
        // Arrange
        var replication = new SingleLeaderReplication<string, int>();
        replication.AddFollower("follower-1");

        // Act — write multiple values to the same key
        await replication.WriteAsync("key1", 1).ConfigureAwait(true);
        await replication.WriteAsync("key1", 2).ConfigureAwait(true);
        await replication.WriteAsync("key1", 3).ConfigureAwait(true);

        await replication.SyncFollowerAsync("follower-1").ConfigureAwait(true);
        var (value, found) = await replication.ReadAsync("key1", "follower-1").ConfigureAwait(true);

        // Assert — follower should have the latest value
        Assert.True(found);
        Assert.Equal(3, value);
    }

    /// <summary>
    /// Test that reading from an unknown replica throws ArgumentException.
    /// </summary>
    [Fact]
    public async Task ReadFromUnknownReplica_ThrowsArgumentException()
    {
        // Arrange
        var replication = new SingleLeaderReplication<string, int>();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => replication.ReadAsync("key1", "nonexistent")).ConfigureAwait(true);
    }

    /// <summary>
    /// Test that reading a key that was never written returns not found.
    /// </summary>
    [Fact]
    public async Task ReadNonexistentKey_ReturnsNotFound()
    {
        // Arrange
        var replication = new SingleLeaderReplication<string, int>();

        // Act
        var (_, found) = await replication.ReadAsync("nonexistent").ConfigureAwait(true);

        // Assert
        Assert.False(found);
    }

    /// <summary>
    /// Test that SyncFollowerAsync returns the correct high-water mark.
    /// </summary>
    [Fact]
    public async Task SyncFollowerAsync_ReturnsHighWaterMark()
    {
        // Arrange
        var replication = new SingleLeaderReplication<string, int>();
        replication.AddFollower("follower-1");

        await replication.WriteAsync("key1", 1).ConfigureAwait(true);
        await replication.WriteAsync("key2", 2).ConfigureAwait(true);

        // Act
        var hwm = await replication.SyncFollowerAsync("follower-1").ConfigureAwait(true);

        // Assert
        Assert.Equal(2, hwm);
    }

    /// <summary>
    /// Test that incremental sync works: sync once, write more, sync again.
    /// The follower should catch up incrementally via high-water marks,
    /// not re-process the entire log.
    /// </summary>
    [Fact]
    public async Task IncrementalSync_SyncWriteSync_FollowerCatchesUp()
    {
        // Arrange
        var replication = new SingleLeaderReplication<string, int>();
        replication.AddFollower("follower-1");

        // First batch of writes
        await replication.WriteAsync("key1", 1).ConfigureAwait(true);
        await replication.WriteAsync("key2", 2).ConfigureAwait(true);

        // First sync — follower catches up to position 2
        var hwm1 = await replication.SyncFollowerAsync("follower-1").ConfigureAwait(true);
        Assert.Equal(2, hwm1);

        // Act — write more, then sync again
        await replication.WriteAsync("key3", 3).ConfigureAwait(true);
        await replication.WriteAsync("key4", 4).ConfigureAwait(true);
        var hwm2 = await replication.SyncFollowerAsync("follower-1").ConfigureAwait(true);

        // Assert — follower is now at position 4 and has all values
        Assert.Equal(4, hwm2);

        var (val3, found3) = await replication.ReadAsync("key3", "follower-1").ConfigureAwait(true);
        Assert.True(found3);
        Assert.Equal(3, val3);

        var (val4, found4) = await replication.ReadAsync("key4", "follower-1").ConfigureAwait(true);
        Assert.True(found4);
        Assert.Equal(4, val4);

        // Lag should be zero after full sync
        var lag = replication.GetReplicationLag();
        Assert.Equal(0, lag["follower-1"]);
    }
}
