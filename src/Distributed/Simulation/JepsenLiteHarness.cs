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

/// <summary>Schedules client operations and records invocation/response boundaries under virtual time.</summary>
/// <typeparam name="TInput">Immutable command type.</typeparam>
/// <typeparam name="TOutput">Immutable response type.</typeparam>
/// <remarks>Borrowed runtime; no owned component resources. This is an in-process Jepsen-inspired harness,
/// not Jepsen itself. The operation delegate completes through its response callback, allowing delayed
/// acknowledgments and overlapping operations. A lost response stays pending and yields Inconclusive.</remarks>
public sealed class JepsenLiteHarness<TInput, TOutput>
{
    private readonly SimulatedNodeRuntime _runtime;

    /// <summary>Attaches an empty client history to an existing deterministic runtime.</summary>
    /// <param name="runtime">Non-null borrowed runtime.</param>
    /// <exception cref="ArgumentNullException">Runtime is null.</exception>
    public JepsenLiteHarness(SimulatedNodeRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <summary>Operation history, including unresolved responses.</summary>
    public HistoryRecorder<TInput, TOutput> History { get; } = new();

    /// <summary>Invokes an operation on a registered client after a virtual delay.</summary>
    /// <param name="client">Registered client endpoint; pauses delay invocations.</param>
    /// <param name="delay">Nonnegative offset from current time.</param>
    /// <param name="input">Immutable command.</param>
    /// <param name="operation">Synchronous start function receiving command and one-shot response callback.</param>
    /// <exception cref="ArgumentNullException">Operation is null.</exception>
    /// <exception cref="KeyNotFoundException">Client is unknown.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Delay is negative.</exception>
    public void Schedule(string client, long delay, TInput input, Action<TInput, Action<TOutput>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _runtime.Schedule(client, delay, () =>
        {
            var id = History.Invoke(input);
            operation(input, output => History.Complete(id, output));
        });
    }
}

