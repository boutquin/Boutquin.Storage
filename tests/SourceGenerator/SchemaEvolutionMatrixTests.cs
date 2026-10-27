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
/// Phase-12 batch D2 — schema-evolution matrix. Pins the writer/reader compatibility
/// guarantees the TaggedWireEmitter must hold across MarketData lifetime: tags can be
/// added, removed, reordered, narrowed (within range), widened, made nullable, and
/// retired-via-reservation. Each test names a specific migration and asserts a specific
/// reader behaviour.
/// </summary>
public sealed class SchemaEvolutionMatrixTests
{
    // ── 1: writer adds a tag the reader doesn't know ────────────────────────

    /// <summary>
    /// Migration: writer adds tag 8 to a Bar shape; reader is on the older schema with no
    /// tag 8. Reader must surface every old field unchanged — the AppendReadAnnotated
    /// SkipField arm fires for the unknown tag.
    /// </summary>
    [Fact]
    public void Migration_AddTag8_OldReader_PreservesOldFields()
    {
        const string newSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_AddTag8_New
            {
                [StorageSerializable]
                public partial record struct Bar(
                    [property: Tag(1)] long A,
                    [property: Tag(2)] long B,
                    [property: Tag(8)] long C);
                public static class F { public static Bar Make() => new Bar(11L, 22L, 88L); }
            }
            """;
        var newAsm = CompileAndLoad(newSchema);
        var bytes = Serialize(newAsm.GetType("D2_AddTag8_New.F")!.GetMethod("Make")!.Invoke(null, null)!);

        const string oldSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_AddTag8_Old
            {
                [StorageSerializable]
                public partial record struct Bar(
                    [property: Tag(1)] long A,
                    [property: Tag(2)] long B);
            }
            """;
        var oldAsm = CompileAndLoad(oldSchema);
        var roundtrip = Deserialize(oldAsm, "D2_AddTag8_Old.Bar", bytes);

        roundtrip.GetType().GetProperty("A")!.GetValue(roundtrip).Should().Be(11L);
        roundtrip.GetType().GetProperty("B")!.GetValue(roundtrip).Should().Be(22L);
    }

    // ── 2: reader's tag 5 not in writer's frame ─────────────────────────────

    /// <summary>
    /// Migration: writer's schema omits tag 5 (deletes a field). Reader's newer schema
    /// declares tag 5. The reader's tag 5 field surfaces at <c>default(T)</c>.
    /// </summary>
    [Fact]
    public void Migration_RemoveTag5_NewReader_GetsDefault()
    {
        const string oldSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_Rem5_Old
            {
                [StorageSerializable]
                public partial record struct Bar([property: Tag(1)] long A);
                public static class F { public static Bar Make() => new Bar(99L); }
            }
            """;
        var oldAsm = CompileAndLoad(oldSchema);
        var bytes = Serialize(oldAsm.GetType("D2_Rem5_Old.F")!.GetMethod("Make")!.Invoke(null, null)!);

        const string newSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_Rem5_New
            {
                [StorageSerializable]
                public partial record struct Bar(
                    [property: Tag(1)] long A,
                    [property: Tag(5)] long Removed);
            }
            """;
        var newAsm = CompileAndLoad(newSchema);
        var roundtrip = Deserialize(newAsm, "D2_Rem5_New.Bar", bytes);

        roundtrip.GetType().GetProperty("A")!.GetValue(roundtrip).Should().Be(99L);
        roundtrip.GetType().GetProperty("Removed")!.GetValue(roundtrip).Should().Be(0L,
            "tag 5 was absent from the writer's frame; reader's field surfaces at default(long)");
    }

    // ── 3: declaration order changes; tag values stable ─────────────────────

