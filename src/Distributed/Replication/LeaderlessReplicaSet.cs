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
using System.Buffers.Binary;
using Boutquin.Storage.Distributed.Merkle;

namespace Boutquin.Storage.Distributed.Replication;

/// <summary>Leaderless in-process replicas with tunable responses, sloppy substitution, hints, and causal repair.</summary>
/// <typeparam name="TKey">Immutable keys with stable equality and hash codes.</typeparam>
/// <typeparam name="TValue">Borrowed immutable values that must not change after writing.</typeparam>
/// <remarks><para><b>Protocol:</b> Home order is a fixed preference list for every key. Only the required number
/// of available nodes respond. Sloppy mode fills missing responses from spares and retains per-target hints.
/// Read repair synchronously merges responders' complete causal histories; anti-entropy repairs cold keys
/// using existing Merkle trees over aligned immutable operation identities.</para>
/// <para><b>Boundaries:</b> Quorum overlap does not imply linearizability, and sloppy reads can miss successful
/// sloppy writes. Sessions reject insufficient frontiers instead of promising partition availability.
/// Calls are serialized by one in-process lock; availability and acknowledgments are simulated, not network IO.
/// Histories/hints are volatile and unbounded. No deletion, membership change, or process-restart recovery exists.</para>
/// <para><b>Cost:</b> Repair rebuilds range trees (O(h log h) hashes), exchanging only missing operations after
/// comparison. Ordinary reads copy complete responder histories, trading efficiency for explicit causality.</para>
/// <para><b>Reference:</b> DDIA Ch. 6, leaderless replication, sloppy quorums, hinted handoff, and read repair.</para></remarks>
/// <example>
/// <code>
/// var replicas = new LeaderlessReplicaSet&lt;string, int&gt;(["a", "b", "c"], ["spare"], new VersionVectorResolver&lt;int&gt;());
/// var session = replicas.Write("key", 42, ReplicationConsistency.Quorum);
/// var read = replicas.Read("key", ReplicationConsistency.Quorum, session: session);
/// </code>
/// </example>
public sealed class LeaderlessReplicaSet<TKey, TValue> where TKey : notnull
{
    private sealed record Hint(string Holder, string Target, IReadOnlyList<ReplicationOperation<TKey, TValue>> Operations);
    private readonly object _gate = new();
    private readonly string[] _homes;
    private readonly string[] _spares;
    private readonly HashSet<string> _available;
    private readonly List<Hint> _hints = [];
    private readonly MultiLeaderReplicaSet<TKey, TValue> _replicas;

    /// <summary>Creates a fixed home set and disjoint spare set, borrowing a pure deterministic resolver.</summary>
    /// <param name="homes">Nonempty home preference list with distinct nonblank identifiers.</param>
    /// <param name="spares">Disjoint spare preference list; may be empty.</param>
    /// <param name="resolver">Conflict policy; values and callbacks remain caller owned.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">Identifiers overlap, are blank, or homes are empty.</exception>
    public LeaderlessReplicaSet(IEnumerable<string> homes, IEnumerable<string> spares, IConflictResolver<TValue> resolver)
    {
        ArgumentNullException.ThrowIfNull(homes);
        ArgumentNullException.ThrowIfNull(spares);
        _homes = homes.ToArray();
        _spares = spares.ToArray();
        if (_homes.Length == 0)
        {
            throw new ArgumentException("At least one home replica is required.", nameof(homes));
        }

        _replicas = new MultiLeaderReplicaSet<TKey, TValue>(_homes.Concat(_spares), resolver);
        _available = new HashSet<string>(_homes.Concat(_spares), StringComparer.Ordinal);
    }

    /// <summary>The number of outstanding handoff deliveries; a delivery may contain multiple dependencies.</summary>
    public int PendingHintCount { get { lock (_gate) { return _hints.Count; } } }

    /// <summary>Changes simulated endpoint reachability without deleting its stored history or hints.</summary>
    /// <param name="replica">A registered home or spare.</param>
    /// <param name="available">Whether operations may contact this replica.</param>
    /// <exception cref="KeyNotFoundException">The replica is unknown.</exception>
    /// <exception cref="ArgumentNullException">The replica identifier is null.</exception>
    public void SetAvailability(string replica, bool available)
    {
        lock (_gate)
        {
            _replicas.Frontier(replica);
            if (available)
            {
                _available.Add(replica);
            }
            else
            {
                _available.Remove(replica);
            }
        }
    }

