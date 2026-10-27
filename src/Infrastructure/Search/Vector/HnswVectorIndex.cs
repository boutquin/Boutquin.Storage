// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//
using Boutquin.Storage.Domain.Interfaces.Search;

namespace Boutquin.Storage.Infrastructure.Search.Vector;

/// <summary>Provides approximate nearest-neighbor search with a hierarchical navigable small-world graph.</summary>
public sealed class HnswVectorIndex<TId> : IVectorIndex<TId> where TId : notnull
{
    private readonly object _gate = new();
    private readonly IVectorDistanceMetric _metric;
    private readonly int _maxConnections;
    private readonly int _efConstruction;
    private readonly int _efSearch;
    private readonly Random _random;
    private readonly Dictionary<TId, VectorEntry<TId>> _entries = [];
    private readonly Dictionary<TId, Node> _nodeOf = [];

    // One graph node per distinct vector: exact duplicates (identical embeddings, zero vectors)
    // share a node instead of filling each other's neighbour lists at distance zero. Distinct
    // vectors at distance zero (positive multiples under cosine) still get a node each; the
    // layer-0 tree edges kept by Prune keep every one of them reachable.
    private readonly Dictionary<float[], Node> _nodesByVector = new(VectorValueComparer.Instance);
    private Node? _entryPoint;
    private int _maximumLevel = -1;
    private int _liveNodeCount;
    private int _removedCount;
    private long _nextSequence;
    private long _nextNodeSequence;