    /// <summary>
    /// Migration: a refactor reorders the property list but keeps tag values. The on-disk
    /// bytes follow declaration order (the emitter writes properties in source order),
    /// so the wire bytes differ — BUT the reader is tag-keyed, so each scheme's reader
    /// can decode the other's bytes and surface the same logical values. The contract:
    /// reordering is observable on the wire but transparent to readers.
    /// </summary>
    [Fact]
    public void Migration_ReorderProperties_ReadersAreTagKeyed()
    {
        const string scheme1 = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_Reorder_S1
            {
                [StorageSerializable]
                public partial record struct R(
                    [property: Tag(1)] long A,
                    [property: Tag(2)] long B,
                    [property: Tag(3)] long C);
                public static class F { public static R Make() => new R(1, 2, 3); }
            }
            """;
        const string scheme2 = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_Reorder_S2
            {
                [StorageSerializable]
                public partial record struct R(
                    [property: Tag(3)] long C,
                    [property: Tag(1)] long A,
                    [property: Tag(2)] long B);
                public static class F { public static R Make() => new R(3, 1, 2); }
            }
            """;
        var asm1 = CompileAndLoad(scheme1);
        var asm2 = CompileAndLoad(scheme2);

        var bytes1 = Serialize(asm1.GetType("D2_Reorder_S1.F")!.GetMethod("Make")!.Invoke(null, null)!);
        var bytes2 = Serialize(asm2.GetType("D2_Reorder_S2.F")!.GetMethod("Make")!.Invoke(null, null)!);

        // On-disk bytes differ because the emitter walks the properties in declaration order.
        bytes2.Should().NotEqual(bytes1,
            "the emitter writes properties in declaration order, so reordering is observable on disk");

        // Cross-decode: scheme2's reader decodes scheme1's bytes correctly, and vice versa,
        // because the reader is tag-keyed via switch.
        var s1_via_s2 = Deserialize(asm2, "D2_Reorder_S2.R", bytes1);
        var s2_via_s1 = Deserialize(asm1, "D2_Reorder_S1.R", bytes2);

        s1_via_s2.GetType().GetProperty("A")!.GetValue(s1_via_s2).Should().Be(1L);
        s1_via_s2.GetType().GetProperty("B")!.GetValue(s1_via_s2).Should().Be(2L);
        s1_via_s2.GetType().GetProperty("C")!.GetValue(s1_via_s2).Should().Be(3L);

        s2_via_s1.GetType().GetProperty("A")!.GetValue(s2_via_s1).Should().Be(1L);
        s2_via_s1.GetType().GetProperty("B")!.GetValue(s2_via_s1).Should().Be(2L);
        s2_via_s1.GetType().GetProperty("C")!.GetValue(s2_via_s1).Should().Be(3L);
    }

    // ── 4: long → int narrowing, value in range ─────────────────────────────

