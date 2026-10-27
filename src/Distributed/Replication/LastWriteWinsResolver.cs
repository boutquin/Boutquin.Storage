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

/// <summary>Selects the greatest Lamport timestamp, breaking concurrent ties by ordinal origin identifier.</summary>
/// <typeparam name="T">The immutable payload type.</typeparam>
/// <remarks>This deterministic logical last-writer-wins policy loses concurrent values by design. It does
/// not claim wall-clock recency or linearizability. Resolution costs O(v²n) to discard causally old versions.</remarks>
public sealed class LastWriteWinsResolver<T> : IConflictResolver<T>
{
    /// <summary>Creates a stateless resolver without clock or resource ownership.</summary>
    public LastWriteWinsResolver() { }
    /// <inheritdoc />
    public IReadOnlyList<T> Resolve(IReadOnlyList<ReplicationVersion<T>> versions)
    {
        var maximal = VersionVectorResolver<T>.Maximal(versions);
        if (maximal.Length == 0)
        {
            return Array.Empty<T>();
        }

        var winner = maximal.OrderBy(v => v.LamportTimestamp).ThenBy(v => v.Origin, StringComparer.Ordinal).ThenBy(v => v.Sequence).Last();
        return Array.AsReadOnly(new[] { winner.Value });
    }
}
