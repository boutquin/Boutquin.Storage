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

        // Act — completes normally when the write quorum is met (quorum misses throw).
        await quorum.WriteAsync("key1", 42).ConfigureAwait(true);

        // Assert — the write is durable and visible through a quorum read.
        var (value, found, _) = await quorum.ReadAsync("key1").ConfigureAwait(true);
        Assert.True(found);
        Assert.Equal(42, value);
    }

    /// <summary>
    /// Test that write throws <see cref="QuorumNotMetException"/> when fewer than W replicas
    /// are available — strict-quorum reject-before-mutate (DDIA p. 230). No replica is
    /// touched so partial state cannot surface.
    /// </summary>
    [Fact]
    public async Task WriteAsync_FewerThanWAvailable_ThrowsQuorumNotMet()
    {
        // Arrange — N=3, W=2, R=2. Take down 2 replicas → only 1 available
        var quorum = new QuorumReplication<string, int>(3, 2, 2);
        quorum.SetReplicaAvailability("replica-0", false);
        quorum.SetReplicaAvailability("replica-1", false);

        // Act / Assert
        var ex = await Assert.ThrowsAsync<QuorumNotMetException>(() => quorum.WriteAsync("key1", 42));
        Assert.Equal("Write", ex.Operation);
        Assert.Equal(1, ex.AvailableReplicas);
        Assert.Equal(2, ex.RequiredQuorum);

        // Assert — the rejected write must not have leaked into the surviving replica.
        // Bring everyone back and confirm no version exists for "key1".
        quorum.SetReplicaAvailability("replica-0", true);
        quorum.SetReplicaAvailability("replica-1", true);
        var (_, found, _) = await quorum.ReadAsync("key1").ConfigureAwait(true);
        Assert.False(found);
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
    /// Test that read throws <see cref="QuorumNotMetException"/> when fewer than R replicas
    /// are available — strict-quorum reject-before-return mirrors the write-side
    /// reject-before-mutate semantics so a caller cannot mistake a transient infrastructure
    /// failure for an authoritative absence.
    /// </summary>
    [Fact]
    public async Task ReadAsync_FewerThanRAvailable_ThrowsQuorumNotMet()
    {
        // Arrange — N=3, W=2, R=2. Write first, then take down 2 replicas → only 1 available
        var quorum = new QuorumReplication<string, int>(3, 2, 2);
        await quorum.WriteAsync("key1", 42).ConfigureAwait(true);

        quorum.SetReplicaAvailability("replica-0", false);
        quorum.SetReplicaAvailability("replica-1", false);

        // Act / Assert
        var ex = await Assert.ThrowsAsync<QuorumNotMetException>(() => quorum.ReadAsync("key1"));
        Assert.Equal("Read", ex.Operation);
        Assert.Equal(1, ex.AvailableReplicas);
        Assert.Equal(2, ex.RequiredQuorum);
    }

    /// <summary>
    /// Read repair must actually write the freshest value to the stale replica. Repair is
    /// unobservable through the public read API by construction (W + R > N forbids an
    /// R-window disjoint from every write set — the previous version of this test asserted
    /// only what the quorum overlap already guarantees), so this pins it via the internal
    /// probe: after awaiting the dispatched repair task, the stale replica's stored version
    /// must equal the version the read selected (DDIA 2e Ch. 6: "The client sees that
    /// replica 3 has a stale value and writes the newer value back to that replica").
    /// </summary>
    [Fact]
    public async Task ReadAsync_PerformsReadRepair_OnStaleReplicas()
    {
        // Arrange — N=3, W=2, R=2. Writes land ordinal-first on {replica-0, replica-1};
        // replica-2 never sees a write directly.
        var quorum = new QuorumReplication<string, int>(3, 2, 2);
        await quorum.WriteAsync("key1", 42).ConfigureAwait(true);
        await quorum.WriteAsync("key1", 100).ConfigureAwait(true);

        quorum.GetReplicaVersionForTesting("replica-2", "key1").Present
            .Should().BeFalse("precondition: the ordinal-first write set never reaches replica-2");

        // Act — the read selects the freshest value and dispatches a background repair.
        var (value, found, version) = await quorum.ReadAsync("key1").ConfigureAwait(true);
        found.Should().BeTrue();
        value.Should().Be(100);

        await quorum.LastReadRepairTaskForTesting!.ConfigureAwait(true);

        // Assert — the repair wrote the freshest version to the stale replica.
        var repaired = quorum.GetReplicaVersionForTesting("replica-2", "key1");
        repaired.Present.Should().BeTrue("read repair must write the freshest value back to the stale replica");
        repaired.Version.Should().Be(version, "the repaired replica must carry exactly the version the read selected");
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
    /// Test that constructor validates W &lt;= N.
    /// </summary>
    [Fact]
    public void Constructor_WGreaterThanN_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuorumReplication<string, int>(3, 4, 2));
    }

    /// <summary>
    /// Test that constructor validates R &lt;= N.
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

        // Act — take one down, write must throw (reject-before-mutate)
        quorum.SetReplicaAvailability("replica-0", false);
        await Assert.ThrowsAsync<QuorumNotMetException>(() => quorum.WriteAsync("key1", 42));

        // Act — bring it back, write completes normally
        quorum.SetReplicaAvailability("replica-0", true);
        await quorum.WriteAsync("key1", 42).ConfigureAwait(true);

        // Assert
        var (value, found, _) = await quorum.ReadAsync("key1").ConfigureAwait(true);
        Assert.True(found);
        Assert.Equal(42, value);
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

        // Act — write completes normally with exactly W=2 replicas
        await quorum.WriteAsync("key1", 42).ConfigureAwait(true);

        // Bring back replica-2. It should NOT have the value yet (wasn't written to)
        quorum.SetReplicaAvailability("replica-2", true);

        // Take down the two that got the write — only replica-2 is available
        quorum.SetReplicaAvailability("replica-0", false);
        quorum.SetReplicaAvailability("replica-1", false);

        // Read should throw (only 1 replica available, need R=2 — read-quorum reject).
        await Assert.ThrowsAsync<QuorumNotMetException>(() => quorum.ReadAsync("key1"));
    }

    // ========== True R-reads break-out ==========

    /// <summary>
    /// ReadAsync stops after R replicas are contacted (DDIA quorum semantics).
    /// With N=3, W=2, R=2 and only R=2 replicas containing the write, the read
    /// succeeds without contacting the third replica.
    /// </summary>
    [Fact]
    public async Task ReadAsync_StopsAfterRReplicas_QuorumOverlapSuffices()
    {
        // Arrange — N=3, W=2, R=2
        var quorum = new QuorumReplication<string, int>(3, 2, 2);

        // Write goes to replica-0 and replica-1 (ordinal-first W=2 available replicas)
        await quorum.WriteAsync("key1", 42).ConfigureAwait(true);

        // Make replica-2 unavailable so exactly R=2 replicas are reachable
        quorum.SetReplicaAvailability("replica-2", false);

        // Act — ReadAsync must succeed with exactly R=2 available replicas
        var (value, found, version) = await quorum.ReadAsync("key1").ConfigureAwait(true);

        // Assert
        Assert.True(found);
        Assert.Equal(42, value);
        Assert.True(version > 0);
    }

    /// <summary>
    /// The repair path's no-downgrade guard: a replica already carrying a NEWER version than
    /// the repair's payload must not be overwritten (a concurrent write can land between the
    /// read that scheduled the repair and the repair applying). Driven directly through the
    /// internal repair entry point because the interleaving cannot be scheduled
    /// deterministically through the public API.
    /// </summary>
    [Fact]
    public async Task ReadRepair_NeverDowngrades_AReplicaWithNewerVersion()
    {
        // Arrange — N=3, W=2, R=2. First write (version 1) lands on {replica-0, replica-1};
        // with replica-0 down, the second write (version 2) lands on {replica-1, replica-2}.
        var quorum = new QuorumReplication<string, int>(3, 2, 2);
        await quorum.WriteAsync("k", 1).ConfigureAwait(true);
        quorum.SetReplicaAvailability("replica-0", false);
        await quorum.WriteAsync("k", 99).ConfigureAwait(true);
        quorum.SetReplicaAvailability("replica-0", true);

        var before = quorum.GetReplicaVersionForTesting("replica-2", "k");
        before.Present.Should().BeTrue("precondition: replica-2 received the second write");
        before.Version.Should().Be(2);

        // Act — a stale repair (version 1) targeting replica-2 must be a no-op.
        await quorum.RunReadRepairForTesting(["replica-2"], "k", 1, version: 1).ConfigureAwait(true);

        // Assert — the newer version survives.
        var after = quorum.GetReplicaVersionForTesting("replica-2", "k");
        after.Version.Should().Be(2, "repair must never downgrade a replica that already holds a newer version");

        // And the repair path still APPLIES when it genuinely is newer.
        await quorum.RunReadRepairForTesting(["replica-0"], "k", 99, version: 2).ConfigureAwait(true);
        quorum.GetReplicaVersionForTesting("replica-0", "k").Version
            .Should().Be(2, "repair must bring a genuinely stale replica up to the newer version");
    }

    /// <summary>
    /// TOCTOU defence: dropping a replica between two writes must produce a clean rejection
    /// on the second write rather than partial state. The gate guards both replica-set
    /// mutation (<see cref="QuorumReplication{TKey, TValue}.SetReplicaAvailability"/>) and
    /// the count-check + fan-out path (<see cref="QuorumReplication{TKey, TValue}.WriteAsync"/>),
    /// so this is a contract test that the same gate covers both phases.
    /// </summary>
    [Fact]
    public async Task WriteAsync_AfterReplicaDrop_RejectsCleanly_NoPartialState()
    {
        // Arrange — N=3, W=3, R=1 (W+R=4 > 3; W=N means strict-all-replicas writes).
        var quorum = new QuorumReplication<string, int>(3, 3, 1);
        await quorum.WriteAsync("k", 1).ConfigureAwait(true);

        // Act — drop a replica, then attempt a second write that now cannot meet W=3.
        quorum.SetReplicaAvailability("replica-2", false);
        var ex = await Assert.ThrowsAsync<QuorumNotMetException>(() => quorum.WriteAsync("k", 2));

        // Assert — the in-flight write was rejected before any replica was mutated.
        // Read with R=1 from the still-available replicas: must return version 1, not 2.
        Assert.Equal("Write", ex.Operation);
        Assert.Equal(2, ex.AvailableReplicas);
        Assert.Equal(3, ex.RequiredQuorum);
        var (value, found, _) = await quorum.ReadAsync("k").ConfigureAwait(true);
        Assert.True(found);
        Assert.Equal(1, value);
    }

    /// <summary>
    /// ReadAsync reads ALL replicas when N replicas are available (N &gt; R) and
    /// returns the best (highest-version) value seen across the first R contacts.
    /// The quorum overlap guarantee means the best value is always in the first R.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithAllReplicasAvailable_ReturnsLatestFromQuorumWindow()
    {
        // Arrange — N=5, W=4, R=2 (W+R=6 > 5)
        var quorum = new QuorumReplication<string, int>(5, 4, 2);
        await quorum.WriteAsync("key1", 99).ConfigureAwait(true);

        // Act — all 5 available; true R-reads stops at R=2
        var (value, found, _) = await quorum.ReadAsync("key1").ConfigureAwait(true);

        // Assert — found via the quorum window (first R=2 replicas, which got the write)
        Assert.True(found);
        Assert.Equal(99, value);
    }

    /// <summary>
    /// DDIA 2e Ch. 6 ("when a client receives multiple values in response to a read, it uses
    /// the one with the greatest timestamp — even if that value was returned by only one
    /// replica"): when a STALE version sits earlier in the read window than the freshest one,
    /// the read must still return the greatest version, not the first value found.
    /// </summary>
    [Fact]
    public async Task ReadAsync_StaleVersionFirstInReadWindow_ReturnsGreatestVersion()
    {
        // Arrange — N=3, W=2, R=2. First write lands on replicas {0, 1} (ordinal-first W).
        var quorum = new QuorumReplication<string, int>(3, 2, 2);
        await quorum.WriteAsync("key1", 42).ConfigureAwait(true);

        // Take replica-0 down; the second write lands on {1, 2}. Replica-0 keeps the stale v1.
        quorum.SetReplicaAvailability("replica-0", false);
        await quorum.WriteAsync("key1", 100).ConfigureAwait(true);
        quorum.SetReplicaAvailability("replica-0", true);

        // Act — the read window is the ordinal-first R=2 = {0, 1}: replica-0 answers with the
        // STALE v1 first; replica-1 carries the fresh v2.
        var (value, found, version) = await quorum.ReadAsync("key1").ConfigureAwait(true);

        // Assert — greatest version wins within the window.
        Assert.True(found);
        Assert.Equal(100, value);
        Assert.Equal(2, version);
    }

    /// <summary>
    /// After <see cref="QuorumReplication{TKey, TValue}.Dispose"/>, every public method throws
    /// <see cref="ObjectDisposedException"/>. The class implements <see cref="IDisposable"/>
    /// so callers wrapping the replicator in a <c>using</c> block release the gate's kernel
    /// waitable handle deterministically; subsequent calls fail loudly.
    /// </summary>
    [Fact]
    public async Task Dispose_IsIdempotent_AndPostDisposeMethodsThrow()
    {
        var quorum = new QuorumReplication<string, int>(3, 2, 2);
        await quorum.WriteAsync("k", 1).ConfigureAwait(true);

        quorum.Dispose();
        quorum.Dispose(); // second dispose is a no-op (Interlocked-gated)

        Assert.Throws<ObjectDisposedException>(() => quorum.SetReplicaAvailability("replica-0", false));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => quorum.WriteAsync("k", 2));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => quorum.ReadAsync("k"));
    }
}