    /// <summary>Writes to the required responders, retaining hints for unavailable homes replaced by spares.</summary>
    /// <param name="key">The non-null key.</param>
    /// <param name="value">The borrowed immutable payload.</param>
    /// <param name="consistency">Required acknowledgments relative to home count.</param>
    /// <param name="sloppy">Whether spare nodes may replace unavailable homes.</param>
    /// <param name="session">Prior causal frontier, or null for a fresh session.</param>
    /// <returns>The frontier applied by all responding nodes before returning; no durability is implied.</returns>
    /// <exception cref="ArgumentNullException">The key is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Consistency is unknown.</exception>
    /// <exception cref="ArgumentException">The token belongs to another replica set.</exception>
    /// <exception cref="QuorumNotMetException">Too few responders are reachable; no write is applied.</exception>
    /// <exception cref="SessionConsistencyException">Responders lack dependencies; no write is applied.</exception>
    /// <remarks>Repeating this method creates a new write. Retrying delivery of a captured operation is idempotent.
    /// Acknowledgments are deterministic local state applications, not claims about a real distributed timeout.</remarks>
    public SessionToken Write(TKey key, TValue value, ReplicationConsistency consistency, bool sloppy = false, SessionToken? session = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var selected = Select(consistency, sloppy, "Write");
            RequireSession(selected, session);
            MergeResponders(selected);
            var token = _replicas.Write(selected[0], key, value, session);
            var operations = _replicas.Capture(selected[0]);
            foreach (var target in selected.Skip(1))
            {
                DeliverAll(target, operations);
            }

            var missingHomes = new Queue<string>(_homes.Where(home => !_available.Contains(home)));
            foreach (var holder in selected.Where(node => _spares.Contains(node, StringComparer.Ordinal)))
            {
                _hints.Add(new Hint(holder, missingHomes.Dequeue(), operations));
            }

            return token;
        }
    }

    /// <summary>Resolves responder histories and completes synchronous read repair before returning.</summary>
    /// <param name="key">The non-null key.</param>
    /// <param name="consistency">Required responses relative to home count.</param>
    /// <param name="sloppy">Whether spare nodes may replace unavailable homes.</param>
    /// <param name="session">Prior causal frontier; insufficient responses are rejected rather than stale.</param>
    /// <returns>Resolved values plus the merged responder frontier.</returns>
    /// <exception cref="ArgumentNullException">The key is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Consistency is unknown.</exception>
    /// <exception cref="ArgumentException">The token belongs to another replica set.</exception>
    /// <exception cref="QuorumNotMetException">Too few responders are reachable.</exception>
    /// <exception cref="SessionConsistencyException">Responders lack dependencies.</exception>
    public ReplicationRead<TValue> Read(TKey key, ReplicationConsistency consistency, bool sloppy = false, SessionToken? session = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var selected = Select(consistency, sloppy, "Read");
            RequireSession(selected, session);
            MergeResponders(selected);
            return _replicas.Read(selected[0], key, session);
        }
    }

    /// <summary>Inspects an individual reachable replica without read repair, making lag observable.</summary>
    /// <param name="replica">A registered reachable node.</param>
    /// <param name="key">The non-null key.</param>
    /// <param name="session">Optional required frontier.</param>
    /// <returns>The local resolved values and frontier.</returns>
    /// <exception cref="KeyNotFoundException">The node is unknown.</exception>
    /// <exception cref="ArgumentNullException">The key is null.</exception>
    /// <exception cref="ArgumentException">The token belongs to another replica set.</exception>
    /// <exception cref="QuorumNotMetException">The node is unavailable.</exception>
    /// <exception cref="SessionConsistencyException">The node lacks required dependencies.</exception>
    public ReplicationRead<TValue> ReadReplica(string replica, TKey key, SessionToken? session = null)
    {
        lock (_gate)
        {
            RequireAvailable(replica);
            return _replicas.Read(replica, key, session);
        }
    }

    /// <summary>Delivers retained hints only when both the holder and target are reachable.</summary>
    /// <returns>The number of acknowledged hints removed; repeated calls are safe.</returns>
    /// <remarks>Offline targets retain their hints. No timer automatically expires hints and no background
    /// thread invokes this method. The caller controls retry scheduling, including deterministic simulations.</remarks>
    public int Handoff()
    {
        lock (_gate)
        {
            var delivered = 0;
            foreach (var hint in _hints.ToArray())
            {
                if (!_available.Contains(hint.Holder) || !_available.Contains(hint.Target))
                {
                    continue;
                }

                DeliverAll(hint.Target, hint.Operations);
                _hints.Remove(hint);
                delivered++;
            }
            return delivered;
        }
    }

    /// <summary>Compares aligned Merkle ranges and exchanges missing writes in both directions.</summary>
    /// <param name="first">First registered reachable replica.</param>
    /// <param name="second">Second registered reachable replica.</param>
    /// <returns>The number of missing operations transferred, including causal dependencies; zero if equal.</returns>
    /// <exception cref="KeyNotFoundException">A node is unknown.</exception>
    /// <exception cref="QuorumNotMetException">A node is unavailable.</exception>
    /// <remarks>Hashes encode membership of immutable operation identities within this trusted replica set.
    /// They do not detect caller mutation of borrowed payloads or disk corruption. Rebuilding each compared
    /// range uses the existing MerkleTree implementation, favoring clarity over incremental range indexes.</remarks>
    /// <exception cref="ArgumentNullException">A replica identifier is null.</exception>
    public int Repair(string first, string second)
    {
        lock (_gate)
        {
            RequireAvailable(first);
            RequireAvailable(second);
            var left = _replicas.Capture(first).ToDictionary(op => (op.Version.Origin, op.Version.Sequence));
            var right = _replicas.Capture(second).ToDictionary(op => (op.Version.Origin, op.Version.Sequence));
            var ids = left.Keys.Union(right.Keys).OrderBy(id => id.Origin, StringComparer.Ordinal).ThenBy(id => id.Sequence).ToArray();
            var transferred = 0;
            Compare(0, ids.Length);
            return transferred;

            void Compare(int offset, int count)
            {
                if (count == 0)
                {
                    return;
                }

                var leftTree = new MerkleTree();
                var rightTree = new MerkleTree();
                leftTree.Build(ids.Skip(offset).Take(count).Select(id => Identity(id, left.ContainsKey(id))).ToArray());
                rightTree.Build(ids.Skip(offset).Take(count).Select(id => Identity(id, right.ContainsKey(id))).ToArray());
                if (leftTree.RootHash.AsSpan().SequenceEqual(rightTree.RootHash))
                {
                    return;
                }

                if (count == 1)
                {
                    var id = ids[offset];
                    if (left.TryGetValue(id, out var operation))
                    {
                        _replicas.Deliver(second, operation);
                    }
                    else
                    {
                        _replicas.Deliver(first, right[id]);
                    }

                    transferred++;
                    return;
                }
                var half = count / 2;
                Compare(offset, half);
                Compare(offset + half, count - half);
            }
        }
    }

    private static byte[] Identity((string Origin, long Sequence) id, bool present)
    {
        if (!present)
        {
            return [0];
        }

        var origin = Encoding.UTF8.GetBytes(id.Origin);
        var bytes = new byte[checked(1 + sizeof(int) + origin.Length + sizeof(long))];
        bytes[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), origin.Length);
        origin.CopyTo(bytes, 1 + sizeof(int));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(1 + sizeof(int) + origin.Length), id.Sequence);
        return bytes;
    }
    private string[] Select(ReplicationConsistency consistency, bool sloppy, string operation)
    {
        var required = consistency switch
        {
            ReplicationConsistency.One => 1,
            ReplicationConsistency.Quorum => _homes.Length / 2 + 1,
            ReplicationConsistency.All => _homes.Length,
            _ => throw new ArgumentOutOfRangeException(nameof(consistency)),
        };
        var preference = sloppy ? _homes.Concat(_spares) : _homes;
        var selected = preference.Where(_available.Contains).Take(required).ToArray();
        if (selected.Length < required)
        {
            throw new QuorumNotMetException(operation, selected.Length, required);
        }

        return selected;
    }
    private void RequireAvailable(string replica)
    {
        _replicas.Frontier(replica);
        if (!_available.Contains(replica))
        {
            throw new QuorumNotMetException("Contact", 0, 1);
        }
    }
    private void RequireSession(string[] selected, SessionToken? session)
    {
        _replicas.ValidateScope(session);
        if (session is null)
        {
            return;
        }

        var frontiers = selected.Select(_replicas.Frontier).ToArray();
        if (!session.Counters.All(pair => frontiers.Any(frontier => frontier.Counters.GetValueOrDefault(pair.Key) >= pair.Value)))
        {
            throw new SessionConsistencyException();
        }
    }
    private void MergeResponders(string[] selected)
    {
        var operations = selected.SelectMany(_replicas.Capture).DistinctBy(op => (op.Version.Origin, op.Version.Sequence)).ToArray();
        foreach (var target in selected)
        {
            DeliverAll(target, operations);
        }
    }
    private void DeliverAll(string target, IReadOnlyList<ReplicationOperation<TKey, TValue>> operations)
    {
        foreach (var operation in operations)
        {
            _replicas.Deliver(target, operation);
        }
    }
}
