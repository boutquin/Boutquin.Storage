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

/// <summary>Immutable send-time loss, duplication, and bounded-delay distribution.</summary>
/// <remarks>Independent delays on every copy permit reordering. Partitions and node pauses are explicit
/// runtime actions. This models message faults, not Byzantine payload corruption or OS failures.</remarks>
public sealed record FaultInjectionPolicy
{
    /// <summary>Validates a bounded network-fault distribution.</summary>
    /// <param name="dropProbability">Probability in [0,1] that a send is lost.</param>
    /// <param name="duplicateProbability">Probability in [0,1] that a surviving send creates two copies.</param>
    /// <param name="maximumDelay">Largest delay in ticks, from zero through int.MaxValue minus one.</param>
    /// <exception cref="ArgumentOutOfRangeException">A probability is nonfinite/outside [0,1], or delay is invalid.</exception>
    public FaultInjectionPolicy(double dropProbability = 0, double duplicateProbability = 0, int maximumDelay = 0)
    {
        ValidateProbability(dropProbability, nameof(dropProbability));
        ValidateProbability(duplicateProbability, nameof(duplicateProbability));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDelay);
        ArgumentOutOfRangeException.ThrowIfEqual(maximumDelay, int.MaxValue);
        DropProbability = dropProbability;
        DuplicateProbability = duplicateProbability;
        MaximumDelay = maximumDelay;
    }

    /// <summary>Send-loss probability.</summary>
    public double DropProbability { get; }
    /// <summary>Duplicate-copy probability for surviving sends.</summary>
    public double DuplicateProbability { get; }
    /// <summary>Inclusive maximum delay in virtual ticks.</summary>
    public int MaximumDelay { get; }

    private static void ValidateProbability(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

