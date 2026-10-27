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
namespace Boutquin.Storage.SourceGenerator;

// Borrowed from StronglyTypedId.
// A readonly struct wrapping T[] that implements IEquatable via SequenceEqual.
// Pipeline intermediate types must never hold Roslyn types — this ensures caching works
// correctly: two EquatableArray<T> with the same elements compare equal across compilations.
internal readonly struct EquatableArray<T>(T[] array) : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _array = array;

    public static readonly EquatableArray<T> Empty = new([]);

    public int Length => _array?.Length ?? 0;

    // Null-forgiving `!` on a null _array throws NullReferenceException from a default-constructed
    // instance. Explicit null-check converts it to IndexOutOfRangeException — the same exception
    // type a real array access throws, so callers get a recognizable, debuggable exception.
    public T this[int index] => _array is null
        ? throw new IndexOutOfRangeException(nameof(index))
        : _array[index];

    public bool Equals(EquatableArray<T> other)
    {
        if (_array is null && other._array is null)
        {
            return true;
        }

        if (_array is null || other._array is null)
        {
            return false;
        }

        if (_array.Length != other._array.Length)
        {
            return false;
        }

        for (var i = 0; i < _array.Length; i++)
        {
            if (!_array[i].Equals(other._array[i]))
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) =>
        obj is EquatableArray<T> other && Equals(other);

    // Uses XOR + prime multiplication instead of HashCode.Combine — HashCode is unavailable on netstandard2.0.
    public override int GetHashCode()
    {
        if (_array is null || _array.Length == 0)
        {
            return 0;
        }

        unchecked
        {
            var hash = 17;
            foreach (T item in _array)
            {
                hash = hash * 31 ^ item.GetHashCode();
            }
            return hash;
        }
    }

    public IEnumerator<T> GetEnumerator() =>
        ((IEnumerable<T>)(_array ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);
    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);
}
