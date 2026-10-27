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
/// Single-node <see cref="RaftNode{TCommand}.ProposeAsync"/> commit-safety tests. A single-node
/// cluster (no peers) is its own majority, so a proposed entry commits without any
/// AppendEntriesResponse round-trip. The safety contract is that the commit is still routed
/// through the same current-term guard (Ongaro &amp; Ousterhout 2014 §5.4.2 — a leader only
/// advances the commit index over entries from its OWN term) that the multi-node path uses,
/// not a divergent special-case that blindly assigns the commit index.
/// </summary>
public sealed class RaftSingleNodeCommitSafetyTests
{
    /// <summary>
    /// A single-node leader proposes a command IN its current term and the entry commits.
    /// This is the positive case of the §5.4.2 current-term guard: the proposed entry's term
    /// equals <c>CurrentTerm</c>, so <c>TryAdvanceCommitIndex</c> advances over it. Routing the
    /// single-node path through the guard (rather than an unconditional <c>_commitIndex = entryIndex</c>)
    /// keeps the single-node and multi-node commit paths on one code path — the entry must be a
    /// current-term entry to commit, exactly as the majority path requires.
    /// </summary>
    [Fact]
    public async Task SingleNodeLeader_ProposesCurrentTermEntry_CommitsThroughCurrentTermGuard()
    {
        var cluster = new RaftCluster<string>();
        var node = new RaftNode<string>("node-1");
        cluster.AddNode(node);

        // Become a single-node leader at term 1.
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        node.State.Should().Be(RaftNodeState.Leader);

        var committed = await node.ProposeAsync("cmd-0").ConfigureAwait(true);

        committed.Should().BeTrue(
            "a single-node leader is its own majority, and the proposed entry is a current-term "
            + "entry, so the current-term commit guard advances the commit index over it");
        node.GetCommittedLog().Should().ContainSingle()
            .Which.Command.Should().Be("cmd-0");
    }
}
