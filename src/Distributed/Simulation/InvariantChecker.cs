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

/// <summary>Named safety predicates checked after every simulated node action.</summary>
/// <remarks>Predicates must be pure, deterministic and nonmutating. A failure identifies the property;
/// the runtime clock and reproduction seed identify the schedule. Checks cost the sum of predicate costs.</remarks>
public sealed class InvariantChecker
{
    private readonly Dictionary<string, Func<bool>> _properties = new(StringComparer.Ordinal);

    /// <summary>Adds a unique, nonblank property name and its predicate.</summary>
    /// <param name="name">Diagnostic identity.</param>
    /// <param name="predicate">Non-null pure predicate; true means the invariant holds.</param>
    /// <exception cref="ArgumentException">Name is blank or duplicated.</exception>
    /// <exception cref="ArgumentNullException">Name or predicate is null.</exception>
    public void Add(string name, Func<bool> predicate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(predicate);
        _properties.Add(name, predicate);
    }

    /// <summary>Evaluates every registered property and throws on the first failure.</summary>
    /// <exception cref="InvalidOperationException">A named invariant returned false.</exception>
    public void Check()
    {
        foreach (var (name, predicate) in _properties)
        {
            if (!predicate())
            {
                throw new InvalidOperationException($"Simulation invariant failed: {name}");
            }
        }
    }
}

