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
namespace Boutquin.Storage.Distributed.Replication;

/// <summary>In-process writable replicas retaining concurrent histories and enforcing four causal session guarantees.</summary>
/// <typeparam name="TKey">Immutable keys with stable dictionary equality and hash codes.</typeparam>
/// <typeparam name="TValue">Borrowed immutable values; callers must not mutate them after writing.</typeparam>
/// <remarks><para><b>Ordering:</b> Each write depends on the entire local applied frontier. Session tokens enforce
/// read-your-writes, monotonic reads, monotonic writes, and write-follows-reads by rejecting insufficient replicas.
/// Explicit delivery permits partitions and buffering; reads do not fetch missing dependencies.</para>
/// <para><b>Scope:</b> A lock serializes in-process calls. Histories and pending messages are unbounded and volatile;
/// no disk, transport, membership changes, deletion, or Byzantine validation is implied. Delivery drains pending
/// writes by repeated scans; worst-case quadratic work in pending writes, multiplied by origin count.</para>
/// <para><b>Reference:</b> DDIA Ch. 6, multi-leader replication and detecting concurrent writes.</para></remarks>
/// <example>
/// <code>
/// var replicas = new MultiLeaderReplicaSet&lt;string, int&gt;(["west", "east"], new VersionVectorResolver&lt;int&gt;());
/// var session = replicas.Write("west", "key", 42);
/// foreach (var operation in replicas.Capture("west")) replicas.Deliver("east", operation);
/// var read = replicas.Read("east", "key", session);
/// </code>
/// </example>
public sealed class MultiLeaderReplicaSet<TKey, TValue> : IMultiLeaderReplication<TKey, TValue> where TKey : notnull
{
    private sealed class Replica
    {
        internal Dictionary<string, long> Frontier { get; } = new(StringComparer.Ordinal);
        internal Dictionary<(string Origin, long Sequence), ReplicationOperation<TKey, TValue>> Applied { get; } = [];
        internal Dictionary<(string Origin, long Sequence), ReplicationOperation<TKey, TValue>> Pending { get; } = [];
        internal long Lamport { get; set; }
    }
    private readonly object _gate = new();
    private readonly Guid _scope = Guid.NewGuid();
    private readonly Dictionary<string, Replica> _replicas = new(StringComparer.Ordinal);
    private readonly IConflictResolver<TValue> _resolver;

    /// <summary>Creates an empty fixed membership replica set, borrowing its pure conflict resolver.</summary>
    /// <param name="replicas">Nonempty, distinct, nonblank identifiers compared ordinally.</param>
    /// <param name="resolver">Pure deterministic resolution policy; no ownership transfer occurs.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">Membership is empty or contains duplicate/blank identifiers.</exception>
    public MultiLeaderReplicaSet(IEnumerable<string> replicas, IConflictResolver<TValue> resolver)
    {
        ArgumentNullException.ThrowIfNull(replicas);
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        foreach (var replica in replicas)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(replica);
            if (!_replicas.TryAdd(replica, new Replica()))
            {
                throw new ArgumentException("Duplicate replica identifier.", nameof(replicas));
            }
        }
        if (_replicas.Count == 0)
        {
            throw new ArgumentException("At least one replica is required.", nameof(replicas));
        }
    }

    /// <inheritdoc />
    public SessionToken Write(string replica, TKey key, TValue value, SessionToken? session = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var node = Require(replica);
            CheckSession(node, session);
            var sequence = checked(node.Frontier.GetValueOrDefault(replica) + 1);
            var timestamp = checked(node.Lamport + 1);
            var frontier = new Dictionary<string, long>(node.Frontier, StringComparer.Ordinal) { [replica] = sequence };
            var context = new SessionToken(_scope, frontier);
            Apply(node, new ReplicationOperation<TKey, TValue>(key, new ReplicationVersion<TValue>(value, replica, sequence, timestamp, context)));
            return context;
        }
    }

    /// <inheritdoc />
    public ReplicationRead<TValue> Read(string replica, TKey key, SessionToken? session = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var node = Require(replica);
            CheckSession(node, session);
            var frontier = new SessionToken(_scope, node.Frontier);
            var versions = node.Applied.Values.Where(op => EqualityComparer<TKey>.Default.Equals(op.Key, key)).Select(op => op.Version).ToArray();
            return new ReplicationRead<TValue>(_resolver.Resolve(Array.AsReadOnly(versions)), frontier);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ReplicationOperation<TKey, TValue>> Capture(string replica)
    {
        lock (_gate)
        {
            return Array.AsReadOnly(Require(replica).Applied.Values.OrderBy(op => op.Version.Origin, StringComparer.Ordinal).ThenBy(op => op.Version.Sequence).ToArray());
        }
    }

    /// <inheritdoc />
    public void Deliver(string replica, ReplicationOperation<TKey, TValue> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            var node = Require(replica);
            ValidateScope(operation.Version.Context);
            var id = (operation.Version.Origin, operation.Version.Sequence);
            if (node.Applied.ContainsKey(id))
            {
                return;
            }

            node.Pending.TryAdd(id, operation);
            bool progressed;
            do
            {
                progressed = false;
                foreach (var pending in node.Pending.Values.ToArray())
                {
                    var version = pending.Version;
                    if (!version.Context.Counters.All(pair => node.Frontier.GetValueOrDefault(pair.Key) >=
                        (pair.Key == version.Origin ? pair.Value - 1 : pair.Value)))
                    {
                        continue;
                    }

                    Apply(node, pending);
                    node.Pending.Remove((version.Origin, version.Sequence));
                    progressed = true;
                }
            } while (progressed);
        }
    }

    internal SessionToken Frontier(string replica)
    {
        lock (_gate)
        {
            return new SessionToken(_scope, Require(replica).Frontier);
        }
    }
    internal void ValidateScope(SessionToken? token)
    {
        if (token is not null && token.Scope != Guid.Empty && token.Scope != _scope)
        {
            throw new ArgumentException("The token or operation belongs to another replica set.", nameof(token));
        }
    }
    private Replica Require(string replica) => _replicas.TryGetValue(replica, out var node) ? node : throw new KeyNotFoundException($"Unknown replica: {replica}");
    private void CheckSession(Replica node, SessionToken? session)
    {
        ValidateScope(session);
        if (session is not null && !new SessionToken(_scope, node.Frontier).Covers(session))
        {
            throw new SessionConsistencyException();
        }
    }
    private static void Apply(Replica node, ReplicationOperation<TKey, TValue> operation)
    {
        var version = operation.Version;
        node.Applied.Add((version.Origin, version.Sequence), operation);
        node.Frontier[version.Origin] = version.Sequence;
        node.Lamport = Math.Max(node.Lamport, version.LamportTimestamp);
    }
}
