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
namespace Boutquin.Storage.Distributed.DistributedSystems;

/// <summary>
/// A grow-only set CRDT. Elements can be added but never removed. Merge is set union.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. The grow-only set CRDT
/// is implemented for educational fidelity to DDIA 2e Ch. 6, but production-grade extensions
/// — delta-state propagation and tombstone-free remove variants — are not included. For production use, prefer <a href="https://github.com/CRDT-NET">automerge-net</a> or AntidoteDB.
/// </para>
///
/// <para>
/// <b>Why grow-only?</b> Set union is commutative, associative, and idempotent — the three
/// requirements for a CRDT merge operation. If removal were allowed, the merge would need to
/// decide whether a removed element should reappear when merging with a replica that still has it.
/// This ambiguity is why removal requires the more complex OR-Set.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is not thread-safe. External synchronization is required
/// for concurrent use.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of elements in the set.</typeparam>
public sealed class GSet<T> : IGSet<T>
    where T : notnull
{
    private readonly HashSet<T> _items;

    /// <summary>
    /// Initializes a new empty GSet.
    /// </summary>
    public GSet()
    {
        _items = [];
    }

    internal GSet(HashSet<T> initialState)
    {
        _items = [.. initialState];
    }

    /// <inheritdoc/>
    /// <remarks>Returns a defensive copy. Returning the internal <see cref="HashSet{T}"/>
    /// directly would allow callers to cast back to a mutable type and corrupt the CRDT's
    /// monotonic-growth invariant.</remarks>
    public IReadOnlySet<T> Value => new HashSet<T>(_items);

    /// <inheritdoc/>
    public void Add(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
    }

    /// <inheritdoc/>
    public bool Contains(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _items.Contains(item);
    }

    /// <inheritdoc/>
    public ICrdt<IReadOnlySet<T>> Merge(ICrdt<IReadOnlySet<T>> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other is not GSet<T> otherGSet)
        {
            throw new ArgumentException("Can only merge with another GSet of the same type.", nameof(other));
        }

        var merged = new HashSet<T>(_items);
        merged.UnionWith(otherGSet._items);
        return new GSet<T>(merged);
    }
}
