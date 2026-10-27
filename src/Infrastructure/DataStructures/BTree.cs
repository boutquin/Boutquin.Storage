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
namespace Boutquin.Storage.Infrastructure.DataStructures;

using Boutquin.Storage.Domain.Helpers;
using Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// In-memory implementation of a B-tree data structure.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the tree.</typeparam>
/// <typeparam name="TValue">The type of the values in the tree.</typeparam>
/// <remarks>
/// <para>A B-tree of minimum degree <c>t</c> (sometimes loosely called the order, though Knuth's order is the maximum child count 2t) has the following properties:</para>
/// <para>- Every node has at most <c>2t - 1</c> keys and <c>2t</c> children.</para>
/// <para>- Every non-root node has at least <c>t - 1</c> keys.</para>
/// <para>- The root has at least 1 key (when non-empty).</para>
/// <para>- All leaves are at the same depth.</para>
///
/// <para><b>Why wrap synchronous operations in Tasks?</b> The <see cref="IBTree{TKey, TValue}"/> interface
/// extends <see cref="IBulkKeyValueStore{TKey, TValue}"/> which is async to support implementations backed
/// by I/O (e.g., on-disk B-trees). This in-memory implementation completes synchronously, so we wrap
/// results in completed tasks to satisfy the interface contract without incurring async state machine overhead.</para>
///
/// <para><b>Insertion strategy:</b> This implementation uses proactive splitting (split-on-the-way-down).
/// When traversing from root to leaf during insertion, any full node encountered is split before descending
/// into it. This guarantees that when we reach the leaf, it has room for the new key, requiring only a
/// single pass down the tree. This is the approach described in CLRS (Introduction to Algorithms).</para>
///
/// <para><b>Deletion:</b> Not supported in this implementation (throws <see cref="NotSupportedException"/>).
/// B-tree deletion requires up to six redistribution/merge cases per CLRS and the DDIA 2e p. 127
/// discussion of deletion complexity in page-oriented storage engines; implementing it correctly in an
/// in-memory test fixture adds complexity without covering the storage algorithms this library targets.</para>
///
/// <para><b>Thread safety:</b> This class is not thread-safe. External synchronization is required for concurrent access.</para>
/// </remarks>
public sealed class BTree<TKey, TValue> : IBTree<TKey, TValue>
    where TKey : IComparable<TKey>, IEquatable<TKey>
{
    /// <summary>
    /// Represents a node in the B-tree. Each node contains a list of keys and values,
    /// and (for internal nodes) a list of child node references.
    /// </summary>
    private sealed class Node
    {
        /// <summary>The keys stored in this node, maintained in sorted order.</summary>
        public List<TKey> Keys { get; } = [];

        /// <summary>The values corresponding to each key, in the same positional order.</summary>
        public List<TValue> Values { get; } = [];

        /// <summary>
        /// Child node references. An internal node with <c>k</c> keys has <c>k + 1</c> children.
        /// Leaf nodes have an empty children list.
        /// </summary>
        public List<Node> Children { get; } = [];

        /// <summary>Whether this node is a leaf (has no children).</summary>
        public bool IsLeaf => Children.Count == 0;
    }

    private readonly int _minimumDegree; // The minimum degree t of the B-tree
    private Node? _root; // The root node of the B-tree
    private int _height; // The current height of the tree

    /// <summary>
    /// Initializes a new instance of the <see cref="BTree{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="minimumDegree">
    /// The minimum degree t of the B-tree (every node holds at most 2t − 1 keys). Must be at least 2.
    /// Each node can hold between <c>t - 1</c> and <c>2t - 1</c> keys.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="minimumDegree"/> is less than 2.
    /// </exception>
    public BTree(int minimumDegree)
    {
        if (minimumDegree < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumDegree),
                minimumDegree,
                "Minimum degree must be at least 2.");
        }

        _minimumDegree = minimumDegree;
    }

    /// <inheritdoc />
    public int MinimumDegree => _minimumDegree;

    /// <inheritdoc />
    public int Height => _height;

    /// <inheritdoc />
    public async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        Insert(key, value);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard.AgainstNullOrDefault(() => key);
        var result = Search(key);
        return await Task.FromResult(result).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard.AgainstNullOrDefault(() => key);
        var (_, found) = Search(key);
        return await Task.FromResult(found).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard.AgainstNullOrDefault(() => key);
        // Why not implemented? In an LSM-tree architecture, deletes are handled by writing tombstone
        // markers, not by removing keys from the tree. B-tree deletion requires complex key redistribution
        // and node merging (6+ cases) for no architectural benefit in this context.
        throw new NotSupportedException("Remove operation is not supported in an append-only storage engine.");
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _root = null;
        _height = 0;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = new List<(TKey Key, TValue Value)>();
        if (_root != null)
        {
            InOrderTraversal(_root, items);
        }

        return await Task.FromResult<IEnumerable<(TKey Key, TValue Value)>>(items).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard.AgainstNullOrDefault(() => items);
        foreach (var item in items)
        {
            // Same per-item guards as SetAsync: Insert itself validates nothing.
            var key = item.Key;
            var value = item.Value;
            Guard.AgainstNullOrDefault(() => key);
            Guard.AgainstNullOrDefault(() => value);
            Insert(key, value);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Searches for a key in the B-tree.
    /// </summary>
    /// <param name="key">The key to search for.</param>
    /// <returns>A tuple containing the value and a boolean indicating whether the key was found.</returns>
    private (TValue Value, bool Found) Search(TKey key)
    {
        if (_root == null)
        {
            return (default!, false);
        }

        return SearchNode(_root, key);
    }

    /// <summary>
    /// Iteratively searches for a key starting from the given node.
    /// </summary>
    /// <param name="node">The node to start searching from.</param>
    /// <param name="key">The key to search for.</param>
    /// <returns>A tuple containing the value and a boolean indicating whether the key was found.</returns>
    /// <remarks>
    /// Uses iterative descent rather than recursion to avoid stack overflow on deep trees,
    /// matching the defensive approach used in <see cref="RedBlackTree{TKey, TValue}"/>.
    /// </remarks>
    private static (TValue Value, bool Found) SearchNode(Node node, TKey key)
    {
        var current = node;

        while (true)
        {
            // Find the first key greater than or equal to the search key
            var i = 0;
            while (i < current.Keys.Count && KeyOrder<TKey>.Compare(key, current.Keys[i]) > 0)
            {
                i++;
            }

            // If we found the exact key, return the associated value
            if (i < current.Keys.Count && KeyOrder<TKey>.Compare(key, current.Keys[i]) == 0)
            {
                return (current.Values[i], true);
            }

            // If this is a leaf node, the key doesn't exist
            if (current.IsLeaf)
            {
                return (default!, false);
            }

            // Descend into the appropriate child
            current = current.Children[i];
        }
    }

    /// <summary>
    /// Inserts a key-value pair into the B-tree, or updates the value if the key already exists.
    /// </summary>
    /// <param name="key">The key to insert.</param>
    /// <param name="value">The value to associate with the key.</param>
    /// <remarks>
    /// <para>Uses proactive splitting (split-on-the-way-down): when traversing from root to leaf,
    /// any full node encountered is split before descending into it. This guarantees the leaf
    /// has room for insertion and requires only a single downward pass.</para>
    ///
    /// <para>If the root is full, it is split first, creating a new root and increasing the tree height by 1.
    /// This is the only operation that increases the height of a B-tree.</para>
    /// </remarks>
    private void Insert(TKey key, TValue value)
    {
        // If the tree is empty, create a new root
        if (_root == null)
        {
            _root = new Node();
            _root.Keys.Add(key);
            _root.Values.Add(value);
            _height = 1;
            return;
        }

        // If root is full, split it before descending — but a pure UPDATE adds no key
        // and must not mutate tree shape, so probe for the key first. The extra search
        // runs only on the rare split-imminent path.
        if (_root.Keys.Count == 2 * _minimumDegree - 1)
        {
            if (TryUpdateInPlace(_root, key, value))
            {
                return;
            }

            var newRoot = new Node();
            newRoot.Children.Add(_root);
            SplitChild(newRoot, 0);
            _root = newRoot;
            _height++;
        }

        InsertNonFull(_root, key, value);
    }

    /// <summary>
    /// Structural invariant checker mirroring the red-black tree's
    /// <c>SatisfiesRedBlackInvariants</c> precedent: behavioural sorted-iteration tests
    /// cannot catch a silent balance regression. Validates (a) per-node occupancy bounds
    /// (non-root: t−1 ≤ keys ≤ 2t−1; root: 1 ≤ keys ≤ 2t−1 when the tree is non-empty),
    /// (b) internal nodes carry exactly keys+1 children and keys.Count values,
    /// (c) keys are strictly ascending within every node and across separator boundaries,
    /// and (d) every leaf sits at the same depth. Test-only via InternalsVisibleTo.
    /// </summary>
    internal bool SatisfiesBTreeInvariants()
    {
        if (_root is null)
        {
            return true;
        }

        var leafDepth = -1;

        // Bounds are tracked with explicit presence flags — a TKey? null-sentinel collapses
        // to default(TKey) for value-type keys and falsely constrains the leftmost/rightmost
        // descent paths.
        bool Walk(Node node, bool isRoot, int depth, bool hasLower, TKey lowerBound, bool hasUpper, TKey upperBound)
        {
            var keyCount = node.Keys.Count;
            var minKeys = isRoot ? 1 : _minimumDegree - 1;
            if (keyCount < minKeys || keyCount > 2 * _minimumDegree - 1)
            {
                return false;
            }
            if (node.Values.Count != keyCount)
            {
                return false;
            }
            for (var i = 0; i < keyCount; i++)
            {
                if (i > 0 && KeyOrder<TKey>.Compare(node.Keys[i - 1], node.Keys[i]) >= 0)
                {
                    return false;
                }
                if (hasLower && KeyOrder<TKey>.Compare(node.Keys[i], lowerBound) <= 0)
                {
                    return false;
                }
                if (hasUpper && KeyOrder<TKey>.Compare(node.Keys[i], upperBound) >= 0)
                {
                    return false;
                }
            }

            if (node.IsLeaf)
            {
                if (leafDepth == -1)
                {
                    leafDepth = depth;
                }
                return leafDepth == depth;
            }

            if (node.Children.Count != keyCount + 1)
            {
                return false;
            }
            for (var i = 0; i <= keyCount; i++)
            {
                var childHasLower = i > 0 || hasLower;
                var childLower = i == 0 ? lowerBound : node.Keys[i - 1];
                var childHasUpper = i < keyCount || hasUpper;
                var childUpper = i == keyCount ? upperBound : node.Keys[i];
                if (!Walk(node.Children[i], isRoot: false, depth + 1, childHasLower, childLower, childHasUpper, childUpper))
                {
                    return false;
                }
            }
            return true;
        }

        return Walk(_root, isRoot: true, 0, hasLower: false, default!, hasUpper: false, default!);
    }

    private static bool TryUpdateInPlace(Node node, TKey key, TValue value)
    {
        var current = node;
        while (true)
        {
            var i = 0;
            while (i < current.Keys.Count && KeyOrder<TKey>.Compare(key, current.Keys[i]) > 0)
            {
                i++;
            }
            if (i < current.Keys.Count && KeyOrder<TKey>.Compare(key, current.Keys[i]) == 0)
            {
                current.Values[i] = value;
                return true;
            }
            if (current.IsLeaf)
            {
                return false;
            }
            current = current.Children[i];
        }
    }

    /// <summary>
    /// Inserts a key-value pair into a node that is guaranteed to be non-full.
    /// </summary>
    /// <param name="node">The non-full node to insert into.</param>
    /// <param name="key">The key to insert.</param>
    /// <param name="value">The value to associate with the key.</param>
    /// <remarks>
    /// If the node is a leaf, the key is inserted directly. If the node is internal,
    /// the appropriate child is found; if that child is full, it is split first, then
    /// the descent continues into the (now non-full) correct child.
    /// </remarks>
    private void InsertNonFull(Node node, TKey key, TValue value)
    {
        var current = node;

        while (true)
        {
            var i = current.Keys.Count - 1;

            if (current.IsLeaf)
            {
                // Check for duplicate key in this leaf
                var insertPos = 0;
                while (insertPos < current.Keys.Count && KeyOrder<TKey>.Compare(key, current.Keys[insertPos]) > 0)
                {
                    insertPos++;
                }

                // If key already exists, update the value
                if (insertPos < current.Keys.Count && KeyOrder<TKey>.Compare(key, current.Keys[insertPos]) == 0)
                {
                    current.Values[insertPos] = value;
                    return;
                }

                // Insert the key-value pair at the correct position to maintain sorted order
                current.Keys.Insert(insertPos, key);
                current.Values.Insert(insertPos, value);
                return;
            }

            // Find the child that will receive the new key
            while (i >= 0 && KeyOrder<TKey>.Compare(key, current.Keys[i]) < 0)
            {
                i--;
            }

            // Check for duplicate key in this internal node
            if (i >= 0 && KeyOrder<TKey>.Compare(key, current.Keys[i]) == 0)
            {
                current.Values[i] = value;
                return;
            }

            i++;

            // If the child is full, split it before descending
            if (current.Children[i].Keys.Count == 2 * _minimumDegree - 1)
            {
                SplitChild(current, i);

                // After splitting, determine which of the two children to descend into
                if (KeyOrder<TKey>.Compare(key, current.Keys[i]) == 0)
                {
                    // The median key from the split matches our key — update the value
                    current.Values[i] = value;
                    return;
                }

                if (KeyOrder<TKey>.Compare(key, current.Keys[i]) > 0)
                {
                    i++;
                }
            }

            current = current.Children[i];
        }
    }

    /// <summary>
    /// Splits a full child node into two nodes, promoting the median key to the parent.
    /// </summary>
    /// <param name="parent">The parent node containing the full child.</param>
    /// <param name="childIndex">The index of the full child in the parent's children list.</param>
    /// <remarks>
    /// <para>The full child has <c>2t - 1</c> keys. After splitting:</para>
    /// <para>- The first <c>t - 1</c> keys remain in the original child.</para>
    /// <para>- The median key (at index <c>t - 1</c>) is promoted to the parent.</para>
    /// <para>- The last <c>t - 1</c> keys move to a new sibling node.</para>
    /// <para>- If the child is internal, its children are also distributed between the two nodes.</para>
    /// </remarks>
    private void SplitChild(Node parent, int childIndex)
    {
        var fullChild = parent.Children[childIndex];
        var newChild = new Node();
        var t = _minimumDegree;

        // Copy the last (t - 1) keys and values to the new child
        for (var j = 0; j < t - 1; j++)
        {
            newChild.Keys.Add(fullChild.Keys[t + j]);
            newChild.Values.Add(fullChild.Values[t + j]);
        }

        // If the full child is not a leaf, copy the last t children to the new child
        if (!fullChild.IsLeaf)
        {
            for (var j = 0; j < t; j++)
            {
                newChild.Children.Add(fullChild.Children[t + j]);
            }
        }

        // The median key and value to promote
        var medianKey = fullChild.Keys[t - 1];
        var medianValue = fullChild.Values[t - 1];

        // Remove the keys, values, and children that were moved or promoted
        // Remove from the end to avoid index shifting issues
        if (!fullChild.IsLeaf)
        {
            fullChild.Children.RemoveRange(t, t);
        }

        fullChild.Keys.RemoveRange(t - 1, t);
        fullChild.Values.RemoveRange(t - 1, t);

        // Insert the new child into the parent's children list
        parent.Children.Insert(childIndex + 1, newChild);

        // Promote the median key and value to the parent
        parent.Keys.Insert(childIndex, medianKey);
        parent.Values.Insert(childIndex, medianValue);
    }

    /// <summary>
    /// Performs a recursive in-order traversal of the B-tree to collect all key-value pairs in sorted order.
    /// </summary>
    /// <param name="node">The current node to traverse.</param>
    /// <param name="items">The list to collect the key-value pairs into.</param>
    /// <remarks>
    /// <para>For a B-tree node with keys [k0, k1, ..., kn-1] and children [c0, c1, ..., cn],
    /// the in-minimumDegree traversal visits: c0, k0, c1, k1, ..., cn-1, kn-1, cn.</para>
    ///
    /// <para>B-tree depth is O(log_t n). With minimum degree t = 2, a tree with 1 billion elements
    /// has at most ~30 levels. Stack overflow from recursion is not a practical concern for B-trees,
    /// unlike binary search trees where sequential insertion can produce O(n) depth. The logarithmic
    /// depth guarantee makes recursion both safe and clearer than an iterative approach.</para>
    /// </remarks>
    private static void InOrderTraversal(Node node, List<(TKey Key, TValue Value)> items)
    {
        for (var i = 0; i < node.Keys.Count; i++)
        {
            // Visit the i-th child before the i-th key
            if (!node.IsLeaf)
            {
                InOrderTraversal(node.Children[i], items);
            }

            items.Add((node.Keys[i], node.Values[i]));
        }

        // Visit the last child (rightmost)
        if (!node.IsLeaf)
        {
            InOrderTraversal(node.Children[node.Keys.Count], items);
        }
    }
}

