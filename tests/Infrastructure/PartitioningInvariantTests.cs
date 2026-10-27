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
/// Exact algorithmic pins for the partitioners (DDIA 2e Ch. 7). The scripted-hash tests place
/// ring slots at hand-computed positions so first-clockwise selection, the inclusive boundary
/// (a key hashing exactly onto a slot belongs to that slot's node), and wrap-around are pinned
/// exactly rather than statistically. The minimal-remap tests pin the defining consistent-
/// hashing property (book p. 263: "as few keys as possible are moved") in its exact form:
/// adding a node moves keys only TO the new node; removing a node moves only ITS keys.
/// </summary>
public sealed class PartitioningInvariantTests
{
    /// <summary>
    /// Returns a scripted value for inputs whose UTF-8 decoding matches a mapped string, and 0
    /// otherwise. The ring composes its 64-bit slot as <c>hi &lt;&lt; 32 | lo</c> where
    /// <c>hi</c> hashes the raw bytes and <c>lo</c> hashes a salted copy; salted bytes never
    /// decode to a mapped string, so <c>lo</c> is always 0 and every slot sits at exactly
    /// <c>mapped-value &lt;&lt; 32</c> — hand-computable positions.
    /// </summary>
    private sealed class ScriptedHash(Dictionary<string, uint> map) : IHashAlgorithm
    {
        public uint ComputeHash(ReadOnlySpan<byte> data)
            => map.TryGetValue(Encoding.UTF8.GetString(data), out var v) ? v : 0u;
    }

    private static ConsistentHashRing<string> BuildScriptedRing()
    {
        // One virtual node per physical node: slots at exactly 10, 20, 30 (<<32).
        var ring = new ConsistentHashRing<string>(
            virtualNodeCount: 1,
            hashAlgorithm: new ScriptedHash(new Dictionary<string, uint>
            {
                ["A#0"] = 10,
                ["B#0"] = 20,
                ["C#0"] = 30,
                ["k05"] = 5,
                ["k10"] = 10,
                ["k15"] = 15,
                ["k25"] = 25,
                ["k35"] = 35,
            }));
        ring.AddNode("A");
        ring.AddNode("B");
        ring.AddNode("C");
        return ring;
    }

    [Fact]
    public void GetNode_FirstClockwiseSlot_ExactPositions()
    {
        var ring = BuildScriptedRing();

        ring.GetNode("k05").Should().Be("A", "5 → first slot clockwise is A at 10");
        ring.GetNode("k15").Should().Be("B", "15 → first slot clockwise is B at 20");
        ring.GetNode("k25").Should().Be("C", "25 → first slot clockwise is C at 30");
    }

    [Fact]
    public void GetNode_KeyHashesExactlyOntoSlot_BelongsToThatSlot()
    {
        var ring = BuildScriptedRing();

        ring.GetNode("k10").Should().Be("A", "a key hashing exactly onto a slot belongs to that slot's node (first slot >= hash)");
    }

    [Fact]
    public void GetNode_PastLastSlot_WrapsToSmallestSlot()
    {
        var ring = BuildScriptedRing();

        ring.GetNode("k35").Should().Be("A", "35 is past the last slot (30), so the ring wraps to the smallest slot (A at 10)");
    }

    [Fact]
    public void GetNodes_PreferenceList_IsClockwiseWalkWithWrap()
    {
        var ring = BuildScriptedRing();

        var nodes = ring.GetNodes("k25", 3);

        nodes.Should().Equal("C", "A", "B");
    }

    [Fact]
    public void ConsistentHashRing_AddNode_MovesKeysOnlyToTheNewNode()
    {
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 50);
        ring.AddNode("server1");
        ring.AddNode("server2");
        ring.AddNode("server3");

        var keys = Enumerable.Range(0, 300).Select(i => $"key{i}").ToList();
        var before = keys.ToDictionary(k => k, ring.GetNode);

        ring.AddNode("server4");

        foreach (var key in keys)
        {
            var after = ring.GetNode(key);
            if (after != before[key])
            {
                after.Should().Be("server4",
                    $"adding a node may only steal keys for itself; '{key}' moved from {before[key]}");
            }
        }
    }

    [Fact]
    public void ConsistentHashRing_RemoveNode_MovesOnlyTheRemovedNodesKeys()
    {
        var ring = new ConsistentHashRing<string>(virtualNodeCount: 50);
        ring.AddNode("server1");
        ring.AddNode("server2");
        ring.AddNode("server3");

        var keys = Enumerable.Range(0, 300).Select(i => $"key{i}").ToList();
        var before = keys.ToDictionary(k => k, ring.GetNode);

        ring.RemoveNode("server2");

        foreach (var key in keys)
        {
            var after = ring.GetNode(key);
            if (before[key] == "server2")
            {
                after.Should().NotBe("server2", $"'{key}' lived on the removed node and must move");
            }
            else
            {
                after.Should().Be(before[key],
                    $"removing a node must not move keys that lived elsewhere; '{key}' was on {before[key]}");
            }
        }
    }

    [Fact]
    public void RendezvousHash_AddNode_MovesKeysOnlyToTheNewNode()
    {
        var hrw = new RendezvousHash<string>();
        hrw.AddNode("server1");
        hrw.AddNode("server2");
        hrw.AddNode("server3");

        var keys = Enumerable.Range(0, 300).Select(i => $"key{i}").ToList();
        var before = keys.ToDictionary(k => k, hrw.GetNode);

        hrw.AddNode("server4");

        foreach (var key in keys)
        {
            var after = hrw.GetNode(key);
            if (after != before[key])
            {
                after.Should().Be("server4",
                    $"HRW scores of existing (key, node) pairs are unchanged by an add; '{key}' moved from {before[key]}");
            }
        }
    }

    [Fact]
    public void RendezvousHash_RemoveNode_MovesOnlyTheRemovedNodesKeys()
    {
        var hrw = new RendezvousHash<string>();
        hrw.AddNode("server1");
        hrw.AddNode("server2");
        hrw.AddNode("server3");

        var keys = Enumerable.Range(0, 300).Select(i => $"key{i}").ToList();
        var before = keys.ToDictionary(k => k, hrw.GetNode);

        hrw.RemoveNode("server2");

        foreach (var key in keys)
        {
            var after = hrw.GetNode(key);
            if (before[key] == "server2")
            {
                after.Should().NotBe("server2", $"'{key}' lived on the removed node and must move");
            }
            else
            {
                after.Should().Be(before[key],
                    $"HRW scores of surviving (key, node) pairs are unchanged by a remove; '{key}' was on {before[key]}");
            }
        }
    }
}
