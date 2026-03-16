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
/// Unit tests for the QuorumReplication class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class QuorumReplicationTests
{
    /// <summary>
    /// Test that write succeeds when W replicas are available.
    /// </summary>
    [Fact]
    public async Task WriteAsync_WReplicasAvailable_Succeeds()
    {
        // Arrange — N=3, W=2, R=2
        var quorum = new QuorumReplication<string, int>(3, 2, 2);

        // Act
        var success = await quorum.WriteAsync("key1", 42).ConfigureAwait(true);

        // Assert
        Assert.True(success);
    }

    /// <summary>
    /// Test that write fails when fewer than W replicas are available.
    /// </summary>
    [Fact]
    public async Task WriteAsync_FewerThanWAvailable_Fails()
    {
        // Arrange — N=3, W=2, R=2. Take down 2 replicas → only 1 available
        var quorum = new QuorumReplication<string, int>(3, 2, 2);
        quorum.SetReplicaAvailability("replica-0", false);
        quorum.SetReplicaAvailability("replica-1", false);

        // Act
        var success = await quorum.WriteAsync("key1", 42).ConfigureAwait(true);

        // Assert
        Assert.False(success);
    }

    /// <summary>
    /// Test that read returns the latest version when R replicas are available.
    /// </summary>
    [Fact]
    public async Task ReadAsync_RReplicasAvailable_ReturnsLatestVersion()
    {
        // Arrange
        var quorum = new QuorumReplication<string, int>(3, 2, 2);
        await quorum.WriteAsync("key1", 42).ConfigureAwait(true);

        // Act
        var (value, found, version) = await quorum.ReadAsync("key1").ConfigureAwait(true);

        // Assert
        Assert.True(found);
        Assert.Equal(42, value);
        Assert.True(version > 0);
    }

    /// <summary>
    /// Test that read fails when fewer than R replicas are available.
    /// </summary>
    [Fact]
    public async Task ReadAsync_FewerThanRAvailable_ReturnsFalse()
    {
        // Arrange — N=3, W=2, R=2. Write first, then take down 2
        var quorum = new QuorumReplication<string, int>(3, 2, 2);
        await quorum.WriteAsync("key1", 42).ConfigureAwait(true);

        quorum.SetReplicaAvailability("replica-0", false);
        quorum.SetReplicaAvailability("replica-1", false);

        // Act
        var (_, found, _) = await quorum.ReadAsync("key1").ConfigureAwait(true);

        // Assert
        Assert.False(found);
    }

    /// <summary>
    /// Test that read repair updates stale replicas.
    /// </summary>
    [Fact]
    public async Task ReadAsync_PerformsReadRepair_OnStaleReplicas()
    {
        // Arrange — N=3, W=2, R=2
        var quorum = new QuorumReplication<string, int>(3, 2, 2);

        // Write with all 3 up
        await quorum.WriteAsync("key1", 42).ConfigureAwait(true);

        // Take down replica-2, write new value (only replica-0 and replica-1 get it)
        quorum.SetReplicaAvailability("replica-2", false);
        await quorum.WriteAsync("key1", 100).ConfigureAwait(true);

        // Bring replica-2 back
        quorum.SetReplicaAvailability("replica-2", true);

        // Act — read should trigger read repair on replica-2
        var (value, found, _) = await quorum.ReadAsync("key1").ConfigureAwait(true);

        // Assert — should get the latest value
        Assert.True(found);
        Assert.Equal(100, value);
    }

    /// <summary>
    /// Test that W + R > N ensures read-after-write consistency.
    /// </summary>
    [Fact]
    public async Task QuorumOverlap_EnsuresReadAfterWriteConsistency()
    {
        // Arrange — N=5, W=3, R=3 (overlap of at least 1)
        var quorum = new QuorumReplication<string, int>(5, 3, 3);

        // Act
        await quorum.WriteAsync("key1", 42).ConfigureAwait(true);
        var (value, found, _) = await quorum.ReadAsync("key1").ConfigureAwait(true);

        // Assert
        Assert.True(found);
        Assert.Equal(42, value);
    }

    /// <summary>
    /// Test that constructor validates W + R > N.
    /// </summary>
    [Fact]
    public void Constructor_WPlusRNotGreaterThanN_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert — N=3, W=1, R=1 → W+R=2 which is NOT > 3
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuorumReplication<string, int>(3, 1, 1));
    }

    /// <summary>
    /// Test that constructor validates W <= N.
    /// </summary>
    [Fact]
    public void Constructor_WGreaterThanN_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuorumReplication<string, int>(3, 4, 2));
    }

    /// <summary>
    /// Test that constructor validates R <= N.
    /// </summary>
    [Fact]
    public void Constructor_RGreaterThanN_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuorumReplication<string, int>(3, 2, 4));
    }

    /// <summary>
    /// Test that SetReplicaAvailability affects write/read success.
    /// </summary>
    [Fact]
    public async Task SetReplicaAvailability_AffectsOperations()
    {
        // Arrange — N=3, W=3, R=3 (strict quorum)
        var quorum = new QuorumReplication<string, int>(3, 3, 3);

        // Act — take one down, write should fail
        quorum.SetReplicaAvailability("replica-0", false);
        var writeSuccess = await quorum.WriteAsync("key1", 42).ConfigureAwait(true);

        // Assert
        Assert.False(writeSuccess);

        // Act — bring it back, write should succeed
        quorum.SetReplicaAvailability("replica-0", true);
        writeSuccess = await quorum.WriteAsync("key1", 42).ConfigureAwait(true);

        // Assert
        Assert.True(writeSuccess);
    }

    /// <summary>
    /// Test that reading a nonexistent key returns not found.
    /// </summary>
    [Fact]
    public async Task ReadAsync_NonexistentKey_ReturnsNotFound()
    {
        // Arrange
        var quorum = new QuorumReplication<string, int>(3, 2, 2);

        // Act
        var (_, found, _) = await quorum.ReadAsync("nonexistent").ConfigureAwait(true);

        // Assert
        Assert.False(found);
    }

    /// <summary>
    /// Test that multiple writes return increasing versions.
    /// </summary>
    [Fact]
    public async Task WriteAsync_MultipleWrites_VersionsIncrease()
    {
        // Arrange
        var quorum = new QuorumReplication<string, int>(3, 2, 2);

        // Act
        await quorum.WriteAsync("key1", 1).ConfigureAwait(true);
        var (_, _, version1) = await quorum.ReadAsync("key1").ConfigureAwait(true);

        await quorum.WriteAsync("key1", 2).ConfigureAwait(true);
        var (_, _, version2) = await quorum.ReadAsync("key1").ConfigureAwait(true);

        // Assert
        Assert.True(version2 > version1);
    }

    // ========== Tier 1 correctness tests ==========

    /// <summary>
    /// Test that writes go to exactly W replicas (not all), so a read from a
    /// non-written replica returns stale data until read repair.
    /// </summary>
    [Fact]
    public async Task WriteAsync_WritesToExactlyWReplicas()
    {
        // Arrange — N=3, W=2, R=2
        var quorum = new QuorumReplication<string, int>(3, 2, 2);

        // Take down one replica — only 2 available, which equals W
        quorum.SetReplicaAvailability("replica-2", false);

        // Act — write should succeed with exactly W=2 replicas
        var success = await quorum.WriteAsync("key1", 42).ConfigureAwait(true);

        // Assert — write succeeded
        Assert.True(success);

        // Bring back replica-2. It should NOT have the value yet (wasn't written to)
        quorum.SetReplicaAvailability("replica-2", true);

        // Take down the two that got the write — only replica-2 is available
        quorum.SetReplicaAvailability("replica-0", false);
        quorum.SetReplicaAvailability("replica-1", false);

        // Read should fail (only 1 replica available, need R=2)
        var (_, found, _) = await quorum.ReadAsync("key1").ConfigureAwait(true);
        Assert.False(found);
    }
}
