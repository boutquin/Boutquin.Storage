// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System.Collections.ObjectModel;
using Boutquin.Storage.Domain.Interfaces.Analytics;
using Boutquin.Storage.Domain.Interfaces.Events;
using Boutquin.Storage.Domain.Interfaces.Streaming;
using Boutquin.Storage.Infrastructure.Events;

namespace Boutquin.Storage.Infrastructure.Analytics;

/// <summary>Maintains per-key aggregates from an append-only log with durable replay position.</summary>
/// <typeparam name="TKey">The non-null immutable grouping key with stable equality and hash codes.</typeparam>
/// <typeparam name="TValue">The immutable event value and per-key aggregate value, borrowed without deep copying.</typeparam>
/// <remarks>Each refresh checkpoints state after every event through the projection runner.
/// Rebuild computes an isolated state and publishes it only after complete replay. The view borrows
/// its log and checkpoint store and does not dispose them. Dictionary structure is immutable throughout
/// folding and publication; decoded dictionaries are copied with the configured comparer before use.
/// Keys and values remain caller-owned immutable objects, not deep clones. Previous snapshots remain
/// unchanged when a later refresh or rebuild publishes a new state.</remarks>
public sealed class MaterializedAggregateView<TKey, TValue>
    : IMaterializedView<IReadOnlyDictionary<TKey, TValue>>
    where TKey : notnull
{
    private readonly string _processorId;
    private readonly ICheckpointStore _checkpoints;
    private readonly Func<IReadOnlyDictionary<TKey, TValue>, byte[]> _serialize;
    private readonly AggregateProjection _projection;
    private readonly ProjectionRunner<KeyValuePair<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>> _runner;
    private readonly ProjectionRebuilder<KeyValuePair<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>> _rebuilder;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public ProjectionCheckpoint<IReadOnlyDictionary<TKey, TValue>> Current { get; private set; }

    /// <summary>Creates an incrementally refreshable aggregate view.</summary>
    /// <param name="processorId">Stable checkpoint identity unique to this view and log.</param>
    /// <param name="log">Ordered contiguous input log, borrowed from the caller.</param>
    /// <param name="checkpoints">Store that durably couples state bytes with the input offset.</param>
    /// <param name="aggregate">Pure deterministic combiner for subsequent values of the same key; must not mutate either input.</param>
    /// <param name="serialize">Deterministic encoder receiving a structurally immutable state dictionary.</param>
    /// <param name="deserialize">State decoder inverse to <paramref name="serialize"/>; its returned dictionary is copied before use.</param>
    /// <param name="keyComparer">Stable key equality comparer applied to new and decoded state, or the default comparer when null.</param>
    /// <exception cref="ArgumentException"><paramref name="processorId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">A required collaborator is null.</exception>
    public MaterializedAggregateView(string processorId, IEventLog<KeyValuePair<TKey, TValue>> log,
        ICheckpointStore checkpoints, Func<TValue, TValue, TValue> aggregate,
        Func<IReadOnlyDictionary<TKey, TValue>, byte[]> serialize,
        Func<byte[], IReadOnlyDictionary<TKey, TValue>> deserialize,
        IEqualityComparer<TKey>? keyComparer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(serialize);
        ArgumentNullException.ThrowIfNull(deserialize);
        _processorId = processorId;
        _checkpoints = checkpoints;
        _serialize = serialize;
        _projection = new AggregateProjection(aggregate, keyComparer ?? EqualityComparer<TKey>.Default);
        _runner = new ProjectionRunner<KeyValuePair<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>>(
            processorId, log, _projection, checkpoints, serialize, bytes => _projection.CopyState(deserialize(bytes)));
        _rebuilder = new ProjectionRebuilder<KeyValuePair<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>>(log, _projection);
        Current = new ProjectionCheckpoint<IReadOnlyDictionary<TKey, TValue>>(-1, _projection.InitialState);
    }

    /// <inheritdoc />
    public async ValueTask<ProjectionCheckpoint<IReadOnlyDictionary<TKey, TValue>>> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Current = await _runner.RunAsync(cancellationToken).ConfigureAwait(false);
            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ProjectionCheckpoint<IReadOnlyDictionary<TKey, TValue>>> RebuildAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rebuilt = await _rebuilder.RebuildAsync(cancellationToken).ConfigureAwait(false);
            var bytes = _serialize(rebuilt.State);
            ArgumentNullException.ThrowIfNull(bytes);
            await _checkpoints.SaveAsync(_processorId, rebuilt.Position, bytes, cancellationToken).ConfigureAwait(false);
            Current = rebuilt;
            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class AggregateProjection(Func<TValue, TValue, TValue> aggregate,
        IEqualityComparer<TKey> comparer)
        : IProjection<KeyValuePair<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>>
    {
        public IReadOnlyDictionary<TKey, TValue> InitialState =>
            new ReadOnlyDictionary<TKey, TValue>(new Dictionary<TKey, TValue>(comparer));

        // A decoder may retain its mutable dictionary, and an empty log suffix skips Apply.
        // Copy before the runner can publish that state or hand it to any later transition.
        public IReadOnlyDictionary<TKey, TValue> CopyState(IReadOnlyDictionary<TKey, TValue> state) =>
            new ReadOnlyDictionary<TKey, TValue>(new Dictionary<TKey, TValue>(state, comparer));

        public IReadOnlyDictionary<TKey, TValue> Apply(IReadOnlyDictionary<TKey, TValue> state,
            KeyValuePair<TKey, TValue> @event)
        {
            var next = new Dictionary<TKey, TValue>(state, comparer);
            next[@event.Key] = next.TryGetValue(@event.Key, out var current)
                ? aggregate(current, @event.Value)
                : @event.Value;
            return new ReadOnlyDictionary<TKey, TValue>(next);
        }
    }
}