    /// <summary>Initializes an HNSW index.</summary>
    public HnswVectorIndex(
        int dimension,
        IVectorDistanceMetric metric,
        int maxConnections = 8,
        int efConstruction = 64,
        int efSearch = 32,
        int randomSeed = 42)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(efConstruction, maxConnections);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(efSearch);
        Dimension = dimension;
        _metric = metric;
        _maxConnections = maxConnections;
        _efConstruction = efConstruction;
        _efSearch = efSearch;
        _random = new Random(randomSeed);
    }

    /// <inheritdoc />
    public int Dimension { get; }

    /// <inheritdoc />
    public void Upsert(TId id, ReadOnlySpan<float> vector)
    {
        var copy = VectorValidation.Copy(vector, Dimension, nameof(vector));
        lock (_gate)
        {
            var sequence = _entries.TryGetValue(id, out var existing) ? existing.Sequence : _nextSequence++;
            var entry = new VectorEntry<TId>(id, copy, sequence);

            // Replacing an id is atomic against a throwing metric: the old registration is detached
            // first, but the new vector is linked (and any rebuild run) before the call returns, and a
            // failure at any point puts the old entry, its counters and the graph back as they were.
            Node? oldNode = null;
            var oldMemberIndex = -1;
            var oldBecameRemoved = false;
            if (existing is not null)
            {
                oldNode = _nodeOf[id];
                oldMemberIndex = oldNode.Members.FindIndex(member => EqualityComparer<TId>.Default.Equals(member.Id, id));
                _entries.Remove(id);
                _nodeOf.Remove(id);
                oldNode.Members.RemoveAt(oldMemberIndex);
                if (oldNode.Members.Count == 0)
                {
                    _liveNodeCount--;
                    _removedCount++;
                    oldBecameRemoved = true;
                }
            }

            var journal = new GraphJournal();
            var previousEntryPoint = _entryPoint;
            var previousMaximumLevel = _maximumLevel;
            Node? node = null;
            var created = false;
            var revived = false;
            var registered = false;
            try
            {
                if (!_nodesByVector.TryGetValue(copy, out node))
                {
                    // Link the node before registering it: a metric exception during Insert leaves no
                    // unlinked node behind for a retried upsert to mistake for a routing point.
                    node = new Node(copy, _nextNodeSequence++, NextLevel());
                    Insert(node, journal);
                    _nodesByVector[copy] = node;
                    created = true;
                }
                else if (node.Removed)
                {
                    // A routing point regains a member and is returned by searches again.
                    _removedCount--;
                    revived = true;
                }

                if (node.Members.Count == 0)
                {
                    _liveNodeCount++;
                }

                node.Members.Add(entry);
                _entries[id] = entry;
                _nodeOf[id] = node;
                registered = true;

                // The replaced vector's node may have turned into a routing point: apply the same
                // rebuild trigger a removal does, once the new vector is linked.
                if (oldBecameRemoved && _removedCount > _liveNodeCount)
                {
                    Rebuild();
                }
            }
            catch
            {
                if (registered)
                {
                    node!.Members.RemoveAt(node.Members.Count - 1);
                    _entries.Remove(id);
                    _nodeOf.Remove(id);
                    if (node.Members.Count == 0)
                    {
                        _liveNodeCount--;
                    }

                    if (revived)
                    {
                        _removedCount++;
                    }

                    if (created)
                    {
                        // Rebuild has restored the graph as it stood with the new node linked: unlink it.
                        _nodesByVector.Remove(copy);
                        journal.Restore();
                        _entryPoint = previousEntryPoint;
                        _maximumLevel = previousMaximumLevel;
                    }
                }

                if (oldNode is not null)
                {
                    oldNode.Members.Insert(oldMemberIndex, existing!);
                    _entries[id] = existing!;
                    _nodeOf[id] = oldNode;
                    if (oldBecameRemoved)
                    {
                        _liveNodeCount++;
                        _removedCount--;
                    }
                }

                throw;
            }
        }
    }

    /// <inheritdoc />
    public bool Remove(TId id)
    {
        lock (_gate)
        {
            if (!_entries.ContainsKey(id))
            {
                return false;
            }

            RemoveCore(id);
            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<(TId Id, float Distance)> Search(ReadOnlySpan<float> query, int count) =>
        Search(query, count, _efSearch);

    /// <summary>Searches with an explicit candidate-list width.</summary>
    public IReadOnlyList<(TId Id, float Distance)> Search(ReadOnlySpan<float> query, int count, int efSearch)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(efSearch);
        var copy = VectorValidation.Copy(query, Dimension, nameof(query));
        lock (_gate)
        {
            if (_entryPoint is null || _entries.Count == 0)
            {
                return [];
            }

            var current = _entryPoint;
            for (var layer = _maximumLevel; layer > 0; layer--)
            {
                current = Greedy(copy, current, layer);
            }

            var members = SearchLayer(copy, [current], Math.Max(count, efSearch), 0, includeRemoved: false)
                .SelectMany(static candidate => candidate.Node.Members)
                .ToArray();
            if (members.Length < Math.Min(count, _entries.Count))
            {
                // A beam that ends short of the requested count never reached its width, so it has
                // explored the whole component it started in: the remaining live vectors are cut off
                // from it by pruning or by routing points. Rank every live vector instead, so a
                // populated index never answers with fewer results than it can supply.
                return VectorSearch.Rank(_entries.Values, copy, count, _metric);
            }

            return VectorSearch.Rank(members, copy, count, _metric);
        }
    }

    private void Insert(Node node, GraphJournal journal)
    {
        try
        {
            InsertCore(node, journal);
        }
        catch
        {
            // The metric is caller code and may throw part-way through linking: put back every
            // adjacency list and tree link the insertion touched, so the graph is as it was.
            journal.Restore();
            throw;
        }
    }

    private void InsertCore(Node node, GraphJournal journal)
    {
        if (_entryPoint is null)
        {
            _entryPoint = node;
            _maximumLevel = node.Level;
            return;
        }

        var current = _entryPoint;
        for (var layer = _maximumLevel; layer > node.Level; layer--)
        {
            current = Greedy(node.Vector, current, layer);
        }

        IReadOnlyList<Node> entryPoints = [current];
        for (var layer = Math.Min(node.Level, _maximumLevel); layer >= 0; layer--)
        {
            // Routing points stay eligible as neighbours here: a new vector whose beam reaches only
            // removed nodes must still be linked into the component every search starts from.
            var found = SearchLayer(node.Vector, entryPoints, _efConstruction, layer, includeRemoved: true)
                .Where(candidate => !ReferenceEquals(candidate.Node, node))
                .ToArray();
            var neighbors = found
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Node.Sequence)
                .Take(_maxConnections)
                .Select(static candidate => candidate.Node)
                .ToArray();

            if (layer == 0 && found.Length > 0)
            {
                // This node hangs from a parent in a layer-0 spanning tree whose edges Prune never
                // drops, in either direction, so every vector stays reachable.
                var parent = ChooseParent(node, found);
                journal.Touch(node);
                journal.Touch(parent);
                node.Parent = parent;
                parent.ChildCount++;
                if (!neighbors.Contains(node.Parent))
                {
                    neighbors = [.. neighbors, node.Parent];
                }
            }

            foreach (var neighbor in neighbors)
            {
                journal.Touch(node.Neighbors[layer]);
                journal.Touch(neighbor.Neighbors[layer]);
                node.Neighbors[layer].Add(neighbor);
                neighbor.Neighbors[layer].Add(node);
                Prune(neighbor, layer);
            }

            // Unlike the descent phase, the whole result list W (not only its nearest element)
            // seeds the search on the next lower layer.
            if (found.Length > 0)
            {
                entryPoints = found.Select(static candidate => candidate.Node).ToArray();
            }
        }

        if (node.Level > _maximumLevel)
        {
            _entryPoint = node;
            _maximumLevel = node.Level;
        }
    }

    private Node ChooseParent(Node node, Candidate[] found)
    {
        // Each node takes at most M children, so its tree edges fit inside Mmax0 = 2·M and only the
        // edge to its own parent can exceed the cap. The nearest beam member with room is the
        // parent; among equal distances the latest node, so a run of ties forms a chain.
        Node? nearest = null;
        foreach (var candidate in found.OrderBy(static candidate => candidate.Distance)
            .ThenByDescending(static candidate => candidate.Node.Sequence))
        {
            nearest ??= candidate.Node;
            if (candidate.Node.ChildCount < _maxConnections)
            {
                return candidate.Node;
            }
        }

        // Every beam member is full, which happens only when many vectors tie: descend the tree from
        // the nearest member through its nearest child until a node has room. Ties between children
        // are broken by a hash of both sequences, so successive inserts spread over the subtrees and
        // the descent stays logarithmic instead of deepening one path.
        var current = nearest!;
        while (current.ChildCount >= _maxConnections)
        {
            var parent = current;
            current = parent.Neighbors[0]
                .Where(neighbor => ReferenceEquals(neighbor.Parent, parent))
                .MinBy(child => (VectorRanking.Distance(_metric, node.Vector, child.Vector), SpreadKey(child.Sequence, node.Sequence)))!;
        }

        return current;
    }

    private static ulong SpreadKey(long child, long inserted) =>
        unchecked((ulong)(child ^ (inserted * 31)) * 0x9E3779B97F4A7C15UL);

    private Node Greedy(float[] query, Node entry, int layer)
    {
        var current = entry;
        var currentDistance = VectorRanking.Distance(_metric, query, current.Vector);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var neighbor in current.Neighbors[layer])
            {
                var distance = VectorRanking.Distance(_metric, query, neighbor.Vector);
                if (distance < currentDistance || (distance == currentDistance && neighbor.Sequence < current.Sequence))
                {
                    current = neighbor;
                    currentDistance = distance;
                    changed = true;
                }
            }
        }

        return current;
    }

    private List<Candidate> SearchLayer(float[] query, IReadOnlyList<Node> entryPoints, int width, int layer, bool includeRemoved)
    {
        // Removed nodes stay in the graph as routing points: they are expanded like any other
        // candidate, and enter the result list only when the caller links to it (insertion), never
        // when the result is returned to a query.
        var visited = new HashSet<Node>(entryPoints);
        var pending = visited.Select(entry => new Candidate(entry, VectorRanking.Distance(_metric, query, entry.Vector))).ToList();
        var best = pending.Where(candidate => includeRemoved || !candidate.Node.Removed).ToList();
        best.Sort(CandidateComparer.Instance);
        if (best.Count > width)
        {
            best.RemoveRange(width, best.Count - width);
        }

        while (pending.Count > 0)
        {
            pending.Sort(CandidateComparer.Instance);
            var current = pending[0];
            pending.RemoveAt(0);
            var worst = best.Count < width ? null : best[^1];
            if (worst is not null && current.Distance > worst.Distance)
            {
                break;
            }

            foreach (var neighbor in current.Node.Neighbors[layer])
            {
                if (!visited.Add(neighbor))
                {
                    continue;
                }

                // A full result list admits only a candidate that ranks before its furthest element
                // (Malkov and Yashunin Alg. 2: strictly closer; among equal distances the repository's
                // lower-sequence-first order). Admitting every tie would walk every vector at that
                // distance, so an index of mutually equidistant vectors would cost linear work per query.
                var candidate = new Candidate(neighbor, VectorRanking.Distance(_metric, query, neighbor.Vector));
                if (worst is null || CandidateComparer.Instance.Compare(candidate, worst) < 0)
                {
                    pending.Add(candidate);
                    if (neighbor.Removed && !includeRemoved)
                    {
                        continue;
                    }

                    best.Add(candidate);
                    best.Sort(CandidateComparer.Instance);
                    if (best.Count > width)
                    {
                        best.RemoveAt(best.Count - 1);
                    }
                }
            }
        }

        return best;
    }

    private void Prune(Node node, int layer)
    {
        // Layer 0 holds every vector and carries Mmax0 = 2·M connections (Malkov and Yashunin
        // section 4.1). No cap alone keeps a vector reachable: once every node linking to it holds
        // that many nearer vectors, pruning drops its last in-edge. Tree edges between a node and
        // its layer-0 parent are therefore always kept, both ways, so layer 0 stays one connected
        // component and a wide enough beam from any start reaches every vector. A node's at most M
        // children count against its cap and the nearest other neighbours fill the rest, so a list
        // holds at most Mmax0 connections plus the edge to its own parent.
        if (layer == 0)
        {
            var room = (2 * _maxConnections) - node.ChildCount;
            var nearest = node.Neighbors[0]
                .Where(neighbor => !IsTreeEdge(node, neighbor))
                .OrderBy(neighbor => VectorRanking.Distance(_metric, node.Vector, neighbor.Vector))
                .ThenBy(static neighbor => neighbor.Sequence)
                .Take(room)
                .ToHashSet();
            node.Neighbors[0].RemoveAll(neighbor => !nearest.Contains(neighbor) && !IsTreeEdge(node, neighbor));
            return;
        }

        var keep = node.Neighbors[layer]
            .OrderBy(neighbor => VectorRanking.Distance(_metric, node.Vector, neighbor.Vector))
            .ThenBy(static neighbor => neighbor.Sequence)
            .Take(_maxConnections)
            .ToHashSet();
        node.Neighbors[layer].RemoveAll(neighbor => !keep.Contains(neighbor));
    }

    private static bool IsTreeEdge(Node node, Node neighbor) =>
        ReferenceEquals(node.Parent, neighbor) || ReferenceEquals(neighbor.Parent, node);

    private void RemoveCore(TId id)
    {
        var node = _nodeOf[id];
        var entry = _entries[id];
        var memberIndex = node.Members.FindIndex(member => EqualityComparer<TId>.Default.Equals(member.Id, id));
        _entries.Remove(id);
        _nodeOf.Remove(id);
        node.Members.RemoveAt(memberIndex);
        if (node.Members.Count > 0)
        {
            return;
        }

        // Unlinking the node would strand every vector that was reachable only through it, so it
        // stays in the graph as a routing point that searches traverse but never return.
        _liveNodeCount--;
        _removedCount++;

        // Once removed nodes outnumber live ones, rebuild from the live nodes: the graph's memory
        // and search work stay proportional to the live set, at an amortized one insertion per removal.
        if (_removedCount > _liveNodeCount)
        {
            try
            {
                Rebuild();
            }
            catch
            {
                // Rebuild has restored the pre-rebuild graph; undo the removal too, so a metric
                // failure surfaces from a Remove that changed nothing.
                node.Members.Insert(memberIndex, entry);
                _entries[id] = entry;
                _nodeOf[id] = node;
                _liveNodeCount++;
                _removedCount--;
                throw;
            }
        }
    }

    private void Rebuild()
    {
        var live = _nodesByVector.Values.Where(static node => !node.Removed).OrderBy(static node => node.Sequence).ToArray();
        var previousNodes = _nodesByVector.ToArray();
        var previousEntryPoint = _entryPoint;
        var previousMaximumLevel = _maximumLevel;
        var previousRemovedCount = _removedCount;

        // The graph is torn down before the caller's metric runs again, so snapshot every live node's
        // adjacency and tree link first; a metric exception then restores the pre-rebuild graph
        // instead of leaving the survivors registered but unlinked.
        var journal = new GraphJournal();
        foreach (var node in live)
        {
            journal.Touch(node);
            foreach (var layer in node.Neighbors)
            {
                journal.Touch(layer);
            }
        }

        try
        {
            RebuildCore(live, journal);
        }
        catch
        {
            journal.Restore();
            _nodesByVector.Clear();
            foreach (var (vector, node) in previousNodes)
            {
                _nodesByVector[vector] = node;
            }

            _entryPoint = previousEntryPoint;
            _maximumLevel = previousMaximumLevel;
            _removedCount = previousRemovedCount;
            throw;
        }
    }

    private void RebuildCore(Node[] live, GraphJournal journal)
    {
        _nodesByVector.Clear();
        foreach (var node in live)
        {
            _nodesByVector[node.Vector] = node;
        }

        _entryPoint = null;
        _maximumLevel = -1;
        _removedCount = 0;
        foreach (var node in live)
        {
            node.Parent = null;
            node.ChildCount = 0;
            foreach (var layer in node.Neighbors)
            {
                layer.Clear();
            }
        }

        // Each node keeps the level it was drawn at insertion, so a rebuild consumes no randomness.
        foreach (var node in live)
        {
            Insert(node, journal);
        }
    }

    private int NextLevel()
    {
        var level = 0;
        while (level < 32 && _random.NextDouble() < 1D / Math.E)
        {
            level++;
        }

        return level;
    }

    private sealed class Node
    {
        internal Node(float[] vector, long sequence, int level)
        {
            Vector = vector;
            Sequence = sequence;
            Level = level;
            Neighbors = Enumerable.Range(0, level + 1).Select(static _ => new List<Node>()).ToArray();
        }

        internal float[] Vector { get; }
        internal long Sequence { get; }
        internal int Level { get; }
        internal List<Node>[] Neighbors { get; }

        /// <summary>The node this one hangs from in the layer-0 spanning tree; none for the first node inserted.</summary>
        internal Node? Parent { get; set; }

        /// <summary>How many nodes hang from this one in the layer-0 spanning tree; at most M.</summary>
        internal int ChildCount { get; set; }

        /// <summary>The stored ids whose vector this node is; none once it is only a routing point.</summary>
        internal List<VectorEntry<TId>> Members { get; } = [];

        internal bool Removed => Members.Count == 0;
    }

    /// <summary>Equates vectors component by component, so <c>-0</c> and <c>+0</c> share a node.</summary>
    private sealed class VectorValueComparer : IEqualityComparer<float[]>
    {
        internal static VectorValueComparer Instance { get; } = new();

        public bool Equals(float[]? x, float[]? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null || x.Length != y.Length)
            {
                return false;
            }

            for (var index = 0; index < x.Length; index++)
            {
                if (x[index] != y[index])
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(float[] obj)
        {
            var hash = new HashCode();
            foreach (var component in obj)
            {
                // Adding 0 maps -0 to +0, whose bit patterns differ although they compare equal.
                hash.Add(component + 0F);
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Records the first-seen state of adjacency lists and tree links so a failed graph mutation can be
    /// rolled back; each list or node is captured once, before its first change.
    /// </summary>
    private sealed class GraphJournal
    {
        private readonly Dictionary<List<Node>, Node[]> _lists = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Node, (Node? Parent, int ChildCount)> _links = new(ReferenceEqualityComparer.Instance);

        internal void Touch(List<Node> list) => _lists.TryAdd(list, [.. list]);

        internal void Touch(Node node) => _links.TryAdd(node, (node.Parent, node.ChildCount));

        internal void Restore()
        {
            foreach (var (list, snapshot) in _lists)
            {
                list.Clear();
                list.AddRange(snapshot);
            }

            foreach (var (node, link) in _links)
            {
                node.Parent = link.Parent;
                node.ChildCount = link.ChildCount;
            }
        }
    }

    private sealed record Candidate(Node Node, double Distance);

    private sealed class CandidateComparer : IComparer<Candidate>
    {
        internal static CandidateComparer Instance { get; } = new();

        public int Compare(Candidate? left, Candidate? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var distance = left.Distance.CompareTo(right.Distance);
            return distance != 0 ? distance : left.Node.Sequence.CompareTo(right.Node.Sequence);
        }
    }
}
