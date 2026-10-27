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
using System.Globalization;

using Boutquin.Storage.Domain.Interfaces.Streaming;

namespace Boutquin.Storage.Infrastructure.Streaming;

/// <summary>
/// Couples deterministic operation ids with a deduplicating sink. Checkpoints remain separate,
/// so replay can repeat processing, while the sink makes externally visible output effectively once.
/// </summary>
/// <typeparam name="TIn">The source event value type.</typeparam>
/// <typeparam name="TState">The immutable fold state serialized into each checkpoint.</typeparam>
/// <typeparam name="TOut">The idempotent sink output value type.</typeparam>
/// <remarks>
/// Effective-once output requires a durable sink that remembers operation IDs across process
/// failure. One instance permits one active run. The transition must be pure and treat state as
/// borrowed and immutable. All collaborators are borrowed and remain caller-owned.
/// </remarks>
public sealed class EffectivelyOnceProcessor<TIn, TState, TOut>
{
    private readonly string _processorId;
    private readonly IStream<TIn> _stream;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IIdempotentStreamSink<TOut> _sink;
    private readonly TState _initialState;
    private readonly Func<TState, TIn, (TState State, TOut Output)> _transition;
    private readonly Func<TState, byte[]> _serializeState;
    private readonly Func<byte[], TState> _deserializeState;
    private int _running;

    /// <summary>Creates an inert effectively-once processor without reading or writing collaborators.</summary>
    /// <param name="processorId">Stable checkpoint identity and operation-ID prefix.</param>
    /// <param name="stream">A zero-based stream with contiguous, increasing offsets.</param>
    /// <param name="checkpointStore">The store that atomically saves state with its source offset.</param>
    /// <param name="sink">A sink that durably deduplicates stable operation IDs.</param>
    /// <param name="initialState">The immutable state used when no checkpoint exists.</param>
    /// <param name="transition">A pure deterministic function that returns new state and output.</param>
    /// <param name="serializeState">A deterministic state encoder invoked before publishing output.</param>
    /// <param name="deserializeState">A decoder that rejects corrupt or incompatible checkpoint bytes.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="processorId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">Thrown when a required collaborator is <see langword="null"/>.</exception>
    public EffectivelyOnceProcessor(
        string processorId,
        IStream<TIn> stream,
        ICheckpointStore checkpointStore,
        IIdempotentStreamSink<TOut> sink,
        TState initialState,
        Func<TState, TIn, (TState State, TOut Output)> transition,
        Func<TState, byte[]> serializeState,
        Func<byte[], TState> deserializeState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        _processorId = processorId;
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _initialState = initialState;
        _transition = transition ?? throw new ArgumentNullException(nameof(transition));
        _serializeState = serializeState ?? throw new ArgumentNullException(nameof(serializeState));
        _deserializeState = deserializeState ?? throw new ArgumentNullException(nameof(deserializeState));
        CurrentState = initialState;
    }

    /// <summary>Gets the state paired with the last successfully saved checkpoint.</summary>
    public TState CurrentState { get; private set; }

    /// <summary>Replays from the last checkpoint and returns the last committed offset.</summary>
    /// <param name="ct">Cancels between source, sink, and checkpoint operations.</param>
    /// <returns>The last checkpointed offset, or <c>-1</c> when no source record was processed.</returns>
    /// <exception cref="InvalidOperationException">Thrown when another run on this instance is active.</exception>
    /// <exception cref="InvalidDataException">Thrown for an invalid checkpoint or non-contiguous source offset.</exception>
    public async ValueTask<long> RunAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("A stream processor run is already active.");
        }

        try
        {
            var checkpoint = await _checkpointStore.LoadAsync(_processorId, ct).ConfigureAwait(false);
            var offset = checkpoint?.Offset ?? -1;
            if (offset < -1 || offset == long.MaxValue)
            {
                throw new InvalidDataException("The stream checkpoint offset cannot be resumed.");
            }

            // Round-trip the baseline before every fresh run so a failed transition cannot retain
            // an aliased mutable initial-state instance across retries.
            CurrentState = checkpoint is null
                ? _deserializeState(SerializeState(_initialState))
                : _deserializeState(checkpoint.Value.State);
            await foreach (var record in _stream.ReadAsync(offset + 1, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (record.Offset != offset + 1 || record.Offset == long.MaxValue)
                {
                    throw new InvalidDataException("Stream offsets must be contiguous and increasing.");
                }

                var next = _transition(CurrentState, record.Value);
                var bytes = SerializeState(next.State);
                var operationId = string.Concat(
                    _processorId,
                    ":",
                    record.Offset.ToString(CultureInfo.InvariantCulture));
                await _sink.WriteAsync(operationId, next.Output, ct).ConfigureAwait(false);
                await _checkpointStore.SaveAsync(
                    _processorId, record.Offset, bytes, ct).ConfigureAwait(false);
                CurrentState = next.State;
                offset = record.Offset;
            }

            return offset;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private byte[] SerializeState(TState state)
    {
        var bytes = _serializeState(state);
        ArgumentNullException.ThrowIfNull(bytes);
        return bytes;
    }
}
