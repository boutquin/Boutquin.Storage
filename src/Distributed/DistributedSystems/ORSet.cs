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
/// An observed-remove set CRDT that supports both add and remove operations.
/// Each add is tagged with a unique identifier; remove only removes observed tags.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. The observed-remove set CRDT
/// is implemented for educational fidelity to DDIA 2e Ch. 6, but production-grade extensions
/// — delta-state propagation and dotted-version-vector tombstone pruning — are not included. For production use, prefer <a href="https://github.com/CRDT-NET">automerge-net</a> or AntidoteDB.
/// </para>
///
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
    // Maps each element to its set of observed add-tags (current and historical). A tag stays
    // in this set after a Remove; filtering against _removedTags tells us whether the element
    // is currently present. Keeping tags here lets Merge carry tombstones across replicas.
    private readonly Dictionary<T, HashSet<string>> _elements = [];

    // Tombstones — tags this replica has observed AND removed. Contains(e) returns true only
    // when _elements[e] has at least one tag not in _removedTags[e] (Shapiro et al. 2011 §3.3,
    // DDIA 2e Ch. 6). Without this, a Remove on one replica is silently undone by
    // a Merge with a replica that still carries the removed tag.
    private readonly Dictionary<T, HashSet<string>> _removedTags = [];

    private readonly Dictionary<string, long> _counters = [];

    // All state mutations occur under _gate — counter increments, _elements updates,
    // and _removedTags updates must be consistent with each other. An unsynchronised Remove
    // concurrent with a Merge could observe a _elements snapshot without its matching
    // _removedTags snapshot.
    private readonly Lock _gate = new();

    /// <summary>
    /// Initializes a new empty ORSet.
    /// </summary>
    public ORSet()
    {
    }

    internal ORSet(
        Dictionary<T, HashSet<string>> elements,
        Dictionary<T, HashSet<string>> removedTags,
        Dictionary<string, long> counters)
    {
        foreach (var kvp in elements)
        {
            _elements[kvp.Key] = [.. kvp.Value];
        }
        foreach (var kvp in removedTags)
        {
            _removedTags[kvp.Key] = [.. kvp.Value];
        }
        foreach (var kvp in counters)
        {
            _counters[kvp.Key] = kvp.Value;
        }
    }

    // Durable sync needs observed tags, tombstones, and counters from the same transition.
    internal (Dictionary<T, HashSet<string>> Elements, Dictionary<T, HashSet<string>> RemovedTags,
        Dictionary<string, long> Counters) CaptureSyncState()
    {
        lock (_gate)
        {
            return (_elements.ToDictionary(entry => entry.Key, entry => new HashSet<string>(entry.Value)),
                _removedTags.ToDictionary(entry => entry.Key, entry => new HashSet<string>(entry.Value)),
                new Dictionary<string, long>(_counters, StringComparer.Ordinal));
        }
    }

    /// <inheritdoc/>
    public IReadOnlySet<T> Value
    {
        get
        {
            lock (_gate)
            {
                var result = new HashSet<T>();
                foreach (var (key, tags) in _elements)
                {
                    if (HasLiveTagLocked(key, tags))
                    {
                        result.Add(key);
                    }
                }
                return result;
            }
        }
    }

    /// <inheritdoc/>
    public void Add(string nodeId, T item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(item);

        lock (_gate)
        {
            // Atomic counter increment plus _elements update, all under one gate so a concurrent
            // Remove can never observe a tag before the counter update that produced it.
            if (!_counters.TryGetValue(nodeId, out var counter))
            {
                counter = 0;
            }
            counter++;
            _counters[nodeId] = counter;
            var tag = $"{nodeId}:{counter}";

            if (!_elements.TryGetValue(item, out var tags))
            {
                tags = [];
                _elements[item] = tags;
            }
            tags.Add(tag);
        }
    }

    /// <inheritdoc/>
    public void Remove(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Move every currently-observed tag for item into the tombstone set. A concurrent add
        // on another replica produces a tag not present in this tombstone set, so after merge
        // the element remains visible — this is the "observed-remove" semantic.
        lock (_gate)
        {
            if (!_elements.TryGetValue(item, out var tags) || tags.Count == 0)
            {
                return;
            }

            if (!_removedTags.TryGetValue(item, out var tombstones))
            {
                tombstones = [];
                _removedTags[item] = tombstones;
            }
            tombstones.UnionWith(tags);
        }
    }

    /// <inheritdoc/>
    public bool Contains(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (_gate)
        {
            return _elements.TryGetValue(item, out var tags) && HasLiveTagLocked(item, tags);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="tags"/> contains at least one tag
    /// that is NOT present in <c>_removedTags[item]</c>. Caller must hold <c>_gate</c>.
    /// </summary>
    private bool HasLiveTagLocked(T item, HashSet<string> tags)
    {
        if (tags.Count == 0)
        {
            return false;
        }
        if (!_removedTags.TryGetValue(item, out var tombstones))
        {
            return true;
        }
        foreach (var tag in tags)
        {
            if (!tombstones.Contains(tag))
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc/>
    public ICrdt<IReadOnlySet<T>> Merge(ICrdt<IReadOnlySet<T>> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other is not ORSet<T> otherORSet)
        {
            throw new ArgumentException("Can only merge with another ORSet of the same type.", nameof(other));
        }

        // Snapshot both sides under their gates to avoid tearing a concurrent Add/Remove.
        Dictionary<T, HashSet<string>> thisElements;
        Dictionary<T, HashSet<string>> thisRemoved;
        Dictionary<string, long> thisCounters;
        lock (_gate)
        {
            thisElements = _elements.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
            thisRemoved = _removedTags.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
            thisCounters = new Dictionary<string, long>(_counters);
        }

        Dictionary<T, HashSet<string>> otherElements;
        Dictionary<T, HashSet<string>> otherRemoved;
        Dictionary<string, long> otherCounters;
        lock (otherORSet._gate)
        {
            otherElements = otherORSet._elements.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
            otherRemoved = otherORSet._removedTags.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
            otherCounters = new Dictionary<string, long>(otherORSet._counters);
        }

        var mergedElements = new Dictionary<T, HashSet<string>>();
        var mergedRemoved = new Dictionary<T, HashSet<string>>();

        var allKeys = new HashSet<T>(thisElements.Keys);
        foreach (var key in otherElements.Keys) { allKeys.Add(key); }
        foreach (var key in thisRemoved.Keys) { allKeys.Add(key); }
        foreach (var key in otherRemoved.Keys) { allKeys.Add(key); }

        foreach (var key in allKeys)
        {
            var unionTags = new HashSet<string>();
            if (thisElements.TryGetValue(key, out var a)) { unionTags.UnionWith(a); }
            if (otherElements.TryGetValue(key, out var b)) { unionTags.UnionWith(b); }

            var unionRemoved = new HashSet<string>();
            if (thisRemoved.TryGetValue(key, out var ar)) { unionRemoved.UnionWith(ar); }
            if (otherRemoved.TryGetValue(key, out var br)) { unionRemoved.UnionWith(br); }

            if (unionTags.Count > 0)
            {
                mergedElements[key] = unionTags;
            }
            if (unionRemoved.Count > 0)
            {
                mergedRemoved[key] = unionRemoved;
            }
        }

        // Merge counters by taking the max per node — every merged replica must continue
        // producing fresh tags that collide with neither side's history.
        var mergedCounters = new Dictionary<string, long>(thisCounters);
        foreach (var (node, count) in otherCounters)
        {
            if (!mergedCounters.TryGetValue(node, out var cur) || count > cur)
            {
                mergedCounters[node] = count;
            }
        }

        return new ORSet<T>(mergedElements, mergedRemoved, mergedCounters);
    }
}
