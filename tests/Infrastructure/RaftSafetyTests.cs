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
/// Phase C safety tests for <see cref="RaftNode{TCommand}"/> per Ongaro and Ousterhout 2014
/// Figure 2. The C1 matchIndex-sentinel fix prevents a newly-elected leader from
/// auto-committing log[0] before any follower has acknowledged replication.
/// </summary>
public sealed class RaftSafetyTests
{
    private static async Task<RaftCluster<string>> NewThreeNodeClusterWithLeaderAsync(string leaderId = "node-1")
    {
        var cluster = new RaftCluster<string>();
        cluster.AddNode(new RaftNode<string>("node-1"));
        cluster.AddNode(new RaftNode<string>("node-2"));
        cluster.AddNode(new RaftNode<string>("node-3"));

        await cluster.TriggerElectionAsync(leaderId);
        for (var i = 0; i < 3; i++)
        {
            await cluster.TickAsync();
        }
        return cluster;
    }

    [Fact]
    public async Task NewlyElectedLeader_WithNoFollowerAcks_DoesNotCommitFirstEntry()
    {
        // The pre-fix impl initialised matchIndex[peers] = 0 so TryAdvanceCommitIndex would
        // count every peer as replicated for log[0] the moment the leader proposed anything —
        // committing before any AppendEntriesResponse for that entry had arrived. With
        // matchIndex sentinel = -1, peers only contribute once a response for the new entry
        // lands.
        var cluster = await NewThreeNodeClusterWithLeaderAsync();
        var leader = cluster.GetLeader();
        leader.Should().NotBeNull();

        // Propose a command. Do NOT tick — no AppendEntriesResponse can have been processed
        // for this brand-new entry. Commit-index must not advance.
        await leader!.ProposeAsync("cmd-0");

        var committed = leader.GetCommittedLog();
        committed.Should().BeEmpty(
            "the freshly-proposed entry cannot be committed before any follower acknowledges it");
    }

    [Fact]
    public async Task Leader_CommitsEntry_OnceFollowerResponsesArrive()
    {
        var cluster = await NewThreeNodeClusterWithLeaderAsync();
        var leader = cluster.GetLeader()!;

        await leader.ProposeAsync("cmd-0");

        // Several ticks let heartbeat + append-entries + response round-trips play out.
        for (var i = 0; i < 6; i++)
        {
            await cluster.TickAsync();
        }

        // Propose a second command and tick again — some commits lag by a round in the tick model.
        await leader.ProposeAsync("cmd-1");
        for (var i = 0; i < 6; i++)
        {
            await cluster.TickAsync();
        }

        var committed = leader.GetCommittedLog();
        committed.Should().NotBeEmpty(
            "once follower AppendEntriesResponses have been processed, the leader must advance commitIndex");
    }
}
