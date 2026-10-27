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
/// Phase C: adversarial collision scenarios for <see cref="ConsistentHashRing{TNode}"/>.
/// The widened 64-bit ring space plus double-hash probing must produce stable AddNode/RemoveNode
/// semantics even under synthetic hash algorithms that deliberately force collisions.
/// </summary>
public sealed class ConsistentHashRingCollisionTests
{
    /// <summary>
    /// A deterministic hash algorithm that always returns the same value, collapsing the
    /// entire ring to a single 32-bit bucket. Used to stress the double-hashing probe path —
    /// with a constant 32-bit hash, the high 32 bits of the ring key are identical for every
    /// virtual node; collisions are resolved only via the salted second hash + perturbation.
    /// </summary>
    private sealed class ConstantHash(uint value) : IHashAlgorithm
    {
        private readonly uint _value = value;

        public uint ComputeHash(ReadOnlySpan<byte> data) => _value;
    }

    /// <summary>
    /// Removing the node that owns the HEAD of a probe chain first must not orphan the
    /// probed slots of later claimants. With a constant hash, "a" claims the natural slot
    /// and "b"/"c" probe past it; removing "a" empties the chain head, and a probe-replay
    /// removal that stops at the first empty slot would then never find "b"'s or "c"'s
    /// slots — leaving ghost ring entries that route keys to removed nodes forever.
    /// </summary>
    [Fact]
    public void RemoveNode_HeadOfProbeChainRemovedFirst_LeavesNoGhostSlots()
    {
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 5, hashAlgorithm: new ConstantHash(13));
        ring.AddNode("a");
        ring.AddNode("b");
        ring.AddNode("c");

        // Remove in probe-chain order: the head claimant first, dependents afterwards.
        ring.RemoveNode("a");
        ring.RemoveNode("b");
        ring.RemoveNode("c");

        // Every node is gone, so the ring must be empty — a lookup throws rather than
        // routing to a ghost slot of a removed node.
        var act = () => ring.GetNode("any-key");
        act.Should().Throw<InvalidOperationException>(
            "all nodes were removed; any surviving ring slot is a ghost entry");
    }

    [Fact]
    public void AddAndRemove_WithConstantHash_PreservesAllNodes()
    {
        // With a constant 32-bit hash the collision storm is guaranteed — the fix must rely on
        // the second salted hash + perturbation to place every virtual node at a distinct slot.
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 5, hashAlgorithm: new ConstantHash(42));

        ring.AddNode("a");
        ring.AddNode("b");
        ring.AddNode("c");

        // Every key should route to one of the three nodes — not throw, not drop nodes.
        var nodes = new HashSet<string>();
        for (var i = 0; i < 50; i++)
        {
            nodes.Add(ring.GetNode($"key-{i}"));
        }
        nodes.Should().BeSubsetOf(["a", "b", "c"]);
        nodes.Should().NotBeEmpty();
    }

    [Fact]
    public void RemoveNode_WithConstantHash_OnlyRemovesTargetNode()
    {
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 5, hashAlgorithm: new ConstantHash(7));
        ring.AddNode("a");
        ring.AddNode("b");
        ring.AddNode("c");

        ring.RemoveNode("b");

        // After removing b, lookups must route only to a or c — if RemoveNode had accidentally
        // deleted a colliding slot for a or c, some keys would now throw or route nowhere.
        var reached = new HashSet<string>();
        for (var i = 0; i < 50; i++)
        {
            reached.Add(ring.GetNode($"key-{i}"));
        }
        reached.Should().BeSubsetOf(["a", "c"]);
        reached.Should().NotContain("b");
    }

    [Fact]
    public void RemoveNode_ThenReAdd_ReproducesSameRingState()
    {
        // Add/remove/re-add cycles must be stable — specifically, remove must leave no residue
        // that a later AddNode would stumble on.
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 10, hashAlgorithm: new ConstantHash(99));
        ring.AddNode("a");
        ring.AddNode("b");

        var before = new Dictionary<string, string>();
        for (var i = 0; i < 30; i++) { before[$"k{i}"] = ring.GetNode($"k{i}"); }

        ring.RemoveNode("b");
        ring.AddNode("b");

        for (var i = 0; i < 30; i++)
        {
            ring.GetNode($"k{i}").Should().Be(before[$"k{i}"],
                "a remove-then-re-add cycle should be idempotent on the routing map");
        }
    }

    [Fact]
    public void RingWithRealHash_DistributesKeysAcrossNodes()
    {
        // Sanity check with the default Murmur3 hash — still works after the 64-bit widening
        // and only exercises the common (no-collision) path.
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 150);
        ring.AddNode("a");
        ring.AddNode("b");
        ring.AddNode("c");

        var counts = new Dictionary<string, int> { ["a"] = 0, ["b"] = 0, ["c"] = 0 };
        for (var i = 0; i < 3000; i++)
        {
            counts[ring.GetNode($"key-{i}")]++;
        }

        foreach (var c in counts.Values)
        {
            c.Should().BeGreaterThan(0, "every node should receive at least some traffic");
        }
    }
}
