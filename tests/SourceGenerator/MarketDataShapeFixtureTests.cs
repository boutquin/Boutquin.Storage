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
using System.Reflection;

namespace Boutquin.Storage.SourceGenerator.Tests;

// Phase 11 — fixture-test the emitter for the Tier-3 BCL types MarketData ingests:
// DateOnly (every Bar/FxRate/scalar), DateTimeOffset (instrument auctions, FX timestamps),
// decimal (with explicit [DecimalScale] for prices), and Guid (provenance / sequence-id).
//
// Each fixture (a) compiles a record holding that type, (b) round-trips a deterministic
// value through Serialize/Deserialize, and (c) where the wire shape is observable (length /
// known prefix), pins it. A regression in any one of these emit arms breaks every
// MarketData record the pipeline persists.
public sealed class MarketDataShapeFixtureTests
{
    // ───── DateOnly — used as the row key in every time-series record ─────

    [Fact]
    public void DateOnly_RoundTrip_PreservesValue()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNs
            {
                [StorageSerializable]
                public partial record struct DateBar([property: Tag(1)] System.DateOnly Date);

                public static class Factory
                {
                    public static DateBar Make() => new DateBar(new System.DateOnly(2026, 4, 25));
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var orig = asm.GetType("TestNs.Factory")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);
        var roundtrip = Deserialize(asm, "TestNs.DateBar", bytes);
        roundtrip.Should().Be(orig);
    }

    [Fact]
    public void DateOnly_DistinctValues_ProduceDistinctBytes()
    {
        // The wire format must distinguish dates by content — equal bytes implies equal date.
        var bytes2025 = SerializeWithDate(new DateOnly(2025, 1, 1));
        var bytes2026 = SerializeWithDate(new DateOnly(2026, 4, 25));
        bytes2025.Should().NotEqual(bytes2026);
    }

    [Fact]
    public void DateOnly_EqualValues_ProduceEqualBytes()
    {
        // Determinism: two records with the same DateOnly must serialise identically.
        var a = SerializeWithDate(new DateOnly(2026, 4, 25));
        var b = SerializeWithDate(new DateOnly(2026, 4, 25));
        a.Should().Equal(b, "writer must be deterministic for DateOnly");
    }

    private static byte[] SerializeWithDate(DateOnly date)
    {
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace DateNs_{{Guid.NewGuid():N}}
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateOnly D);

                public static class F
                {
                    public static R Make(int year, int month, int day) => new R(new System.DateOnly(year, month, day));
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var ns = asm.GetTypes().First(t => t.Namespace?.StartsWith("DateNs_", StringComparison.Ordinal) == true).Namespace!;
        var orig = asm.GetType($"{ns}.F")!.GetMethod("Make")!.Invoke(null, [date.Year, date.Month, date.Day])!;
        return Serialize(orig);
    }

    // ───── DateTimeOffset — instrument auctions, FX bid/offer timestamps ────

