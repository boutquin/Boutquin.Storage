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
/// A positive-negative counter CRDT that supports both increment and decrement.
/// Internally composed of two GCounters: one for positive increments (P) and one for negative decrements (N).
/// The value is P.Value - N.Value.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two GCounters?</b> A single grow-only counter cannot support decrements. By tracking
/// increments and decrements separately, each as a grow-only counter, both operations become
/// monotonic (only grow) and merge correctly. The actual value is computed as the difference.
/// </para>
/// </remarks>
public sealed class PNCounter : IPNCounter
{
    private readonly GCounter _positive;
    private readonly GCounter _negative;

    /// <summary>
    /// Initializes a new empty PNCounter.
    /// </summary>
    public PNCounter()
    {
        _positive = new GCounter();
        _negative = new GCounter();
    }

    internal PNCounter(GCounter positive, GCounter negative)
    {
        _positive = positive;
        _negative = negative;
    }

    /// <inheritdoc/>
    public long Value => _positive.Value - _negative.Value;

    /// <inheritdoc/>
    public void Increment(string nodeId)
    {
        _positive.Increment(nodeId);
    }

    /// <inheritdoc/>
    public void Decrement(string nodeId)
    {
        _negative.Increment(nodeId);
    }

    /// <inheritdoc/>
    public ICrdt<long> Merge(ICrdt<long> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other is not PNCounter otherPNCounter)
        {
            throw new ArgumentException("Can only merge with another PNCounter.", nameof(other));
        }

        // Why pattern matching instead of direct cast? GCounter.Merge returns ICrdt<long>,
        // not GCounter. A direct cast is unsafe if the return type ever changes. Pattern
        // matching provides a clear error message instead of an opaque InvalidCastException.
        if (_positive.Merge(otherPNCounter._positive) is not GCounter mergedPositive)
        {
            throw new InvalidOperationException("GCounter.Merge did not return a GCounter instance.");
        }
        if (_negative.Merge(otherPNCounter._negative) is not GCounter mergedNegative)
        {
            throw new InvalidOperationException("GCounter.Merge did not return a GCounter instance.");
        }

        return new PNCounter(mergedPositive, mergedNegative);
    }
}
