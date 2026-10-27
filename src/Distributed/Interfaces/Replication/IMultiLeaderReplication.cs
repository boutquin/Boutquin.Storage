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
using Boutquin.Storage.Distributed.Replication;

namespace Boutquin.Storage.Distributed.Interfaces.Replication;

/// <summary>Independent writable replicas with causal delivery, conflict resolution, and session frontiers.</summary>
/// <typeparam name="TKey">Immutable dictionary keys with stable equality and hash codes.</typeparam>
/// <typeparam name="TValue">Immutable or caller-owned payloads borrowed for the replica set's lifetime.</typeparam>
public interface IMultiLeaderReplication<TKey, TValue> where TKey : notnull
{
    /// <summary>Applies a local write after checking all session dependencies, without implicit replication.</summary>
    /// <param name="replica">The registered writable replica.</param>
    /// <param name="key">The non-null key.</param>
    /// <param name="value">The immutable payload.</param>
    /// <param name="session">Prior read/write frontier, or null for a fresh session.</param>
    /// <returns>The frontier after applying this write locally; no remote acknowledgment or durability is implied.</returns>
    /// <exception cref="KeyNotFoundException">The replica is unknown.</exception>
    /// <exception cref="ArgumentNullException">The key or replica identifier is null.</exception>
    /// <exception cref="ArgumentException">The session belongs to another replica set.</exception>
    /// <exception cref="SessionConsistencyException">The replica lacks required dependencies.</exception>
    SessionToken Write(string replica, TKey key, TValue value, SessionToken? session = null);

    /// <summary>Reads locally after checking the session frontier, preserving absence as an empty value list.</summary>
    /// <param name="replica">The registered replica.</param>
    /// <param name="key">The non-null key.</param>
    /// <param name="session">Prior read/write frontier, or null for a fresh session.</param>
    /// <returns>Resolved values and the frontier to carry to the next session operation.</returns>
    /// <exception cref="KeyNotFoundException">The replica is unknown.</exception>
    /// <exception cref="ArgumentNullException">The key or replica identifier is null.</exception>
    /// <exception cref="ArgumentException">The session belongs to another replica set.</exception>
    /// <exception cref="SessionConsistencyException">The replica lacks required dependencies.</exception>
    ReplicationRead<TValue> Read(string replica, TKey key, SessionToken? session = null);

    /// <summary>Captures applied writes for explicit transport; pending dependencies are excluded.</summary>
    /// <param name="replica">The registered source replica.</param>
    /// <returns>An immutable snapshot of borrowed immutable operations in origin/sequence order.</returns>
    /// <exception cref="KeyNotFoundException">The replica is unknown.</exception>
    /// <exception cref="ArgumentNullException">The replica identifier is null.</exception>
    IReadOnlyList<ReplicationOperation<TKey, TValue>> Capture(string replica);

    /// <summary>Receives one write idempotently, buffering it until every causal dependency is present.</summary>
    /// <param name="replica">The registered target replica.</param>
    /// <param name="operation">An operation captured from this replica set.</param>
    /// <exception cref="KeyNotFoundException">The replica is unknown.</exception>
    /// <exception cref="ArgumentNullException">The operation or replica identifier is null.</exception>
    /// <exception cref="ArgumentException">The operation belongs to another replica set.</exception>
    void Deliver(string replica, ReplicationOperation<TKey, TValue> operation);
}
