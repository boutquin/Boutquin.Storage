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
namespace Boutquin.Storage.Infrastructure.Tests.TestHelpers;

/// <summary>
/// A test-only <see cref="IHashAlgorithm"/> that always returns the same constant hash value.
/// Used in AC-7.7 to prove that custom hash algorithm constructors wire the supplied
/// algorithms into the Add/Contains flow — not the default Murmur3+XxHash32 algorithms.
/// </summary>
internal sealed class ConstantHashAlgorithm(uint constant) : IHashAlgorithm
{
    public uint ComputeHash(ReadOnlySpan<byte> data) => constant;
}

/// <summary>
/// A test-only value-type key implementing both <see cref="IComparable{T}"/> and
/// <see cref="IEquatable{T}"/>. Used by AC-7.2 tests to verify that index types correctly
/// enforce the <c>IEquatable&lt;TKey&gt;</c> constraint and use typed equality.
/// </summary>
internal readonly struct EquatableIntKey(int value)
    : IComparable<EquatableIntKey>, IEquatable<EquatableIntKey>
{
    public int Value { get; } = value;

    public int CompareTo(EquatableIntKey other) => Value.CompareTo(other.Value);
    public bool Equals(EquatableIntKey other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is EquatableIntKey other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
}
