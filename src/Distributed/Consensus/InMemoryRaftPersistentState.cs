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
namespace Boutquin.Storage.Distributed.Consensus;

/// <summary>
/// In-memory <see cref="IRaftPersistentState"/> default — backs <c>currentTerm</c> and
/// <c>votedFor</c> as plain auto-properties and provides no crash durability. Correct only
/// for non-failing reference scenarios; a production deployment must supply a file-backed
/// implementation through the same ctor parameter on <see cref="RaftNode{TCommand}"/>.
/// Without persistent term + vote, a crash + restart can elect two leaders in the same
/// term (split brain) — a textbook Raft safety violation, not a perf gap.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why ship this.</b> Same rationale as <see cref="InMemoryRaftLog{TCommand}"/>: the
/// seam is the load-bearing pre-GA piece; this default keeps in-process reference scenarios
/// and in-memory test fixtures cheap.
/// </para>
/// </remarks>
public sealed class InMemoryRaftPersistentState : IRaftPersistentState
{
    /// <inheritdoc/>
    public long CurrentTerm { get; set; }

    /// <inheritdoc/>
    public string? VotedFor { get; set; }

    /// <inheritdoc/>
    public void SetTermAndVote(long term, string? votedFor)
    {
        CurrentTerm = term;
        VotedFor = votedFor;
    }
}
