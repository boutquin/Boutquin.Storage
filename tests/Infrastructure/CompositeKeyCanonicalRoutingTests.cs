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
/// Composite (tuple) keys and nodes must route by one canonical, culture-invariant form. A
/// <see cref="ValueTuple"/> is not <see cref="IFormattable"/>, so its <c>ToString</c> prints each
/// component with the calling thread's culture ("1.5" vs "1,5") and with the component's
/// non-canonical text (<c>1.0m</c> vs <c>1.00m</c>); its default comparer orders string components
/// by the thread culture. Every partitioner must instead see equal keys as equal bytes, the same
/// key as the same bytes in every culture, and string components in ordinal order.
/// </summary>
public sealed class CompositeKeyCanonicalRoutingTests
{
    private static readonly string[] s_cultures = ["en-US", "fr-FR", "de-DE", "cs-CZ", "sv-SE", "tr-TR", ""];

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
    public void HashPartitioner_EqualCompositeKeys_SharePartitionAndSaltSet()
    {
        var partitioner = new HashPartitioner<(decimal, int)>(partitionCount: 1 << 20);
        var dtoPartitioner = new HashPartitioner<(DateTimeOffset, string)>(partitionCount: 1 << 20);
        var salting = new SaltedHotKeyStrategy<(decimal, int)>();

        foreach (var (a, b) in new[] { ((1.0m, 3), (1.00m, 3)), ((-2.50m, 7), (-2.5m, 7)), ((0m, 0), (0.000m, 0)), ((1234.5600m, -1), (1234.56m, -1)) })
        {
            a.Equals(b).Should().BeTrue();
            a.CompareTo(b).Should().Be(0);
            partitioner.GetPartition(a).Should().Be(partitioner.GetPartition(b),
                $"{a} and {b} are one composite key, so they must share a partition");
            salting.GetReadKeys(a, 4).Should().Equal(salting.GetReadKeys(b, 4),
                $"{a} and {b} are one composite key, so a read of one must cover every salt a write of the other used");
        }

        var utc = new DateTimeOffset(2024, 3, 5, 12, 30, 15, 123, TimeSpan.Zero);
        var shifted = utc.ToOffset(TimeSpan.FromHours(-5));
        (utc, "x").CompareTo((shifted, "x")).Should().Be(0);
        dtoPartitioner.GetPartition((shifted, "x")).Should().Be(dtoPartitioner.GetPartition((utc, "x")),
            "one instant in two offsets is one composite key");
    }

    [Fact]
    public void HashPartitioner_CompositeKey_PartitionAndSaltSetAreCultureInvariant()
    {
        var partitioner = new HashPartitioner<(decimal, int)>(partitionCount: 1 << 20);
        var doublePartitioner = new HashPartitioner<(double, string)>(partitionCount: 1 << 20);
        var salting = new SaltedHotKeyStrategy<(decimal, int)>();
        var key = (1.5m, 7);

        var expectedPartition = WithCulture("en-US", () => partitioner.GetPartition(key));
        var expectedSalts = WithCulture("en-US", () => salting.GetReadKeys(key, 4));
        var expectedDouble = WithCulture("en-US", () => doublePartitioner.GetPartition((-2.25, "h")));
        foreach (var culture in s_cultures)
        {
            WithCulture(culture, () => partitioner.GetPartition(key)).Should().Be(expectedPartition,
                $"the partition of {key} must not depend on the thread culture ('{culture}')");
            WithCulture(culture, () => salting.GetReadKeys(key, 4)).Should().Equal(expectedSalts,
                $"the salt set of {key} must not depend on the thread culture ('{culture}')");
            WithCulture(culture, () => doublePartitioner.GetPartition((-2.25, "h"))).Should().Be(expectedDouble,
                $"a negative double component must not move the key under culture '{culture}'");
        }
    }

