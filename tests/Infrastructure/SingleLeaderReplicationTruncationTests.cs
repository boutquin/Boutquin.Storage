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
/// Observable end-to-end behaviour of <see cref="SingleLeaderReplication{TKey, TValue}"/>
/// under replication-log truncation. The log itself is internal; these tests cover the
/// externally-visible consequences — a follower that joins after truncation bootstraps from
/// a leader snapshot (DDIA 2e Ch. 6, "Setting Up New Followers"), and a lagging follower
/// keeps the log alive until it has caught up. <see cref="FollowerTooFarBehindException"/>
/// remains the standalone <see cref="ReplicationLog{TKey, TValue}"/> contract for direct log
/// consumers (covered by <see cref="ReplicationLogTruncateTests"/>).
/// </summary>
public sealed class SingleLeaderReplicationTruncationTests
{
    /// <summary>
    /// DDIA 2e Ch. 6, "Setting Up New Followers": a new follower is seeded with a consistent
    /// snapshot of the leader's database associated with an exact replication-log position,
    /// then requests only the changes since that position. A follower added AFTER the log has
    /// truncated must therefore bootstrap from the snapshot and sync cleanly — not wedge on
    /// <see cref="FollowerTooFarBehindException"/>.
    /// </summary>
    [Fact]
    public async Task AddFollower_AfterTruncation_BootstrapsFromLeaderSnapshot_AndSyncSucceeds()
    {
        var replication = new SingleLeaderReplication<string, int>();
        replication.AddFollower("f1");

        for (var i = 1; i <= 3; i++)
        {
            await replication.WriteAsync($"k{i}", i);
        }

        // f1 is the only follower; its sync advances min-hwm to 3 and truncates the log below 4.
        (await replication.SyncFollowerAsync("f1")).Should().Be(3);

        replication.AddFollower("late");

        // Snapshot bootstrap: the late follower carries the leader's state as of AddFollower.
        var (v1, found1) = await replication.ReadAsync("k1", "late");
        found1.Should().BeTrue("the new follower is seeded with a consistent snapshot of the leader");
        v1.Should().Be(1);

        // Its first sync starts from the snapshot's log position — nothing missed, no wedge.
        (await replication.SyncFollowerAsync("late")).Should().Be(3);
    }

    /// <summary>
    /// Writes that land AFTER the snapshot bootstrap are not part of the snapshot — the new
    /// follower lags behind until its next sync, exactly like any other follower (replication
    /// lag is observable between snapshot seeding and incremental catch-up).
    /// </summary>
    [Fact]
    public async Task LateFollower_WritesAfterSnapshot_RequireSyncToCatchUp()
    {
        var replication = new SingleLeaderReplication<string, int>();
        replication.AddFollower("f1");

        for (var i = 1; i <= 3; i++)
        {
            await replication.WriteAsync($"k{i}", i);
        }

        // f1's sync advances min-hwm to 3 and truncates the log below 4.
        (await replication.SyncFollowerAsync("f1")).Should().Be(3);

        // The late follower snapshots at log position 3; a write after that is not in its store
        // until it syncs.
        replication.AddFollower("late");
        await replication.WriteAsync("k4", 4);

        var (_, foundBeforeSync) = await replication.ReadAsync("k4", "late");
        foundBeforeSync.Should().BeFalse("the snapshot predates k4; the follower lags until synced");

        (await replication.SyncFollowerAsync("late")).Should().Be(4);
        var (v4, foundAfterSync) = await replication.ReadAsync("k4", "late");
        foundAfterSync.Should().BeTrue();
        v4.Should().Be(4);
    }

    [Fact]
    public async Task SlowFollower_PreventsTruncation_AndCatchesUpSuccessfully()
    {
        var replication = new SingleLeaderReplication<string, int>();
        replication.AddFollower("fast");
        replication.AddFollower("slow");

        for (var i = 1; i <= 5; i++)
        {
            await replication.WriteAsync($"k{i}", i);
        }

        // "fast" syncs alone — but min-hwm is min(5, 0) = 0, so no truncation occurs.
        (await replication.SyncFollowerAsync("fast")).Should().Be(5);

        // "slow" can still catch up because the log is intact.
        (await replication.SyncFollowerAsync("slow")).Should().Be(5);

        var (v3, found3) = await replication.ReadAsync("k3", "slow");
        found3.Should().BeTrue();
        v3.Should().Be(3);
    }
}
