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

/// <summary>Retains concurrent siblings and removes versions causally superseded by another write.</summary>
/// <typeparam name="T">The immutable payload type.</typeparam>
/// <remarks>Vector dominance, rather than a wall clock, distinguishes causality from conflict (DDIA Ch. 6).
/// Comparing v versions over n origins costs O(v²n). The stable origin/sequence ordering is not a winner policy.</remarks>
public sealed class VersionVectorResolver<T> : IConflictResolver<T>
{
    /// <summary>Creates a stateless resolver without background work or owned resources.</summary>
    public VersionVectorResolver() { }
    /// <inheritdoc />
    public IReadOnlyList<T> Resolve(IReadOnlyList<ReplicationVersion<T>> versions)
        => Array.AsReadOnly(Maximal(versions).Select(version => version.Value).ToArray());

    internal static ReplicationVersion<T>[] Maximal(IReadOnlyList<ReplicationVersion<T>> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        Guid? scope = null;
        foreach (var version in versions)
        {
            ArgumentNullException.ThrowIfNull(version);
            if (scope.HasValue && scope.Value != version.Context.Scope)
            {
                throw new ArgumentException("Versions belong to different replica sets.", nameof(versions));
            }
            scope = version.Context.Scope;
        }

        var unique = versions.DistinctBy(v => (v.Origin, v.Sequence)).ToArray();
        return unique.Where(v => !unique.Any(other => !ReferenceEquals(v, other) && other.Context.Covers(v.Context)))
            .OrderBy(v => v.Origin, StringComparer.Ordinal).ThenBy(v => v.Sequence).ToArray();
    }
}
