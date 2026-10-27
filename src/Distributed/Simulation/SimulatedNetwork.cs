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

/// <summary>Seeded virtual transport supporting loss, duplication, reordering, and symmetric partitions.</summary>
/// <remarks>Payloads are borrowed and must remain immutable after Send. Each duplicate has an independent
/// delay. Partitions are checked at send and delivery, so partitioning also drops in-flight packets.
/// Single-driver only; this is an in-process reference transport, not a socket implementation.</remarks>
public sealed class SimulatedNetwork
{
    private readonly SimulatedNodeRuntime _runtime;
    private readonly PropertyTestGenerators _random;
    private readonly HashSet<(string Source, string Target)> _partitions = [];
    private FaultInjectionPolicy _policy;

    internal SimulatedNetwork(SimulatedNodeRuntime runtime, uint seed, FaultInjectionPolicy policy)
    {
        _runtime = runtime;
        _random = new PropertyTestGenerators(seed);
        _policy = policy;
    }

    /// <summary>Distribution applied to future sends; already queued delivery times stay fixed.</summary>
    /// <exception cref="ArgumentNullException">The assigned policy is null.</exception>
    public FaultInjectionPolicy Policy
    {
        get => _policy;
        set => _policy = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Changes bidirectional connectivity between two registered endpoints.</summary>
    /// <param name="first">First endpoint.</param>
    /// <param name="second">Second endpoint.</param>
    /// <param name="partitioned">True to drop traffic, false to heal the link.</param>
    /// <exception cref="KeyNotFoundException">An endpoint is unknown.</exception>
    public void SetPartition(string first, string second, bool partitioned)
    {
        _runtime.RequireNode(first);
        _runtime.RequireNode(second);
        if (partitioned)
        {
            _partitions.Add((first, second));
            _partitions.Add((second, first));
        }
        else
        {
            _partitions.Remove((first, second));
            _partitions.Remove((second, first));
        }
    }

    /// <summary>Queues zero, one, or two deliveries without calling the receiver inline.</summary>
    /// <param name="source">Registered sender.</param>
    /// <param name="target">Registered receiver.</param>
    /// <param name="message">Non-null immutable payload, borrowed until delivery.</param>
    /// <exception cref="KeyNotFoundException">An endpoint is unknown.</exception>
    /// <exception cref="ArgumentNullException">The payload is null.</exception>
    public void Send(string source, string target, object message)
    {
        _runtime.RequireNode(source);
        _runtime.RequireNode(target);
        ArgumentNullException.ThrowIfNull(message);
        if (_partitions.Contains((source, target)) || _random.Chance(Policy.DropProbability))
        {
            return;
        }
        var copies = _random.Chance(Policy.DuplicateProbability) ? 2 : 1;
        for (var i = 0; i < copies; i++)
        {
            var delay = _random.Next(Policy.MaximumDelay + 1);
            _runtime.Clock.Schedule(delay, () =>
            {
                if (!_partitions.Contains((source, target)))
                {
                    _runtime.Deliver(target, message);
                }
            });
        }
    }
}

