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
using System.Collections.ObjectModel;

namespace Boutquin.Storage.Distributed.Replication;

/// <summary>Immutable causal frontier carried between operations of one sequential client session.</summary>
/// <remarks>Retain each returned token and pass it to the next operation, including writes to different keys.
/// A replica must have applied every dependency before serving it. Tokens are scoped to one in-memory replica
/// set and cannot survive its destruction. Counters grow with membership; no unsafe pruning is performed.</remarks>
public sealed class SessionToken
{
    internal SessionToken(Guid scope, IReadOnlyDictionary<string, long> counters)
    {
        Scope = scope;
        Counters = new ReadOnlyDictionary<string, long>(new Dictionary<string, long>(counters, StringComparer.Ordinal));
    }

    /// <summary>An unbound session with no previously observed operations.</summary>
    public static SessionToken Empty { get; } = new(Guid.Empty, new Dictionary<string, long>());

    /// <summary>Applied per-origin sequence numbers; the dictionary is an immutable snapshot.</summary>
    public IReadOnlyDictionary<string, long> Counters { get; }

    internal Guid Scope { get; }

    /// <summary>Checks whether this frontier contains every operation required by another token.</summary>
    /// <param name="other">The frontier to satisfy.</param>
    /// <returns>True when every required counter has been reached.</returns>
    /// <exception cref="ArgumentNullException">The token is null.</exception>
    /// <exception cref="ArgumentException">Bound tokens belong to different replica sets.</exception>
    public bool Covers(SessionToken other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Scope != other.Scope && Scope != Guid.Empty && other.Scope != Guid.Empty)
        {
            throw new ArgumentException("Session tokens belong to different replica sets.", nameof(other));
        }

        return other.Counters.All(pair => Counters.GetValueOrDefault(pair.Key) >= pair.Value);
    }
}