/// <summary>
/// The key order shared by every in-memory ordered structure in this assembly (<see cref="BTree{TKey, TValue}"/>,
/// <see cref="BPlusTree{TKey, TValue}"/>, <see cref="RedBlackTree{TKey, TValue}"/>,
/// <see cref="SkipListMemTable{TKey, TValue}"/> and <see cref="Indexing.InMemoryStorageIndex{TKey, TValue}"/>):
/// the key's own <see cref="IComparable{T}"/> order, except that a string — alone, or as a tuple component,
/// nested tuples included — compares by Unicode code point.
/// </summary>
/// <remarks>
/// <para><b>Why not <c>key.CompareTo</c> for strings?</b> <see cref="string.CompareTo(string)"/> is the
/// current-culture comparison. It reports ordinally distinct strings (a soft hyphen, an embedded NUL,
/// canonically equivalent forms) as equal, so a structure that identifies keys by a zero comparison merges two
/// keys into one and returns one key's value for the other; and it orders keys by the calling thread's culture,
/// so two processes iterate the same keys differently. Code-point order is
/// <see cref="OrderedByteHelpers.CompareStringByteLex"/> — the UTF-8 byte order the on-disk key formats and
/// <see cref="SerializableWrapper{T}"/> already use — under which zero means the same string.</para>
/// <para>A tuple's own comparison orders its string components with the same culture comparison; its
/// structural comparison takes the component comparer as an argument, so the code-point relation reaches every
/// component.</para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
internal static class KeyOrder<TKey>
    where TKey : IComparable<TKey>
{
    private static readonly bool s_isTuple =
        typeof(ITuple).IsAssignableFrom(typeof(TKey)) && typeof(IStructuralComparable).IsAssignableFrom(typeof(TKey));

    /// <summary>The key order as an <see cref="IComparer{T}"/>, for the BCL sorted collections.</summary>
    internal static IComparer<TKey> Comparer { get; } = Comparer<TKey>.Create(Compare);

    /// <summary>Compares two non-null keys in the shared key order.</summary>
    /// <param name="left">The first key.</param>
    /// <param name="right">The second key.</param>
    /// <returns>A negative value, zero, or a positive value as <paramref name="left"/> orders before, is the same
    /// key as, or orders after <paramref name="right"/>.</returns>
    internal static int Compare(TKey left, TKey right)
    {
        // typeof tests on TKey are JIT-time constants, so a value-type key keeps its direct CompareTo call.
        if (typeof(TKey) == typeof(string))
        {
            return OrderedByteHelpers.CompareStringByteLex((string)(object)left!, (string)(object)right!);
        }

        return s_isTuple
            ? CodePointComponentComparer.Instance.Compare(left, right)
            : left.CompareTo(right);
    }
}

/// <summary>
/// Compares tuple components in their natural order, except that strings compare by Unicode code point and
/// nested tuples recurse with this comparer (see <see cref="KeyOrder{TKey}"/>).
/// </summary>
internal sealed class CodePointComponentComparer : IComparer
{
    private static readonly ConcurrentDictionary<Type, IComparer> s_typedComparers = new();

    private CodePointComponentComparer()
    {
    }

    internal static CodePointComponentComparer Instance { get; } = new();

    public int Compare(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        if (x is string left && y is string right)
        {
            return OrderedByteHelpers.CompareStringByteLex(left, right);
        }

        if (x is ITuple && x is IStructuralComparable structural)
        {
            return structural.CompareTo(y, this);
        }

        if (x is IComparable)
        {
            return System.Collections.Comparer.Default.Compare(x, y);
        }

        // A component comparable only through IComparable<T>: use the same Comparer<T>.Default the tuple's own
        // CompareTo would have used.
        return s_typedComparers
            .GetOrAdd(x.GetType(), static type =>
                (IComparer)typeof(Comparer<>).MakeGenericType(type).GetProperty(nameof(Comparer<object>.Default))!.GetValue(null)!)
            .Compare(x, y);
    }
}