    [Fact]
    public void RangePartitioner_CompositeKeysWithStringComponents_OrderOrdinallyInEveryCulture()
    {
        // Ordinal: "ch" < "ci" < "hz". cs-CZ collation sorts "ch" after "h", which would reject the
        // boundaries and route ("ci", 0) below "ch".
        var partitioner = WithCulture("en-US", () => new RangePartitioner<(string, int)>([("ch", 0), ("hz", 0)]));
        foreach (var culture in s_cultures)
        {
            WithCulture(culture, () => partitioner.GetPartition(("ci", 0))).Should().Be(1,
                $"composite keys order their string component ordinally under culture '{culture}'");
            WithCulture(culture, () => partitioner.GetPartition(("d", 5))).Should().Be(1,
                $"composite keys order their string component ordinally under culture '{culture}'");
            WithCulture(culture, () => partitioner.GetPartitionRange(("ci", 0), ("d", 0))).Should().Be((1, 1),
                $"range bounds compare composite keys ordinally under culture '{culture}'");

            var sorted = () => WithCulture(culture, () => new RangePartitioner<(string, int)>([("ch", 0), ("hz", 0)]));
            sorted.Should().NotThrow($"(\"ch\", 0) precedes (\"hz\", 0) ordinally under culture '{culture}'")
                .Which.PartitionCount.Should().Be(3);

            // Ordinal puts "B" (0x42) before "C" before "a" (0x61); every linguistic order, the invariant
            // culture's included, puts "a" first. A culture captured once per process is still not ordinal.
            var caseOrdered = () => WithCulture(culture, () => new RangePartitioner<(string, int)>([("B", 0), ("a", 0)]));
            caseOrdered.Should().NotThrow($"(\"B\", 0) precedes (\"a\", 0) ordinally under culture '{culture}'")
                .Which.GetPartition(("C", 0)).Should().Be(1, $"(\"C\", 0) lies between (\"B\", 0) and (\"a\", 0) ordinally under culture '{culture}'");

            var unsorted = () => WithCulture(culture, () => new RangePartitioner<(string, int)>([("h", 0), ("ch", 0)]));
            unsorted.Should().Throw<ArgumentException>($"(\"ch\", 0) precedes (\"h\", 0) ordinally under culture '{culture}'");
        }
    }

    [Fact]
    public void ConsistentHashRing_CompositeNodes_BuiltUnderDifferentCultures_RouteIdentically()
    {
        static ConsistentHashRing<(string Host, double Weight)> Build() =>
            AddAll(new ConsistentHashRing<(string Host, double Weight)>(virtualNodeCount: 50),
                [("a", 1.5), ("b", -2.25), ("c", 0.125)]);

        var expected = WithCulture("en-US", Build);
        foreach (var culture in s_cultures)
        {
            var ring = WithCulture(culture, Build);
            for (var i = 0; i < 200; i++)
            {
                var key = $"key-{i}";
                ring.GetNode(key).Should().Be(expected.GetNode(key),
                    $"a ring of composite nodes built under culture '{culture}' must place them where every other host does");
            }
        }
    }

    [Fact]
    public void RendezvousHash_CompositeNodes_RoutingIsCultureInvariant()
    {
        var hash = new RendezvousHash<(string Host, double Weight)>();
        hash.AddNode(("a", 1.5));
        hash.AddNode(("b", -2.25));
        hash.AddNode(("c", 0.125));

        for (var i = 0; i < 200; i++)
        {
            var key = $"key-{i}";
            var expected = WithCulture("en-US", () => hash.GetNode(key));
            foreach (var culture in s_cultures)
            {
                WithCulture(culture, () => hash.GetNode(key)).Should().Be(expected,
                    $"the highest-random-weight node for '{key}' must not depend on the thread culture ('{culture}')");
                WithCulture(culture, () => hash.GetNodes(key, 3)[0]).Should().Be(expected,
                    $"GetNodes must rank by the same culture-invariant score under culture '{culture}'");
            }
        }
    }

    [Fact]
    public void Partitioners_RecordKeysAndNodes_FormatUnderInvariantCulture()
    {
        // A record's synthesized ToString formats its members with the thread culture and cannot take
        // a format provider, so the canonical form must render it under the invariant culture.
        var partitioner = new HashPartitioner<HostWeight>(partitionCount: 1 << 20);
        var ring = WithCulture("en-US", () => AddAll(new ConsistentHashRing<HostWeight>(virtualNodeCount: 50),
            [new HostWeight("a", 1.5), new HostWeight("b", -2.25), new HostWeight("c", 0.125)]));
        var key = new HostWeight("k", 3.75);

        var expectedPartition = WithCulture("en-US", () => partitioner.GetPartition(key));
        foreach (var culture in s_cultures)
        {
            WithCulture(culture, () => partitioner.GetPartition(key)).Should().Be(expectedPartition,
                $"the partition of a record key must not depend on the thread culture ('{culture}')");

            var rebuilt = WithCulture(culture, () => AddAll(new ConsistentHashRing<HostWeight>(virtualNodeCount: 50),
                [new HostWeight("a", 1.5), new HostWeight("b", -2.25), new HostWeight("c", 0.125)]));
            for (var i = 0; i < 100; i++)
            {
                rebuilt.GetNode($"key-{i}").Should().Be(ring.GetNode($"key-{i}"),
                    $"a ring of record nodes built under culture '{culture}' must place them where every other host does");
            }
        }
    }

    private readonly record struct HostWeight(string Host, double Weight) : IComparable<HostWeight>
    {
        public int CompareTo(HostWeight other)
        {
            var byHost = string.CompareOrdinal(Host, other.Host);
            return byHost != 0 ? byHost : Weight.CompareTo(other.Weight);
        }
    }

    private static ConsistentHashRing<TNode> AddAll<TNode>(ConsistentHashRing<TNode> ring, TNode[] nodes)
        where TNode : notnull
    {
        foreach (var node in nodes)
        {
            ring.AddNode(node);
        }

        return ring;
    }
}
