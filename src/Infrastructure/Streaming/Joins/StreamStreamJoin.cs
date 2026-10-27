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
using Boutquin.Storage.Domain.ValueObjects.Streaming;

namespace Boutquin.Storage.Infrastructure.Streaming.Joins;

/// <summary>
/// Performs a bounded event-time inner join over two finite streams. Both inputs are buffered;
/// this reference implementation is intended for finite replay and small-cluster composition.
/// </summary>
/// <typeparam name="TLeft">The left stream value type.</typeparam>
/// <typeparam name="TRight">The right stream value type.</typeparam>
/// <typeparam name="TKey">The non-null equality key type.</typeparam>
/// <typeparam name="TOut">The joined output value type.</typeparam>
/// <remarks>
/// Output offsets are provenance: each match carries the larger input offset. Multiple matches can
/// therefore share an offset or leave gaps. Append results to a fresh event log before passing them
/// to a checkpointed processor that requires a contiguous source.
/// </remarks>
public sealed class StreamStreamJoin<TLeft, TRight, TKey, TOut> : IStreamJoin<TLeft, TRight, TOut>
    where TKey : notnull
{
    private readonly TimeSpan _tolerance;
    private readonly Func<TLeft, TKey> _leftKey;
    private readonly Func<TRight, TKey> _rightKey;
    private readonly Func<TLeft, TRight, TOut> _project;

    /// <summary>Creates a symmetric event-time join.</summary>
    /// <param name="tolerance">The non-negative maximum timestamp distance for a match.</param>
    /// <param name="leftKey">Selects the left join key.</param>
    /// <param name="rightKey">Selects the right join key.</param>
    /// <param name="project">Combines one matching pair.</param>
    /// <exception cref="ArgumentOutOfRangeException">The tolerance is negative.</exception>
    /// <exception cref="ArgumentNullException">A delegate is <see langword="null"/>.</exception>
    public StreamStreamJoin(
        TimeSpan tolerance,
        Func<TLeft, TKey> leftKey,
        Func<TRight, TKey> rightKey,
        Func<TLeft, TRight, TOut> project)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tolerance, TimeSpan.Zero);

        _tolerance = tolerance;
        _leftKey = leftKey ?? throw new ArgumentNullException(nameof(leftKey));
        _rightKey = rightKey ?? throw new ArgumentNullException(nameof(rightKey));
        _project = project ?? throw new ArgumentNullException(nameof(project));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamRecord<TOut>> JoinAsync(
        IAsyncEnumerable<StreamRecord<TLeft>> left,
        IAsyncEnumerable<StreamRecord<TRight>> right,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var rightByKey = new Dictionary<TKey, List<StreamRecord<TRight>>>();
        await foreach (var record in right.WithCancellation(ct).ConfigureAwait(false))
        {
            var key = _rightKey(record.Value);
            if (!rightByKey.TryGetValue(key, out var records))
            {
                records = [];
                rightByKey.Add(key, records);
            }

            records.Add(record);
        }

        var results = new List<StreamRecord<TOut>>();
        await foreach (var leftRecord in left.WithCancellation(ct).ConfigureAwait(false))
        {
            if (!rightByKey.TryGetValue(_leftKey(leftRecord.Value), out var matches))
            {
                continue;
            }

            foreach (var rightRecord in matches)
            {
                var delta = (leftRecord.EventTime - rightRecord.EventTime).Duration();
                if (delta <= _tolerance)
                {
                    results.Add(new StreamRecord<TOut>(
                        Math.Max(leftRecord.Offset, rightRecord.Offset),
                        leftRecord.EventTime >= rightRecord.EventTime
                            ? leftRecord.EventTime
                            : rightRecord.EventTime,
                        _project(leftRecord.Value, rightRecord.Value)));
                }
            }
        }

        foreach (var result in results.OrderBy(result => result.EventTime).ThenBy(result => result.Offset))
        {
            yield return result;
        }
    }
}
