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

/// <summary>
/// Phase-12 batch C — TaggedWireEmitter MarketData-reachable remainders. Pins the wire
/// behaviour for nullable primitives that MarketData uses on every Bar shape (DateOnly,
/// decimal+DecimalScale, Guid), the IEnumerable&lt;T&gt; arms over nested records and
/// LengthPrefixed primitives (DateTimeOffset), and the three concrete <c>SkipField</c>
/// arms exercised when an old reader meets a new writer that added a tag of each wire
/// type (Varint, Fixed64, LengthPrefixed).
/// </summary>
public sealed class Phase12TaggedWireRemaindersTests
{
    // ── 1 & 2: Nullable<DateOnly> — null and non-null arms ──────────────────

    [Fact]
    public void Nullable_DateOnly_NullValue_RoundTrips()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace NullableDateOnlyNull
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateOnly? D);

                public static class F
                {
                    public static R Make() => new R((System.DateOnly?)null);
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var orig = asm.GetType("NullableDateOnlyNull.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);
        var roundtrip = Deserialize(asm, "NullableDateOnlyNull.R", bytes);
        roundtrip.Should().Be(orig);

        var dProp = roundtrip.GetType().GetProperty("D")!;
        dProp.GetValue(roundtrip).Should().BeNull(
            "Nullable<T> with HasValue == false must round-trip as null — the writer simply omits the tag");
    }

    [Fact]
    public void Nullable_DateOnly_NonNull_RoundTrips()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace NullableDateOnlyNonNull
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateOnly? D);

                public static class F
                {
                    public static R Make() => new R(new System.DateOnly(2026, 4, 25));
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var orig = asm.GetType("NullableDateOnlyNonNull.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);
        var roundtrip = Deserialize(asm, "NullableDateOnlyNonNull.R", bytes);
        roundtrip.Should().Be(orig);

        var dValue = roundtrip.GetType().GetProperty("D")!.GetValue(roundtrip);
        dValue.Should().Be(new DateOnly(2026, 4, 25));
    }

    // ── 3: Nullable<decimal> with [DecimalScale] — both null and non-null ──

    [Fact]
    public void Nullable_Decimal_WithDecimalScale_RoundTrips_Null()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace NullableDecimalNull
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)][property: DecimalScale(4)] decimal? V);

                public static class F
                {
                    public static R Make() => new R((decimal?)null);
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var orig = asm.GetType("NullableDecimalNull.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);
        var roundtrip = Deserialize(asm, "NullableDecimalNull.R", bytes);
        roundtrip.Should().Be(orig);

        roundtrip.GetType().GetProperty("V")!.GetValue(roundtrip).Should().BeNull();
    }

    [Fact]
    public void Nullable_Decimal_WithDecimalScale_RoundTrips_NonNull()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace NullableDecimalNonNull
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)][property: DecimalScale(4)] decimal? V);

                public static class F
                {
                    public static R Make() => new R(123.4567m);
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var orig = asm.GetType("NullableDecimalNonNull.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);
        var roundtrip = Deserialize(asm, "NullableDecimalNonNull.R", bytes);
        roundtrip.Should().Be(orig);

        roundtrip.GetType().GetProperty("V")!.GetValue(roundtrip).Should().Be(123.4567m);
    }

    // ── 4: Nullable<Guid> ───────────────────────────────────────────────────

    [Fact]
    public void Nullable_Guid_RoundTrips()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace NullableGuidRoundtrip
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.Guid? G);

                public static class F
                {
                    public static R MakePresent() => new R(new System.Guid("12345678-1234-5678-1234-567812345678"));
                    public static R MakeAbsent() => new R((System.Guid?)null);
                }
            }
            """;
        var asm = CompileAndLoad(source);

        var present = asm.GetType("NullableGuidRoundtrip.F")!.GetMethod("MakePresent")!.Invoke(null, null)!;
        var absent = asm.GetType("NullableGuidRoundtrip.F")!.GetMethod("MakeAbsent")!.Invoke(null, null)!;

        Deserialize(asm, "NullableGuidRoundtrip.R", Serialize(present)).Should().Be(present);
        Deserialize(asm, "NullableGuidRoundtrip.R", Serialize(absent)).Should().Be(absent);
    }

    // ── 5 & 6: IEnumerable<NestedRecord> — non-empty and empty ─────────────

    [Fact]
    public void IEnumerable_OfNestedRecord_RoundTrips()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace NestedRecordCollection
            {
                [StorageSerializable]
                public partial record struct Inner([property: Tag(1)] long V);

                [StorageSerializable]
                public partial record struct Outer([property: Tag(1)] IEnumerable<Inner> Items);

                public static class F
                {
                    public static Outer Make() =>
                        new Outer(new[] { new Inner(10L), new Inner(20L), new Inner(30L) });
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var orig = asm.GetType("NestedRecordCollection.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);
        var roundtrip = Deserialize(asm, "NestedRecordCollection.Outer", bytes);

        var origItems = ((System.Collections.IEnumerable)orig.GetType().GetProperty("Items")!.GetValue(orig)!)
            .Cast<object>().ToList();
        var rtItems = ((System.Collections.IEnumerable)roundtrip.GetType().GetProperty("Items")!.GetValue(roundtrip)!)
            .Cast<object>().ToList();
        rtItems.Should().Equal(origItems,
            "IEnumerable<NestedRecord> takes the AppendReadAnnotatedCollection path — count + per-element Deserialize");
    }

    [Fact]
    public void IEnumerable_OfNestedRecord_Empty_RoundTrips()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace NestedRecordCollectionEmpty
            {
                [StorageSerializable]
                public partial record struct Inner([property: Tag(1)] long V);

                [StorageSerializable]
                public partial record struct Outer([property: Tag(1)] IEnumerable<Inner> Items);

                public static class F
                {
                    public static Outer Make() => new Outer(System.Array.Empty<Inner>());
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var orig = asm.GetType("NestedRecordCollectionEmpty.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);
        var roundtrip = Deserialize(asm, "NestedRecordCollectionEmpty.Outer", bytes);

        var rtItems = ((System.Collections.IEnumerable)roundtrip.GetType().GetProperty("Items")!.GetValue(roundtrip)!)
            .Cast<object>().ToList();
        rtItems.Should().BeEmpty(
            "the count == 0 arm of AppendReadAnnotatedCollection must produce an empty list, not a NRE on the loop body");
    }

    // ── 7: IEnumerable<DateTimeOffset> — LengthPrefixed element wire type ──

    [Fact]
    public void IEnumerable_OfDateTimeOffset_RoundTrips()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace DateTimeOffsetCollection
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] IEnumerable<System.DateTimeOffset> Stamps);

                public static class F
                {
                    public static R Make() => new R(new[]
                    {
                        new System.DateTimeOffset(2026, 1, 1, 9, 30, 0, System.TimeSpan.Zero),
                        new System.DateTimeOffset(2026, 1, 2, 16, 0, 0, System.TimeSpan.FromHours(-5)),
                        new System.DateTimeOffset(2026, 1, 3, 8, 15, 30, System.TimeSpan.FromHours(2)),
                    });
                }
            }
            """;
        var asm = CompileAndLoad(source);
        var orig = asm.GetType("DateTimeOffsetCollection.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(orig);
        var roundtrip = Deserialize(asm, "DateTimeOffsetCollection.R", bytes);

        var origStamps = ((System.Collections.IEnumerable)orig.GetType().GetProperty("Stamps")!.GetValue(orig)!)
            .Cast<object>().ToList();
        var rtStamps = ((System.Collections.IEnumerable)roundtrip.GetType().GetProperty("Stamps")!.GetValue(roundtrip)!)
            .Cast<object>().ToList();
        rtStamps.Should().Equal(origStamps,
            "LengthPrefixed element wire type goes through the count-prefixed reader; UTC instants and offsets must both survive");
    }

    // ── 8, 9, 10: SkipField arms — old reader vs new writer ────────────────

    /// <summary>
    /// New writer adds a tag with <see cref="Boutquin.Storage.Domain.Helpers.WireType.Varint"/>
    /// payload (any integer/DateOnly/TimeOnly/TimeSpan kind). Old reader's switch has no case
    /// for that tag and falls through to the <c>SkipField(WireType.Varint)</c> arm — the
    /// old reader must surface its declared field unchanged.
    /// </summary>
    [Fact]
    public void OldReader_SkipsUnknownVarintTag()
    {
        // New writer emits tag 1 (Varint) AND tag 2 (Varint).
        const string newSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace OldSkipVarint_New
            {
                [StorageSerializable]
                public partial record struct R(
                    [property: Tag(1)] long A,
                    [property: Tag(2)] long B);

                public static class F
                {
                    public static R Make() => new R(11L, 22L);
                }
            }
            """;
        var newAsm = CompileAndLoad(newSchema);
        var newValue = newAsm.GetType("OldSkipVarint_New.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(newValue);

        // Old reader knows tag 1 only — must skip the unknown Varint at tag 2.
        const string oldSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace OldSkipVarint_Old
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] long A);
            }
            """;
        var oldAsm = CompileAndLoad(oldSchema);
        var roundtrip = Deserialize(oldAsm, "OldSkipVarint_Old.R", bytes);

        roundtrip.GetType().GetProperty("A")!.GetValue(roundtrip).Should().Be(11L,
            "tag 1 (known) must surface unchanged after skipping the unknown Varint-wire tag 2");
    }

    /// <summary>
    /// New writer adds a tag with <see cref="Boutquin.Storage.Domain.Helpers.WireType.Fixed64"/>
    /// payload (double). Old reader skips the 8-byte payload via the <c>SkipField(Fixed64)</c>
    /// arm and surfaces its known field.
    /// </summary>
    [Fact]
    public void OldReader_SkipsUnknownFixed64Tag()
    {
        const string newSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace OldSkipFixed64_New
            {
                [StorageSerializable]
                public partial record struct R(
                    [property: Tag(1)] long A,
                    [property: Tag(2)] double B);

                public static class F
                {
                    public static R Make() => new R(11L, 3.14159);
                }
            }
            """;
        var newAsm = CompileAndLoad(newSchema);
        var newValue = newAsm.GetType("OldSkipFixed64_New.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(newValue);

        const string oldSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace OldSkipFixed64_Old
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] long A);
            }
            """;
        var oldAsm = CompileAndLoad(oldSchema);
        var roundtrip = Deserialize(oldAsm, "OldSkipFixed64_Old.R", bytes);

        roundtrip.GetType().GetProperty("A")!.GetValue(roundtrip).Should().Be(11L,
            "tag 1 (known) must surface unchanged after skipping the unknown Fixed64-wire tag 2");
    }

    /// <summary>
    /// New writer adds a tag with <see cref="Boutquin.Storage.Domain.Helpers.WireType.LengthPrefixed"/>
    /// payload (string). Old reader skips the varint-length + bytes via the
    /// <c>SkipField(LengthPrefixed)</c> arm and surfaces its known field.
    /// </summary>
    [Fact]
    public void OldReader_SkipsUnknownLengthPrefixedTag()
    {
        const string newSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace OldSkipLP_New
            {
                [StorageSerializable]
                public partial record struct R(
                    [property: Tag(1)] long A,
                    [property: Tag(2)] string B);

                public static class F
                {
                    public static R Make() => new R(11L, "Phase12");
                }
            }
            """;
        var newAsm = CompileAndLoad(newSchema);
        var newValue = newAsm.GetType("OldSkipLP_New.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(newValue);

        const string oldSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace OldSkipLP_Old
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] long A);
            }
            """;
        var oldAsm = CompileAndLoad(oldSchema);
        var roundtrip = Deserialize(oldAsm, "OldSkipLP_Old.R", bytes);

        roundtrip.GetType().GetProperty("A")!.GetValue(roundtrip).Should().Be(11L,
            "tag 1 (known) must surface unchanged after skipping the unknown LengthPrefixed-wire tag 2");
    }

    /// <summary>
    /// New writer adds tags with <c>Fixed16</c> (Half), <c>Fixed32</c> (float), and
    /// <c>Fixed128</c> (Guid) payloads — the three fixed-width <c>SkipField</c> arms the
    /// Varint/Fixed64/LengthPrefixed tests above don't reach. The old reader must advance
    /// exactly 2/4/16 bytes per unknown tag and surface its known field unchanged.
    /// </summary>
    [Fact]
    public void OldReader_SkipsUnknownFixed16Fixed32Fixed128Tags()
    {
        const string newSchema = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace OldSkipFixedWidths_New
            {
                [StorageSerializable]
                public partial record struct R(
                    [property: Tag(1)] long A,
                    [property: Tag(2)] Half H,
                    [property: Tag(3)] float F,
                    [property: Tag(4)] Guid G);

                public static class F
                {
                    public static R Make() => new R(11L, (Half)1.5, 2.5f, Guid.Parse("11111111-2222-3333-4444-555555555555"));
                }
            }
            """;
        var newAsm = CompileAndLoad(newSchema);
        var newValue = newAsm.GetType("OldSkipFixedWidths_New.F")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(newValue);

        const string oldSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace OldSkipFixedWidths_Old
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] long A);
            }
            """;
        var oldAsm = CompileAndLoad(oldSchema);
        var roundtrip = Deserialize(oldAsm, "OldSkipFixedWidths_Old.R", bytes);

        roundtrip.GetType().GetProperty("A")!.GetValue(roundtrip).Should().Be(11L,
            "tag 1 (known) must surface unchanged after skipping the unknown Fixed16, Fixed32, and Fixed128 tags");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Helpers — same compile-and-load pattern as TaggedWireTests / MarketDataShapeFixtureTests.
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
