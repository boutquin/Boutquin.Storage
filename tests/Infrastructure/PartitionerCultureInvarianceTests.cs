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
using System.Globalization;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Partition routing must be a pure function of the key — not of the calling thread's
/// culture. Keys and nodes are turned into hash input through their string representation;
/// a culture-sensitive <c>ToString</c> ("1.5" under en-US, "1,5" under fr-FR) silently
/// produces different hash bytes for the same logical value, so the same key routes to
/// different partitions/nodes depending on the thread's culture, and a node added under one
/// culture cannot be removed under another.
/// </summary>
public sealed class PartitionerCultureInvarianceTests
{
    private static readonly double[] s_doubleKeys = [1.5, 2.25, -3.75, 1234.5678, 0.001, -0.5];

    private static T WithCulture<T>(string cultureName, Func<T> body)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(cultureName);
        try
        {
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void HashPartitioner_DoubleKey_PartitionIsCultureInvariant()
    {
        var partitioner = new HashPartitioner<double>(partitionCount: 1 << 20);

        foreach (var key in s_doubleKeys)
        {
            var enUs = WithCulture("en-US", () => partitioner.GetPartition(key));
            var frFr = WithCulture("fr-FR", () => partitioner.GetPartition(key));

            frFr.Should().Be(enUs,
                $"the partition of {key.ToString(CultureInfo.InvariantCulture)} must not depend on the thread culture");
        }
    }

    [Fact]
    public void ConsistentHashRing_NodeAddedUnderOneCulture_IsRemovableUnderAnother()
    {
        var ring = new ConsistentHashRing<double>(virtualNodeCount: 10);

        WithCulture("en-US", () => { ring.AddNode(1.5); return 0; });
        WithCulture("fr-FR", () => { ring.RemoveNode(1.5); return 0; });

        // The only node is gone; the ring must be empty. Culture-sensitive virtual-key
        // formatting would compute different slot hashes on removal and leave ghosts.
        var act = () => ring.GetNode("any-key");
        act.Should().Throw<InvalidOperationException>(
            "the only node was removed, so no ghost slot may remain to route to");
    }

    [Fact]
    public void ConsistentHashRing_BuiltUnderDifferentCultures_RoutesIdentically()
    {
        // Two rings over the same logical nodes, constructed under different cultures, must
        // be the same ring: culture-sensitive virtual-key formatting would place the nodes'
        // virtual slots at different positions and route the same key differently.
        var ringEnUs = WithCulture("en-US", () =>
        {
            var r = new ConsistentHashRing<double>(virtualNodeCount: 50);
            r.AddNode(1.5);
            r.AddNode(2.5);
            return r;
        });
        var ringFrFr = WithCulture("fr-FR", () =>
        {
            var r = new ConsistentHashRing<double>(virtualNodeCount: 50);
            r.AddNode(1.5);
            r.AddNode(2.5);
            return r;
        });

        for (var i = 0; i < 30; i++)
        {
            var key = $"key-{i}";
            ringFrFr.GetNode(key).Should().Be(ringEnUs.GetNode(key),
                $"the ring position of a node's virtual slots for '{key}' must not depend on the culture active at AddNode time");
        }
    }

    [Fact]
    public void RendezvousHash_DoubleNodes_RoutingIsCultureInvariant()
    {
        var hash = new RendezvousHash<double>();
        hash.AddNode(1.5);
        hash.AddNode(2.5);
        hash.AddNode(-3.25);

        for (var i = 0; i < 20; i++)
        {
            var key = $"key-{i}";
            var enUs = WithCulture("en-US", () => hash.GetNode(key));
            var frFr = WithCulture("fr-FR", () => hash.GetNode(key));

            frFr.Should().Be(enUs, $"the highest-random-weight node for '{key}' must not depend on the thread culture");
        }
    }
    private static readonly string[] s_cultures = ["en-US", "cs-CZ", "tr-TR", "de-DE", ""];

    [Fact]
    public void RendezvousHash_TiedScores_GetNodesFirstMatchesGetNode_InEveryCulture()
    {
        // A constant hash ties every candidate, so the winner is decided by the tie-break alone.
        // GetNode and GetNodes document the same descending-score ranking; the primary must agree.
        foreach (var nodes in new string[][] { ["a", "B"], ["ch", "h"], ["i", "I", "\u0130", "\u0131"] })
        {
            var hash = new RendezvousHash<string>(new ConstantHashAlgorithm(7));
            foreach (var node in nodes)
            {
                hash.AddNode(node);
            }

            foreach (var culture in s_cultures)
            {
                var single = WithCulture(culture, () => hash.GetNode("key"));
                var ranked = WithCulture(culture, () => hash.GetNodes("key", 1));

                ranked[0].Should().Be(single,
                    $"GetNodes(key, 1)[0] and GetNode(key) rank by the same score, so they must name the same primary under culture '{culture}'");
            }
        }
    }

    [Fact]
    public void RendezvousHash_TiedScores_ReplicaRankingIsCultureInvariant()
    {
        var hash = new RendezvousHash<string>(new ConstantHashAlgorithm(7));
        foreach (var node in new[] { "i", "I", "\u0130", "\u0131", "ch", "h", "a", "B" })
        {
            hash.AddNode(node);
        }

        var expected = WithCulture("", () => hash.GetNodes("key", 8));
        expected.Should().Equal(new[] { "B", "I", "a", "ch", "h", "i", "\u0130", "\u0131" },
            "tied replicas are ranked by ordinal node identity");
        foreach (var culture in s_cultures)
        {
            WithCulture(culture, () => hash.GetNodes("key", 8)).Should().Equal(expected,
                $"the replica ranking must not depend on the thread culture ('{culture}')");
        }
    }

    [Fact]
    public void RendezvousHash_TiedScores_NodeIdentityIsInvariantFormatted()
    {
        // DateOnly.ToString() is culture-sensitive: under de-DE 2024-01-02 renders "02.01.2024" and
        // 2024-10-01 renders "01.10.2024", reversing the invariant "01/02/2024" < "10/01/2024" order.
        var hash = new RendezvousHash<DateOnly>(new ConstantHashAlgorithm(7));
        hash.AddNode(new DateOnly(2024, 10, 1));
        hash.AddNode(new DateOnly(2024, 1, 2));

        var invariant = WithCulture("", () => hash.GetNode("key"));
        invariant.Should().Be(new DateOnly(2024, 1, 2));
        foreach (var culture in s_cultures)
        {
            WithCulture(culture, () => hash.GetNode("key")).Should().Be(invariant,
                $"the tie-break identity must be the invariant-formatted node string under culture '{culture}'");
            WithCulture(culture, () => hash.GetNodes("key", 2)[0]).Should().Be(invariant,
                $"GetNodes must use the same invariant identity under culture '{culture}'");
        }
    }

    [Fact]
    public void RangePartitioner_StringKeys_RouteByOrdinalOrderInEveryCulture()
    {
        var partitioner = new RangePartitioner<string>(["B", "d", "m"]);

        foreach (var culture in s_cultures)
        {
            // Ordinal: "a" (0x61) >= "B" (0x42) and < "d" -> partition 1; "ch" < "d" -> partition 1.
            WithCulture(culture, () => partitioner.GetPartition("a")).Should().Be(1,
                $"string keys route by ordinal order under culture '{culture}'");
            WithCulture(culture, () => partitioner.GetPartition("ch")).Should().Be(1,
                $"string keys route by ordinal order under culture '{culture}'");
            WithCulture(culture, () => partitioner.GetPartitionRange("a", "ch")).Should().Be((1, 1),
                $"range bounds compare by ordinal order under culture '{culture}'");
        }
    }

    [Fact]
    public void RangePartitioner_StringBoundaryValidation_IsCultureInvariant()
    {
        foreach (var culture in s_cultures)
        {
            // Ordinal order puts "ch" before "h", so ["h", "ch"] is unsorted in every culture
            // (cs-CZ collation would otherwise accept it and void BinarySearch's precondition).
            var unsorted = () => WithCulture(culture, () => new RangePartitioner<string>(["h", "ch"]));
            unsorted.Should().Throw<ArgumentException>($"boundary order is ordinal under culture '{culture}'");

            var sorted = () => WithCulture(culture, () => new RangePartitioner<string>(["B", "a"]));
            sorted.Should().NotThrow($"\"B\" precedes \"a\" ordinally under culture '{culture}'")
                .Which.PartitionCount.Should().Be(3);
        }
    }
}