    /// <summary>
    /// Migration: writer declares <c>long</c>, reader narrows to <c>int</c>; the writer's
    /// value fits in the int range. The reader uses <c>checked()</c> conversion which
    /// succeeds without overflow.
    /// </summary>
    [Fact]
    public void Migration_LongToInt_InRange_NarrowsCorrectly()
    {
        const string wide = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_Narrow_Wide
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] long V);
                public static class F { public static R Make() => new R(1_234_567L); }
            }
            """;
        var wideAsm = CompileAndLoad(wide);
        var bytes = Serialize(wideAsm.GetType("D2_Narrow_Wide.F")!.GetMethod("Make")!.Invoke(null, null)!);

        const string narrow = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_Narrow_Narrow
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] int V);
            }
            """;
        var narrowAsm = CompileAndLoad(narrow);
        var roundtrip = Deserialize(narrowAsm, "D2_Narrow_Narrow.R", bytes);

        roundtrip.GetType().GetProperty("V")!.GetValue(roundtrip).Should().Be(1_234_567,
            "checked-narrowing succeeds when the writer's value fits the reader's narrower type");
    }

    // ── 5: long → int narrowing, overflow ───────────────────────────────────

    /// <summary>
    /// Migration: writer declares <c>long</c>, reader narrows to <c>int</c>; the writer's
    /// value EXCEEDS int.MaxValue. The reader's <c>checked()</c> conversion must surface
    /// <see cref="OverflowException"/>, not silently truncate.
    /// </summary>
    [Fact]
    public void Migration_LongToInt_Overflow_Throws()
    {
        const string wide = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_Overflow_Wide
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] long V);
                public static class F { public static R Make() => new R(long.MaxValue); }
            }
            """;
        var wideAsm = CompileAndLoad(wide);
        var bytes = Serialize(wideAsm.GetType("D2_Overflow_Wide.F")!.GetMethod("Make")!.Invoke(null, null)!);

        const string narrow = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_Overflow_Narrow
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] int V);
            }
            """;
        var narrowAsm = CompileAndLoad(narrow);

        Action act = () => Deserialize(narrowAsm, "D2_Overflow_Narrow.R", bytes);
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<OverflowException>(
                "narrowing must surface OverflowException when the writer's value cannot fit");
    }

    // ── 6: DateOnly → Nullable<DateOnly>, value present ────────────────────

    /// <summary>
    /// Migration: writer declares <c>DateOnly</c>, reader widens to <c>DateOnly?</c>.
    /// The reader's nullable inner-read populates <c>HasValue == true</c>.
    /// </summary>
    [Fact]
    public void Migration_DateOnlyToNullable_NonNull_Reads()
    {
        const string oldS = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_DOToNull_Old
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateOnly D);
                public static class F { public static R Make() => new R(new System.DateOnly(2026, 4, 25)); }
            }
            """;
        var oldAsm = CompileAndLoad(oldS);
        var bytes = Serialize(oldAsm.GetType("D2_DOToNull_Old.F")!.GetMethod("Make")!.Invoke(null, null)!);

        const string newS = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_DOToNull_New
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateOnly? D);
            }
            """;
        var newAsm = CompileAndLoad(newS);
        var roundtrip = Deserialize(newAsm, "D2_DOToNull_New.R", bytes);

        var d = (DateOnly?)roundtrip.GetType().GetProperty("D")!.GetValue(roundtrip);
        d.Should().Be(new DateOnly(2026, 4, 25),
            "wider Nullable<DateOnly> reader must accept the narrower writer's tag and surface the value");
    }

    // ── 7: Nullable<DateOnly> with null → DateOnly ─────────────────────────

    /// <summary>
    /// Migration: writer declares <c>DateOnly?</c> and emits null (omits the tag); reader
    /// has narrowed to non-nullable <c>DateOnly</c>. The reader's field surfaces at
    /// <c>default(DateOnly)</c> via the missing-tag path.
    /// </summary>
    [Fact]
    public void Migration_NullableToDateOnly_NullWriter_DefaultReader()
    {
        const string oldS = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_NullToDO_Old
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateOnly? D);
                public static class F { public static R Make() => new R((System.DateOnly?)null); }
            }
            """;
        var oldAsm = CompileAndLoad(oldS);
        var bytes = Serialize(oldAsm.GetType("D2_NullToDO_Old.F")!.GetMethod("Make")!.Invoke(null, null)!);

        const string newS = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_NullToDO_New
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateOnly D);
            }
            """;
        var newAsm = CompileAndLoad(newS);
        var roundtrip = Deserialize(newAsm, "D2_NullToDO_New.R", bytes);

        var d = (DateOnly)roundtrip.GetType().GetProperty("D")!.GetValue(roundtrip)!;
        d.Should().Be(default,
            "writer omitted the tag (null nullable); narrowed reader's field defaults");
    }

    // ── 8: DecimalScale increase preserves logical value ───────────────────

    /// <summary>
    /// Migration: writer uses <c>[DecimalScale(2)]</c>; reader uses <c>[DecimalScale(4)]</c>.
    /// The Fixed128 wire format carries the canonical .NET <c>decimal</c> bits — including
    /// the scale field embedded in the value — so the reader reconstructs the same logical
    /// value regardless of its own DecimalScale annotation. The reader's annotation governs
    /// what IT writes, not what it reads: scale-widening is a transparent migration on read.
    /// </summary>
    [Fact]
    public void Migration_DecimalScale_Increase_PreservesValue()
    {
        const string scale2 = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_Scale2
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)][property: DecimalScale(2)] decimal V);
                public static class F { public static R Make() => new R(1.23m); }
            }
            """;
        var scale2Asm = CompileAndLoad(scale2);
        var bytes = Serialize(scale2Asm.GetType("D2_Scale2.F")!.GetMethod("Make")!.Invoke(null, null)!);

        const string scale4 = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_Scale4
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)][property: DecimalScale(4)] decimal V);
            }
            """;
        var scale4Asm = CompileAndLoad(scale4);
        var roundtrip = Deserialize(scale4Asm, "D2_Scale4.R", bytes);

        var actual = (decimal)roundtrip.GetType().GetProperty("V")!.GetValue(roundtrip)!;
        actual.Should().Be(1.23m,
            "Fixed128 carries the .NET decimal bits including its embedded scale — the reader's DecimalScale annotation does not reinterpret the value");
    }

    // ── 9: nested record evolves while outer collection reads forward ──────

    /// <summary>
    /// Migration: the inner record (used inside an <c>IEnumerable&lt;Inner&gt;</c>) gains a
    /// new tag. The outer reader stays on the old inner schema. Each inner element's
    /// unknown tag is skipped via the inner record's own SkipField arm — the outer
    /// collection iteration is unaffected.
    /// </summary>
    [Fact]
    public void Migration_NestedRecord_InnerEvolves_OuterStillReads()
    {
        const string newInnerSrc = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace D2_NestedNew
            {
                [StorageSerializable]
                public partial record struct Inner(
                    [property: Tag(1)] long V,
                    [property: Tag(2)] long Extra);

                [StorageSerializable]
                public partial record struct Outer([property: Tag(1)] IEnumerable<Inner> Items);

                public static class F
                {
                    public static Outer Make() => new Outer(new[] { new Inner(10, 100), new Inner(20, 200) });
                }
            }
            """;
        var newAsm = CompileAndLoad(newInnerSrc);
        var bytes = Serialize(newAsm.GetType("D2_NestedNew.F")!.GetMethod("Make")!.Invoke(null, null)!);

        const string oldInnerSrc = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace D2_NestedOld
            {
                [StorageSerializable]
                public partial record struct Inner([property: Tag(1)] long V);

                [StorageSerializable]
                public partial record struct Outer([property: Tag(1)] IEnumerable<Inner> Items);
            }
            """;
        var oldAsm = CompileAndLoad(oldInnerSrc);
        var roundtrip = Deserialize(oldAsm, "D2_NestedOld.Outer", bytes);

        var items = ((System.Collections.IEnumerable)roundtrip.GetType().GetProperty("Items")!.GetValue(roundtrip)!)
            .Cast<object>().ToList();
        items.Should().HaveCount(2,
            "outer collection iteration must complete even when each inner record carries an unknown tag");
        items.Select(i => (long)i.GetType().GetProperty("V")!.GetValue(i)!)
            .Should().Equal([10L, 20L],
                "the known tag (V) must surface on every inner element");
    }

    // ── 10: writer's tag ends up on reader's [ReservedTag] slot ────────────

    /// <summary>
    /// Migration: the reader retired tag N via <c>[ReservedTag(N)]</c>; old data still
    /// carries that tag. The reader must skip it silently — no diagnostic, just SkipField.
    /// </summary>
    [Fact]
    public void Migration_ReservedTag_RetiredTagInOldData_SkippedNoDiagnostic()
    {
        const string oldS = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_Retired_Old
            {
                [StorageSerializable]
                public partial record struct R(
                    [property: Tag(1)] long A,
                    [property: Tag(7)] long Doomed);
                public static class F { public static R Make() => new R(11L, 77L); }
            }
            """;
        var oldAsm = CompileAndLoad(oldS);
        var bytes = Serialize(oldAsm.GetType("D2_Retired_Old.F")!.GetMethod("Make")!.Invoke(null, null)!);

        // Reader retires tag 7. Tag 1 stays. No tag may collide with the reservation.
        const string newS = """
            using Boutquin.Storage.Domain.Attributes;
            namespace D2_Retired_New
            {
                [StorageSerializable]
                [ReservedTag(7)]
                public partial record struct R([property: Tag(1)] long A);
            }
            """;
        var newAsm = CompileAndLoad(newS);
        var roundtrip = Deserialize(newAsm, "D2_Retired_New.R", bytes);

        roundtrip.GetType().GetProperty("A")!.GetValue(roundtrip).Should().Be(11L,
            "tag 1 (live) surfaces; tag 7 (retired via [ReservedTag]) is silently skipped");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Helpers
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
        errors.Should().BeEmpty(because: "generated code must compile; got:\n" +
            string.Join("\n", errors.Select(d => d.ToString())));

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
