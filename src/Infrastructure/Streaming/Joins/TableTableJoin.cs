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
using Boutquin.Storage.Domain.Interfaces.Streaming;

namespace Boutquin.Storage.Infrastructure.Streaming.Joins;

/// <summary>Computes an inner join over point-in-time snapshots of two state tables.</summary>
/// <typeparam name="TKey">The non-null common key type.</typeparam>
/// <typeparam name="TLeft">The left table value type.</typeparam>
/// <typeparam name="TRight">The right table value type.</typeparam>
/// <typeparam name="TOut">The joined output value type.</typeparam>
/// <remarks>Each table snapshot is internally consistent; the pair is captured sequentially.</remarks>
public sealed class TableTableJoin<TKey, TLeft, TRight, TOut>
    where TKey : notnull
{
    private readonly IStateStore<TKey, TLeft> _left;
    private readonly IStateStore<TKey, TRight> _right;
    private readonly Func<TKey, TLeft, TRight, TOut> _project;

    /// <summary>Creates an inert table-table inner join over borrowed state stores.</summary>
    /// <param name="left">The left table.</param>
    /// <param name="right">The right table.</param>
    /// <param name="project">Combines a key and its two table values.</param>
    /// <exception cref="ArgumentNullException">A collaborator is <see langword="null"/>.</exception>
    public TableTableJoin(
        IStateStore<TKey, TLeft> left,
        IStateStore<TKey, TRight> right,
        Func<TKey, TLeft, TRight, TOut> project)
    {
        _left = left ?? throw new ArgumentNullException(nameof(left));
        _right = right ?? throw new ArgumentNullException(nameof(right));
        _project = project ?? throw new ArgumentNullException(nameof(project));
    }

    /// <summary>Joins keys present in both snapshots.</summary>
    /// <param name="ct">Cancels snapshot acquisition.</param>
    /// <returns>A task that completes with one output for every common key.</returns>
    public async ValueTask<IReadOnlyList<TOut>> JoinAsync(CancellationToken ct = default)
    {
        var left = await _left.SnapshotAsync(ct).ConfigureAwait(false);
        var right = await _right.SnapshotAsync(ct).ConfigureAwait(false);
        var output = new List<TOut>();
        foreach (var (key, leftValue) in left)
        {
            if (right.TryGetValue(key, out var rightValue))
            {
                output.Add(_project(key, leftValue, rightValue));
            }
        }

        return output;
    }
}
