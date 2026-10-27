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
namespace Boutquin.Storage.Distributed.Simulation;

/// <summary>A client operation's invocation and optional definitive response.</summary>
/// <typeparam name="TInput">Immutable command type.</typeparam>
/// <typeparam name="TOutput">Immutable response type.</typeparam>
/// <param name="Id">Recorder-local operation identity.</param>
/// <param name="Input">Command passed at invocation.</param>
/// <param name="Output">Response, meaningful only when Completed is true.</param>
/// <param name="Start">Strictly increasing invocation event ordinal.</param>
/// <param name="End">Response ordinal, or null for an uncertain/in-flight operation.</param>
/// <param name="Completed">True for a successful response, false for a pending or definitively rejected operation.</param>
/// <remarks>Ordinals capture real-time precedence even when operations share a virtual tick.
/// An End with Completed false asserts rejection without effects. Timeouts must stay pending.</remarks>
public sealed record HistoryOperation<TInput, TOutput>(
    long Id, TInput Input, TOutput? Output, long Start, long? End, bool Completed);

/// <summary>Pairs client invocations with responses without confusing timeout with rejection.</summary>
/// <typeparam name="TInput">Immutable command type, borrowed rather than cloned.</typeparam>
/// <typeparam name="TOutput">Immutable response type, borrowed rather than cloned.</typeparam>
/// <remarks>Single-driver recorder. Snapshots copy the operation list; inputs/outputs must be immutable.
/// Event ordinals, rather than clock ticks, preserve response-before-invocation ordering within a tick.</remarks>
public sealed class HistoryRecorder<TInput, TOutput>
{
    private readonly List<HistoryOperation<TInput, TOutput>> _operations = [];
    private long _ordinal;

    /// <summary>Records an invocation and returns its unique recorder-local identity.</summary>
    /// <param name="input">Immutable command.</param>
    /// <returns>Identity used exactly once by Complete or Reject.</returns>
    public long Invoke(TInput input)
    {
        var id = _operations.Count;
        _operations.Add(new HistoryOperation<TInput, TOutput>(id, input, default, checked(++_ordinal), null, false));
        return id;
    }

    /// <summary>Records a successful response after its externally visible effect.</summary>
    /// <param name="id">Pending invocation identity.</param>
    /// <param name="output">Immutable response.</param>
    /// <exception cref="ArgumentOutOfRangeException">The identity is unknown.</exception>
    /// <exception cref="InvalidOperationException">The operation already responded.</exception>
    public void Complete(long id, TOutput output) => Finish(id, output, true);

    /// <summary>Records definitive rejection with no effect; uncertain timeouts must remain pending.</summary>
    /// <param name="id">Pending invocation identity.</param>
    /// <exception cref="ArgumentOutOfRangeException">The identity is unknown.</exception>
    /// <exception cref="InvalidOperationException">The operation already responded.</exception>
    public void Reject(long id) => Finish(id, default, false);

    /// <summary>Copies the current immutable records, including in-flight operations.</summary>
    /// <returns>An independent read-only operation list in invocation order.</returns>
    public IReadOnlyList<HistoryOperation<TInput, TOutput>> Snapshot() => _operations.AsReadOnly().ToArray();

    private void Finish(long id, TOutput? output, bool completed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, _operations.Count);
        var operation = _operations[(int)id];
        if (operation.End.HasValue)
        {
            throw new InvalidOperationException("An operation can respond only once.");
        }
        _operations[(int)id] = operation with { End = checked(++_ordinal), Output = output, Completed = completed };
    }
}

