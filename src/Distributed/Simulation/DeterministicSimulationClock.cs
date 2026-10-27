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
using Boutquin.Storage.Distributed.Interfaces.Simulation;

namespace Boutquin.Storage.Distributed.Simulation;

/// <summary>Stable virtual-time scheduler for reproducible fault schedules.</summary>
/// <remarks>Single-driver only. Scheduling costs O(log n); advancing costs O(k log n) for k due events.
/// Callbacks execute inline and exceptions propagate with Now left at the failing event.</remarks>
public sealed class DeterministicSimulationClock : ISimulationClock
{
    private readonly PriorityQueue<Action, (long Time, long Sequence)> _events = new();
    private long _sequence;
    private bool _advancing;

    /// <inheritdoc />
    public long Now { get; private set; }

    /// <inheritdoc />
    public void Schedule(long delay, Action callback)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delay);
        ArgumentNullException.ThrowIfNull(callback);
        _events.Enqueue(callback, (checked(Now + delay), checked(_sequence++)));
    }

    /// <inheritdoc />
    public void AdvanceTo(long time, int maxEvents = 100000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(time, Now);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEvents);
        if (_advancing)
        {
            throw new InvalidOperationException("Virtual time advancement cannot be reentrant.");
        }
        _advancing = true;
        try
        {
            var executed = 0;
            while (_events.TryPeek(out _, out var priority) && priority.Time <= time)
            {
                if (executed++ == maxEvents)
                {
                    throw new InvalidOperationException("Simulation event budget exhausted.");
                }
                var callback = _events.Dequeue();
                Now = priority.Time;
                callback();
            }
            Now = time;
        }
        finally
        {
            _advancing = false;
        }
    }
}

