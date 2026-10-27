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

/// <summary>Reusable single-driver node registry with virtual timers, paused mailboxes, and an injected network.</summary>
/// <remarks>Handlers and timers run synchronously; do not use async-void handlers, wall-clock waits, or concurrent
/// callers. A pause retains already-delivered work until resume, modeling a process stall rather than a crash.
/// The runtime owns its clock/network; registered component lifetimes remain the caller's responsibility.</remarks>
public sealed class SimulatedNodeRuntime
{
    private sealed class Node(Action<object> receive)
    {
        internal Action<object> Receive { get; } = receive;
        internal Queue<Action> Pending { get; } = new();
        internal bool Paused { get; set; }
    }
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);

    /// <summary>Constructs an inert runtime with a private virtual clock and seeded network.</summary>
    /// <param name="seed">Reproduction seed for network choices.</param>
    /// <param name="policy">Optional initial fault policy; null means reliable immediate delivery.</param>
    public SimulatedNodeRuntime(uint seed, FaultInjectionPolicy? policy = null)
    {
        Clock = new DeterministicSimulationClock();
        Network = new SimulatedNetwork(this, seed, policy ?? new FaultInjectionPolicy());
    }

    /// <summary>Owned virtual clock; advance through RunUntil to check invariants after every event.</summary>
    public DeterministicSimulationClock Clock { get; }
    /// <summary>Owned transport; endpoints are registered node IDs.</summary>
    public SimulatedNetwork Network { get; }
    /// <summary>Properties checked after each node action and at RunUntil boundaries.</summary>
    public InvariantChecker Invariants { get; } = new();

    /// <summary>Registers a unique endpoint with an inline message handler.</summary>
    /// <param name="nodeId">Nonblank unique identity.</param>
    /// <param name="receive">Non-null synchronous handler for caller-owned immutable payloads.</param>
    /// <exception cref="ArgumentException">Identity is blank or already registered.</exception>
    /// <exception cref="ArgumentNullException">Identity or handler is null.</exception>
    public void AddNode(string nodeId, Action<object> receive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(receive);
        _nodes.Add(nodeId, new Node(receive));
    }

    /// <summary>Retains future node work in a FIFO mailbox until resumed.</summary>
    /// <param name="nodeId">Registered endpoint.</param>
    /// <exception cref="KeyNotFoundException">The endpoint is unknown.</exception>
    public void Pause(string nodeId) => _nodes[nodeId].Paused = true;

    /// <summary>Requeues retained work at the current tick without executing it inline.</summary>
    /// <param name="nodeId">Registered endpoint.</param>
    /// <exception cref="KeyNotFoundException">The endpoint is unknown.</exception>
    public void Resume(string nodeId)
    {
        var node = _nodes[nodeId];
        node.Paused = false;
        while (node.Pending.TryDequeue(out var action))
        {
            Schedule(nodeId, 0, action);
        }
    }

    /// <summary>Schedules a node-local timer; paused nodes execute it only after resume.</summary>
    /// <param name="nodeId">Registered endpoint.</param>
    /// <param name="delay">Nonnegative tick offset.</param>
    /// <param name="action">Synchronous deterministic callback.</param>
    /// <exception cref="KeyNotFoundException">The endpoint is unknown.</exception>
    /// <exception cref="ArgumentNullException">Action is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Delay is negative.</exception>
    public void Schedule(string nodeId, long delay, Action action)
    {
        RequireNode(nodeId);
        ArgumentNullException.ThrowIfNull(action);
        Clock.Schedule(delay, () => Execute(nodeId, action));
    }

    /// <summary>Runs through a target tick with invariant checks before and after the event loop.</summary>
    /// <param name="time">Target at least the current virtual tick.</param>
    /// <param name="maxEvents">Positive event budget to detect livelock.</param>
    /// <exception cref="InvalidOperationException">An invariant fails or the event budget is exhausted.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Time moves backward or budget is invalid.</exception>
    public void RunUntil(long time, int maxEvents = 100000)
    {
        Invariants.Check();
        Clock.AdvanceTo(time, maxEvents);
        Invariants.Check();
    }

    internal void RequireNode(string id) => _ = _nodes[id];
    internal void Deliver(string id, object message) => Execute(id, () => _nodes[id].Receive(message));

    private void Execute(string id, Action action)
    {
        var node = _nodes[id];
        if (node.Paused)
        {
            node.Pending.Enqueue(action);
            return;
        }
        action();
        Invariants.Check();
    }
}

