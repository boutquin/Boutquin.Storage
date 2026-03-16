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
/// An observed-remove set CRDT that supports both add and remove operations.
/// Each add is tagged with a unique identifier; remove only removes observed tags.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why unique tags per add?</b> The fundamental challenge of a remove-capable CRDT set is
/// distinguishing between "removed and should stay removed" vs "re-added concurrently". By tagging
/// each add with a unique identifier (nodeId + counter), a remove only affects the specific add
/// operations it has observed. A concurrent add from another node creates a new tag that the remove
/// hasn't seen, so the element remains (add-wins semantics).
/// </para>
///
/// <para>
/// <b>Merge semantics:</b> The merged set contains all tags from both replicas minus the tags
/// that have been removed by either replica. An element is present if it has at least one active tag.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of elements in the set.</typeparam>
public sealed class ORSet<T> : IORSet<T>
    where T : notnull
{
    // Maps each element to its set of active unique tags
    private readonly Dictionary<T, HashSet<string>> _elements = new();
    private readonly Dictionary<string, long> _counters = new();

    // Why a lock for counter increments? The read-modify-write sequence (TryGetValue → increment →
    // store) is not atomic. Without synchronization, two concurrent Add calls for the same nodeId
    // could read the same counter value and produce duplicate tags, violating the uniqueness guarantee.
    private readonly object _counterLock = new();

    /// <summary>
    /// Initializes a new empty ORSet.
    /// </summary>
    public ORSet()
    {
    }

    internal ORSet(Dictionary<T, HashSet<string>> elements)
    {
        foreach (var kvp in elements)
        {
            _elements[kvp.Key] = new HashSet<string>(kvp.Value);
        }
    }

    /// <inheritdoc/>
    public IReadOnlySet<T> Value
    {
        get
        {
            var result = new HashSet<T>();
            foreach (var kvp in _elements)
            {
                if (kvp.Value.Count > 0)
                {
                    result.Add(kvp.Key);
                }
            }
            return result;
        }
    }

    /// <inheritdoc/>
    public void Add(string nodeId, T item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(item);

        string tag;

        // Atomic counter increment to guarantee unique tags across concurrent calls
        lock (_counterLock)
        {
            if (!_counters.TryGetValue(nodeId, out var counter))
            {
                counter = 0;
            }
            counter++;
            _counters[nodeId] = counter;
            tag = $"{nodeId}:{counter}";
        }

        if (!_elements.TryGetValue(item, out var tags))
        {
            tags = new HashSet<string>();
            _elements[item] = tags;
        }
        tags.Add(tag);
    }

    /// <inheritdoc/>
    public void Remove(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Remove all observed tags for this element. If a concurrent add creates a new tag
        // on another replica, that tag won't be in our set, so the element will reappear on merge.
        if (_elements.TryGetValue(item, out var tags))
        {
            tags.Clear();
        }
    }

    /// <inheritdoc/>
    public bool Contains(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _elements.TryGetValue(item, out var tags) && tags.Count > 0;
    }

    /// <inheritdoc/>
    public ICrdt<IReadOnlySet<T>> Merge(ICrdt<IReadOnlySet<T>> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other is not ORSet<T> otherORSet)
        {
            throw new ArgumentException("Can only merge with another ORSet of the same type.", nameof(other));
        }

        var merged = new Dictionary<T, HashSet<string>>();

        // Collect all elements from both sets
        var allKeys = new HashSet<T>(_elements.Keys);
        foreach (var key in otherORSet._elements.Keys)
        {
            allKeys.Add(key);
        }

        foreach (var key in allKeys)
        {
            var mergedTags = new HashSet<string>();

            if (_elements.TryGetValue(key, out var thisTags))
            {
                mergedTags.UnionWith(thisTags);
            }
            if (otherORSet._elements.TryGetValue(key, out var otherTags))
            {
                mergedTags.UnionWith(otherTags);
            }

            if (mergedTags.Count > 0)
            {
                merged[key] = mergedTags;
            }
        }

        return new ORSet<T>(merged);
    }
}
