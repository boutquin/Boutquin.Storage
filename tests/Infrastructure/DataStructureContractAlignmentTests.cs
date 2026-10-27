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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Sibling-contract alignment across the four in-memory ordered stores (BTree, BPlusTree,
/// SkipListMemTable, RedBlackTree) — the same input must be accepted or rejected identically
/// everywhere, per the repo-wide <c>Guard.AgainstNullOrDefault</c> boundary convention (the
/// same alignment class as the partitioner-sibling fix). Also pins two behavioural
/// divergences: update-at-capacity must be an in-place update (no node is added), and a pure
/// update must never mutate tree shape.
/// </summary>
public sealed class DataStructureContractAlignmentTests
{
    // ── Default-key rejection: all four stores ──────────────────────────────

    [Fact]
    public async Task BTree_DefaultKey_IsRejected()
    {
        var tree = new BTree<int, string>(minimumDegree: 2);
        var act = () => tree.SetAsync(0, "v");
        await act.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task BPlusTree_DefaultKey_IsRejected()
    {
        var tree = new BPlusTree<int, string>(minimumDegree: 4);
        var act = () => tree.SetAsync(0, "v");
        await act.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task SkipListMemTable_DefaultKey_IsRejected()
    {
        var list = new SkipListMemTable<int, string>(maxSize: 8);
        var act = () => list.SetAsync(0, "v");
        await act.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task RedBlackTree_DefaultKey_IsRejected()
    {
        var tree = new RedBlackTree<int, string>(maxSize: 8);
        var act = () => tree.SetAsync(0, "v");
        await act.Should().ThrowAsync<ArgumentException>(
            "the sibling stores all reject default keys; the RB memtable must agree").ConfigureAwait(true);
    }

    // ── Default-value rejection: all four stores ────────────────────────────

    [Fact]
    public async Task BTree_DefaultValue_IsRejected()
    {
        var tree = new BTree<int, int>(minimumDegree: 2);
        var act = () => tree.SetAsync(1, 0);
        await act.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task BPlusTree_DefaultValue_IsRejected()
    {
        var tree = new BPlusTree<int, int>(minimumDegree: 4);
        var act = () => tree.SetAsync(1, 0);
        await act.Should().ThrowAsync<ArgumentException>(
            "every store must apply the same Guard.AgainstNullOrDefault boundary convention").ConfigureAwait(true);
    }

    [Fact]
    public async Task SkipListMemTable_DefaultValue_IsRejected()
    {
        var list = new SkipListMemTable<int, int>(maxSize: 8);
        var act = () => list.SetAsync(1, 0);
        await act.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task RedBlackTree_DefaultValue_IsRejected()
    {
        var tree = new RedBlackTree<int, int>(maxSize: 8);
        var act = () => tree.SetAsync(1, 0);
        await act.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
    }

    // ── Update-at-capacity parity (RB must match SkipList) ──────────────────

    [Fact]
    public async Task RedBlackTree_UpdateOfExistingKey_AtCapacity_Succeeds()
    {
        // An update adds no node, so a full memtable must accept it — SkipListMemTable
        // already does (IsFull && !ContainsKey predicate); the RB memtable threw before
        // checking key existence, so a tombstone/value overwrite at exactly-full capacity
        // failed on one memtable and succeeded on the other.
        var tree = new RedBlackTree<int, string>(maxSize: 1);
        await tree.SetAsync(1, "a");

        await tree.SetAsync(1, "b");

        var (v, found) = await tree.TryGetValueAsync(1);
        found.Should().BeTrue();
        v.Should().Be("b");
    }

    // ── Pure update must not mutate tree shape ──────────────────────────────

    [Fact]
    public async Task BTree_UpdateOfExistingKey_UnderFullRoot_DoesNotChangeHeight()
    {
        // CLRS proactive descent splits a full root before knowing the operation is an
        // update; an update-only workload must not grow the tree.
        var tree = new BTree<int, string>(minimumDegree: 2);
        await tree.SetAsync(1, "a");
        await tree.SetAsync(2, "b");
        await tree.SetAsync(3, "c"); // root now holds 2t-1 = 3 keys (full)
        var heightBefore = tree.Height;

        await tree.SetAsync(2, "b2"); // pure update

        tree.Height.Should().Be(heightBefore,
            "an update of an existing key adds no node and must not split the root");
        var (v, found) = await tree.TryGetValueAsync(2);
        found.Should().BeTrue();
        v.Should().Be("b2");
    }

    // ── Empty-tree Height parity ─────────────────────────────────────────────

    [Fact]
    public void BPlusTree_EmptyTree_HeightIsZero()
    {
        // IBTree pins "an empty tree has height 0" and BTree honors it; the B+ sibling
        // reported 1 for its empty root leaf.
        new BPlusTree<int, string>(minimumDegree: 4).Height.Should().Be(0,
            "the sibling stores agree that an empty tree has height 0");
    }
}
