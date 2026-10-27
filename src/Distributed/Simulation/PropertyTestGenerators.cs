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

/// <summary>Version-stable seeded xorshift32 generator for workloads and fault choices.</summary>
/// <remarks>Single-driver, noncryptographic. Seed zero maps to 0x9e3779b9.
/// Explicit integer arithmetic avoids dependence on System.Random's runtime implementation.
/// Reuse the seed and the same calls to reproduce a schedule; shrinking is caller-controlled.</remarks>
public sealed class PropertyTestGenerators
{
    private uint _state;

    /// <summary>Creates an independent deterministic stream without touching global randomness.</summary>
    /// <param name="seed">Reproduction seed; every uint is accepted.</param>
    public PropertyTestGenerators(uint seed) => _state = seed == 0 ? 0x9e3779b9 : seed;

    /// <summary>Draws a uniform 32-bit word from the xorshift32 recurrence.</summary>
    /// <returns>The next nonzero generator state.</returns>
    public uint NextUInt32()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return _state = x;
    }

    /// <summary>Draws an integer in [0, exclusiveMaximum) with rejection to avoid modulo bias.</summary>
    /// <param name="exclusiveMaximum">Positive upper bound.</param>
    /// <returns>A reproducible bounded value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The bound is nonpositive.</exception>
    public int Next(int exclusiveMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMaximum);
        var bound = (uint)exclusiveMaximum;
        // xorshift excludes zero: normalize its 1..uint.MaxValue range before rejection.
        var limit = uint.MaxValue - uint.MaxValue % bound;
        uint value;
        do
        {
            value = NextUInt32() - 1;
        } while (value >= limit);
        return (int)(value % bound);
    }

    internal bool Chance(double probability) => NextUInt32() / 4294967296.0 < probability;
}

