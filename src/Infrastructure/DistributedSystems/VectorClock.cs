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
namespace Boutquin.Storage.Infrastructure.DistributedSystems;

/// <summary>
/// A vector clock implementation for tracking causal ordering of events in a distributed system.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why Dictionary instead of array?</b> In dynamic distributed systems, nodes join and leave
/// over time. A dictionary keyed by node ID handles this naturally, while a fixed-size array
/// would require a mapping layer and resizing logic.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is not thread-safe. External synchronization is required
/// for concurrent use. In practice, vector clocks are typically updated during message
/// send/receive which is already serialized per node.
/// </para>
/// </remarks>
public sealed class VectorClock : IVectorClock
{
    private readonly Dictionary<string, long> _clock = new();

    /// <summary>
    /// Initializes a new empty vector clock.
    /// </summary>
    public VectorClock()
    {
    }

    /// <summary>
    /// Initializes a vector clock with the specified initial state.
    /// </summary>
    internal VectorClock(Dictionary<string, long> initialState)
    {
        foreach (var kvp in initialState)
        {
            _clock[kvp.Key] = kvp.Value;
        }
    }

    /// <inheritdoc/>
    public void Increment(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        if (_clock.TryGetValue(nodeId, out var current))
        {
            _clock[nodeId] = current + 1;
        }
        else
        {
            _clock[nodeId] = 1;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, long> GetClock()
    {
        // Why new Dictionary? Returning the internal dictionary directly would allow callers to
        // cast back to Dictionary<string, long> and mutate the clock's internal state. Creating
        // a copy ensures true immutability of the returned snapshot.
        return new Dictionary<string, long>(_clock);
    }

    /// <inheritdoc/>
    public VectorClockComparison CompareTo(IVectorClock other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var otherClock = other.GetClock();
        var allKeys = new HashSet<string>(_clock.Keys);
        foreach (var key in otherClock.Keys)
        {
            allKeys.Add(key);
        }

        var hasLess = false;
        var hasGreater = false;

        foreach (var key in allKeys)
        {
            _clock.TryGetValue(key, out var thisValue);
            otherClock.TryGetValue(key, out var otherValue);

            if (thisValue < otherValue)
            {
                hasLess = true;
            }
            else if (thisValue > otherValue)
            {
                hasGreater = true;
            }

            if (hasLess && hasGreater)
            {
                return VectorClockComparison.Concurrent;
            }
        }

        if (hasLess && !hasGreater)
        {
            return VectorClockComparison.Before;
        }

        if (hasGreater && !hasLess)
        {
            return VectorClockComparison.After;
        }

        return VectorClockComparison.Equal;
    }

    /// <inheritdoc/>
    public IVectorClock Merge(IVectorClock other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var otherClock = other.GetClock();
        var merged = new Dictionary<string, long>(_clock);

        foreach (var kvp in otherClock)
        {
            if (merged.TryGetValue(kvp.Key, out var existingValue))
            {
                merged[kvp.Key] = Math.Max(existingValue, kvp.Value);
            }
            else
            {
                merged[kvp.Key] = kvp.Value;
            }
        }

        return new VectorClock(merged);
    }
}
