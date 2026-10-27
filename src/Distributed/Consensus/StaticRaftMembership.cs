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

// Membership is external to the term/vote/log persistence seams. Restoration therefore needs an
// explicit caller-owned membership manifest; discovering that set from Follower state is unsafe.
internal sealed class StaticRaftMembership
{
    private readonly HashSet<string>? _restoredMemberIds;
    private bool _started;

    internal StaticRaftMembership(IEnumerable<string>? restoredMemberIds = null)
    {
        if (restoredMemberIds is null)
        {
            return;
        }

        _restoredMemberIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in restoredMemberIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (!_restoredMemberIds.Add(id))
            {
                throw new ArgumentException("Restored membership contains a duplicate node identity.", nameof(restoredMemberIds));
            }
        }
        if (_restoredMemberIds.Count == 0)
        {
            throw new ArgumentException("Restored membership must contain at least one node.", nameof(restoredMemberIds));
        }
    }

    internal void ValidateAddition<TCommand>(RaftNode<TCommand> node, IReadOnlyDictionary<string, RaftNode<TCommand>> nodes)
    {
        if (_started || node.State != RaftNodeState.Follower ||
            nodes.Values.Any(member => member.State != RaftNodeState.Follower))
        {
            throw new InvalidOperationException("Register followers before starting elections or processing messages.");
        }
        if (nodes.ContainsKey(node.NodeId))
        {
            throw new ArgumentException("Duplicate Raft node identity.", nameof(node));
        }
        if (_restoredMemberIds is null && (node.HasPersistentHistory || nodes.Values.Any(member => member.HasPersistentHistory)))
        {
            throw new InvalidOperationException("A used follower requires explicit restoration with its original membership.");
        }
        if (_restoredMemberIds is not null && !_restoredMemberIds.Contains(node.NodeId))
        {
            throw new InvalidOperationException("The node is absent from the declared restored membership; reconfiguration is unsupported.");
        }
    }

    internal void Start(int registeredCount)
    {
        if (_restoredMemberIds is not null && registeredCount != _restoredMemberIds.Count)
        {
            throw new InvalidOperationException("Register every member of the restored membership before execution.");
        }
        _started = true;
    }
}