    [Fact]
    public void DateTimeOffset_RoundTrip_PreservesUtcInstantAndOffset()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace DtoNs
            {
                [StorageSerializable]
                public partial record struct Auction([property: Tag(1)] System.DateTimeOffset At);

                public static class F
                {
                    public static Auction Make() =>
                        new Auction(new System.DateTimeOffset(2026, 4, 25, 14, 30, 0, System.TimeSpan.FromHours(-4)));
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var orig = asm.GetType("DtoNs.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);
        var roundtrip = Deserialize(asm, "DtoNs.Auction", bytes);
        // Records compare DateTimeOffset by structural equality (UTC instant + offset both).
        roundtrip.Should().Be(orig);
    }

    [Fact]
    public void DateTimeOffset_SameUtcInstantDifferentOffset_NotEqual()
    {
        // 14:30 EDT (-04:00) and 18:30 UTC are the same UTC instant but different
        // DateTimeOffset structurally. The serialiser must preserve both pieces.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace DtoNs2
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateTimeOffset At);

                public static class F
                {
                    public static R EDT() => new R(new System.DateTimeOffset(2026, 4, 25, 14, 30, 0, System.TimeSpan.FromHours(-4)));
                    public static R UTC() => new R(new System.DateTimeOffset(2026, 4, 25, 18, 30, 0, System.TimeSpan.Zero));
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var edt = asm.GetType("DtoNs2.F")!.GetMethod("EDT")!.Invoke(null, null)!;
        var utc = asm.GetType("DtoNs2.F")!.GetMethod("UTC")!.Invoke(null, null)!;

        var edtBytes = Serialize(edt);
        var utcBytes = Serialize(utc);
        edtBytes.Should().NotEqual(utcBytes,
            "DateTimeOffset wire format must preserve the offset, not just the UTC instant");
    }

    // ───── decimal + [DecimalScale] — price/quantity in every market-data record ─────

    [Fact]
    public void Decimal_WithDecimalScale_RoundTripsValue()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace DecNs
            {
                [StorageSerializable]
                public partial record struct Quote([property: Tag(1)][property: DecimalScale(4)] decimal Price);

                public static class F
                {
                    public static Quote Make() => new Quote(123.4567m);
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var orig = asm.GetType("DecNs.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);
        var roundtrip = Deserialize(asm, "DecNs.Quote", bytes);
        roundtrip.Should().Be(orig);
    }

    [Fact]
    public void Decimal_WithDecimalScale_DistinctValuesProduceDistinctBytes()
    {
        // Determinism: 1.5m and 2.5m at the same declared scale must produce distinct bytes.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace DecBytes
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)][property: DecimalScale(4)] decimal V);

                public static class F
                {
                    public static R Lower() => new R(1.5m);
                    public static R Higher() => new R(2.5m);
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var lower = asm.GetType("DecBytes.F")!.GetMethod("Lower")!.Invoke(null, null)!;
        var higher = asm.GetType("DecBytes.F")!.GetMethod("Higher")!.Invoke(null, null)!;

        Serialize(lower).Should().NotEqual(Serialize(higher));

        // And equal values produce equal bytes.
        var alsoLower = asm.GetType("DecBytes.F")!.GetMethod("Lower")!.Invoke(null, null)!;
        Serialize(lower).Should().Equal(Serialize(alsoLower));
    }

    [Fact]
    public void Decimal_WithoutDecimalScale_FailsValidation()
    {
        // BSSG017 fires when [Key] decimal lacks [DecimalScale]. (StorageSerializable also
        // requires the scale per spec.)
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace DecNoScale;
            [Key]
            public partial record struct K([property: Tag(1)] decimal Price);
            """;
        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG017");
    }

    [Fact]
    public void Decimal_NegativeValue_RoundTrips()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace DecNeg
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)][property: DecimalScale(2)] decimal V);

                public static class F
                {
                    public static R Make() => new R(-123.45m);
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var orig = asm.GetType("DecNeg.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);
        var roundtrip = Deserialize(asm, "DecNeg.R", bytes);
        roundtrip.Should().Be(orig);
    }

    // ───── Guid — provenance + sequence ids ─────────────────────────────────

    [Fact]
    public void Guid_RoundTrip_PreservesValue()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace GuidNs
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.Guid Id);

                public static class F
                {
                    public static R Make() => new R(System.Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"));
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var orig = asm.GetType("GuidNs.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);
        var roundtrip = Deserialize(asm, "GuidNs.R", bytes);
        roundtrip.Should().Be(orig);
    }

    [Fact]
    public void Guid_EmptyAndNonEmpty_ProduceDistinctBytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace GuidNs2
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.Guid Id);

                public static class F
                {
                    public static R Empty() => new R(System.Guid.Empty);
                    public static R NonEmpty() => new R(System.Guid.Parse("11111111-2222-3333-4444-555555555555"));
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var empty = asm.GetType("GuidNs2.F")!.GetMethod("Empty")!.Invoke(null, null)!;
        var nonempty = asm.GetType("GuidNs2.F")!.GetMethod("NonEmpty")!.Invoke(null, null)!;
        Serialize(empty).Should().NotEqual(Serialize(nonempty));
    }

    // ───── MarketData composite shape — all Tier-3 types in one record ──────

    [Fact]
    public void MarketDataBar_CompositeShape_RoundTripsAllFields()
    {
        // Pins the canonical MarketData "Bar" shape: DateOnly + multiple decimals
        // (with [DecimalScale]) + long volume + Guid provenance.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace MDNs
            {
                [StorageSerializable]
                public partial record struct Bar(
                    [property: Tag(1)] System.DateOnly Date,
                    [property: Tag(2)][property: DecimalScale(4)] decimal Open,
                    [property: Tag(3)][property: DecimalScale(4)] decimal High,
                    [property: Tag(4)][property: DecimalScale(4)] decimal Low,
                    [property: Tag(5)][property: DecimalScale(4)] decimal Close,
                    [property: Tag(6)] long Volume,
                    [property: Tag(7)] System.Guid SourceId);

                public static class F
                {
                    public static Bar Make() => new Bar(
                        new System.DateOnly(2026, 4, 25),
                        100.1234m, 101.5000m, 99.7500m, 100.8750m,
                        1_500_000L,
                        System.Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var orig = asm.GetType("MDNs.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);
        var roundtrip = Deserialize(asm, "MDNs.Bar", bytes);
        roundtrip.Should().Be(orig);
    }

    [Fact]
    public void MarketDataBar_AddingNewTagPreservesOldReadability()
    {
        // Forward compat: a new schema adds tag 8 — old readers (without tag 8) still
        // succeed populating the original 7 fields.
        const string newSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace MDNsNew
            {
                [StorageSerializable]
                public partial record struct Bar(
                    [property: Tag(1)] System.DateOnly Date,
                    [property: Tag(2)][property: DecimalScale(4)] decimal Open,
                    [property: Tag(3)][property: DecimalScale(4)] decimal High,
                    [property: Tag(4)][property: DecimalScale(4)] decimal Low,
                    [property: Tag(5)][property: DecimalScale(4)] decimal Close,
                    [property: Tag(6)] long Volume,
                    [property: Tag(7)] System.Guid SourceId,
                    [property: Tag(8)] long TradeCount);

                public static class F
                {
                    public static Bar Make() => new Bar(
                        new System.DateOnly(2026, 4, 25),
                        100.1234m, 101.5000m, 99.7500m, 100.8750m,
                        1_500_000L,
                        System.Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                        TradeCount: 42L);
                }
            }
            """;
        var newAsm = CompileAndLoad(newSchema);
        var newValue = newAsm.GetType("MDNsNew.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(newValue);

        // Old schema = first 7 tags only.
        const string oldSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace MDNsOld;
            [StorageSerializable]
            public partial record struct Bar(
                [property: Tag(1)] System.DateOnly Date,
                [property: Tag(2)][property: DecimalScale(4)] decimal Open,
                [property: Tag(3)][property: DecimalScale(4)] decimal High,
                [property: Tag(4)][property: DecimalScale(4)] decimal Low,
                [property: Tag(5)][property: DecimalScale(4)] decimal Close,
                [property: Tag(6)] long Volume,
                [property: Tag(7)] System.Guid SourceId);
            """;
        var oldAsm = CompileAndLoad(oldSchema);
        var oldRoundtrip = Deserialize(oldAsm, "MDNsOld.Bar", bytes);

        oldRoundtrip.GetType().GetProperty("Date")!.GetValue(oldRoundtrip).Should().Be(new DateOnly(2026, 4, 25));
        oldRoundtrip.GetType().GetProperty("Volume")!.GetValue(oldRoundtrip).Should().Be(1_500_000L);
    }

    // ───── ColumnarSourceGenerator gap: [ColumnarStorable] + [StorageSerializable] together ──

    [Fact]
    public void ColumnarStorable_AndStorageSerializable_TogetherEmitsWithoutDuplicateProjectTo()
    {
        // Bars in MarketData are typically marked with both attributes — the row generator
        // emits IProjectable<TSelf>.ProjectTo, and the columnar generator must skip its own
        // emission (skipProjectable == true) to avoid CS0111 "duplicate definition".
        // Run BOTH generators in one driver to surface the duplicate-emit hazard.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace BothAttrsNs;

            [StorageSerializable]
            [ColumnarStorable]
            public partial record Bar : Boutquin.Storage.Domain.Interfaces.TimeSeries.IHasDate
            {
                public System.DateOnly Date { get; init; }
                [Tag(1)] public string Ticker { get; init; } = "";
                [Tag(2)] public long Volume { get; init; }
                public Bar() { }
            }
            """;

        var compilation = TestHelpers.CreateCompilation(source);
        var driver = CSharpGeneratorDriver.Create(
            new StorageSourceGenerator(),
            new ColumnarSourceGenerator());
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var outputCompilation, out _);
        var runResult = driver.GetRunResult();

        // No compile errors — CS0111 (duplicate definition) is the failure mode this guards.
        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Where(d => d.Id != "CS0436") // test-harness duplicate-attribute noise
            .ToList();
        errors.Should().BeEmpty(
            because: "[ColumnarStorable] + [StorageSerializable] on the same record must not emit duplicate ProjectTo; got:\n" +
                     string.Join("\n", errors.Select(d => d.ToString())));

        // Both generators must have produced their files.
        var generatedFiles = runResult.GeneratedTrees.Select(t => t.FilePath).ToList();
        generatedFiles.Should().Contain(p => p.EndsWith("Bar.Columnar.g.cs", StringComparison.Ordinal),
            "the columnar generator must emit its plan even when StorageSerializable is also present");

        // The contract is that the columnar generator's skipProjectable check fires when
        // StorageSerializable is also present, so neither file emits a duplicate. The CS0111
        // error check above already enforces this — failing it would prove the contract broken.
    }

    // ───── TaggedWireEmitter gap: enum element inside IEnumerable<T> ────────

    [Fact]
    public void EnumElement_InEnumerableT_RoundTripsThroughTaggedWire()
    {
        // MarketData uses IEnumerable<EnumType> for granularities, source tiers, etc.
        // The TaggedWireEmitter has dedicated arms for enum write (466–472) and enum read
        // (828–838) inside collections. This test exercises both via runtime round-trip.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace EnumCollNs
            {
                public enum Granularity { Daily, Hourly, Minute }

                [StorageSerializable]
                public partial record struct GranularitySet([property: Tag(1)] IEnumerable<Granularity> Values);

                public static class F
                {
                    public static GranularitySet Make() =>
                        new GranularitySet(new[] { Granularity.Daily, Granularity.Hourly, Granularity.Minute, Granularity.Daily });
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var orig = asm.GetType("EnumCollNs.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);

        var roundtrip = Deserialize(asm, "EnumCollNs.GranularitySet", bytes);

        // The Values property is IEnumerable<Granularity>; reflect to compare.
        var origValues = ((System.Collections.IEnumerable)orig.GetType().GetProperty("Values")!.GetValue(orig)!)
            .Cast<object>().ToList();
        var rtValues = ((System.Collections.IEnumerable)roundtrip.GetType().GetProperty("Values")!.GetValue(roundtrip)!)
            .Cast<object>().ToList();

        rtValues.Should().Equal(origValues,
            "enum elements inside IEnumerable<T> must round-trip with their value identity preserved");
    }

    [Theory]
    [InlineData("byte")]
    [InlineData("sbyte")]
    [InlineData("short")]
    [InlineData("ushort")]
    [InlineData("int")]
    [InlineData("uint")]
    [InlineData("long")]
    [InlineData("ulong")]
    public void EnumElement_InEnumerableT_RoundTripsForEachUnderlyingNumeric(string underlying)
    {
        // EnumUnderlyingCSharpName has eight arms — one per supported integer underlying
        // type. Each arm exercises a distinct cast in the emitted reader/writer.
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace EnumColl_{{underlying}}_Ns
            {
                public enum E : {{underlying}} { A, B, C }

                [StorageSerializable]
                public partial record struct Bag([property: Tag(1)] IEnumerable<E> Values);

                public static class F
                {
                    public static Bag Make() => new Bag(new[] { E.A, E.B, E.C, E.A });
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var ns = $"EnumColl_{underlying}_Ns";
        var orig = asm.GetType($"{ns}.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);
        var roundtrip = Deserialize(asm, $"{ns}.Bag", bytes);

        var origValues = ((System.Collections.IEnumerable)orig.GetType().GetProperty("Values")!.GetValue(orig)!)
            .Cast<object>().Select(v => v.ToString()).ToList();
        var rtValues = ((System.Collections.IEnumerable)roundtrip.GetType().GetProperty("Values")!.GetValue(roundtrip)!)
            .Cast<object>().Select(v => v.ToString()).ToList();

        rtValues.Should().Equal(origValues,
            $"enum (underlying {underlying}) inside IEnumerable<T> must round-trip");
    }

    [Fact]
    public void MarketDataBar_SchemaFormatVersionByteIsTagZero()
    {
        // Every emitted record opens with tag 0 + schema version 1, regardless of property mix.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace MDNsV
            {
                [StorageSerializable]
                public partial record struct R(
                    [property: Tag(1)] System.DateOnly D,
                    [property: Tag(2)] System.Guid G);

                public static class F
                {
                    public static R Make() => new R(new System.DateOnly(2020, 1, 1), System.Guid.Empty);
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var orig = asm.GetType("MDNsV.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);

        // Skip the length prefix varint and verify the next two bytes are tag-0 (0x00) + version 1.
        using var stream = new MemoryStream(bytes);
        _ = Boutquin.Storage.Domain.Helpers.VarintHelpers.ReadVarUInt64(stream); // length prefix
        stream.ReadByte().Should().Be(0x00); // tag 0, varint wire-type
        stream.ReadByte().Should().Be(0x01); // schema-format version 1
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Helpers — duplicated from TaggedWireTests; co-locating them here keeps each test file
    // independently runnable and avoids a cross-file refactor that's out of Phase 11 scope.
    // ═════════════════════════════════════════════════════════════════════════════════════

    private static Assembly CompileAndLoad(string source)
    {
        var asmName = "TestAsm_" + Guid.NewGuid().ToString("N");
        var compilation = TestHelpers.CreateCompilation(source, assemblyName: asmName);
        var generator = new StorageSourceGenerator();
        var driver = CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        errors.Should().BeEmpty(
            because: "generated code must compile; got:\n" + string.Join("\n", errors.Select(d => d.ToString())));

        using var ms = new MemoryStream();
        var result = outputCompilation.Emit(ms);
        result.Success.Should().BeTrue();
        ms.Position = 0;
        return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    private static byte[] Serialize(object record)
    {
        var method = record.GetType().GetMethod("Serialize", [typeof(Stream)])!;
        using var ms = new MemoryStream();
        method.Invoke(record, [ms]);
        return ms.ToArray();
    }

    private static object Deserialize(Assembly asm, string typeFqn, byte[] bytes)
    {
        var t = asm.GetType(typeFqn)!;
        var method = t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!;
        using var ms = new MemoryStream(bytes, writable: false);
        return method.Invoke(null, [ms])!;
    }
}
