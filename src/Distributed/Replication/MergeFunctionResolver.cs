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

/// <summary>Applies an application merge to the canonical set of concurrent, causally maximal values.</summary>
/// <typeparam name="T">The immutable payload type.</typeparam>
/// <remarks>The merge is evaluated from original siblings each time, never from previous merged outputs,
/// so duplicate delivery cannot repeatedly add a value. Costs include O(v²n) dominance checking plus the callback.</remarks>
public sealed class MergeFunctionResolver<T> : IConflictResolver<T>
{
    private readonly Func<IReadOnlyList<T>, T> _merge;
    /// <summary>Creates a resolver borrowing a pure deterministic merge callback.</summary>
    /// <param name="merge">Receives nonempty siblings in ordinal origin/sequence order; must not mutate them.</param>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    public MergeFunctionResolver(Func<IReadOnlyList<T>, T> merge)
    {
        ArgumentNullException.ThrowIfNull(merge);
        _merge = merge;
    }
    /// <inheritdoc />
    public IReadOnlyList<T> Resolve(IReadOnlyList<ReplicationVersion<T>> versions)
    {
        var values = new VersionVectorResolver<T>().Resolve(versions);
        return values.Count == 0 ? Array.Empty<T>() : Array.AsReadOnly(new[] { _merge(values) });
    }
}
