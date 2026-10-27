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
namespace Boutquin.Storage.Distributed.Interfaces.Simulation;

/// <summary>Monotonic virtual time and a stable, single-driver event queue.</summary>
/// <remarks>Ticks are dimensionless simulation units. No wall clock, threads, or background timers are used.</remarks>
public interface ISimulationClock
{
    /// <summary>Current virtual tick, initially zero.</summary>
    long Now { get; }

    /// <summary>Queues a callback after a nonnegative delay; equal deadlines execute in insertion order.</summary>
    /// <param name="delay">Nonnegative tick offset from Now.</param>
    /// <param name="callback">Synchronous deterministic action; must not be null.</param>
    /// <exception cref="ArgumentOutOfRangeException">The delay is negative.</exception>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    void Schedule(long delay, Action callback);

    /// <summary>Executes due callbacks, stopping with an exception if the event budget is exhausted.</summary>
    /// <param name="time">Target tick, at least Now.</param>
    /// <param name="maxEvents">Positive bound protecting against zero-delay livelock.</param>
    /// <exception cref="ArgumentOutOfRangeException">Time moves backward or the budget is nonpositive.</exception>
    /// <exception cref="InvalidOperationException">The event budget is exhausted or advancement is reentrant.</exception>
    void AdvanceTo(long time, int maxEvents = 100000);
}

