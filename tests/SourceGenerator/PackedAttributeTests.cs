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

// Phase F2 — [Packed] / [Unpacked] collection-encoding tests. Exercises both writer dispatch
// (wire-byte-size assertion and cross-format round-trip) and reader dispatch (read a packed
// stream on a record that declared [Unpacked], and vice versa). Default behaviour in v2.0.0:
// packed-on for Tier-1 fixed-width numerics and bool; count-prefixed for other element kinds.
public sealed class PackedAttributeTests
{
    // Default-packed contract: `IEnumerable<int>` with length ≥ 8 must produce a strictly
    // smaller wire than the same property when declared [Unpacked]. The ~15% savings figure
    // in the packed-default rule depends on the per-element tag-varint being dropped; a short collection is
    // enough to demonstrate the shape, but length 16 amplifies the gap so the assertion holds
    // under every CI noise profile.
    [Fact]
    public void Default_Packed_Long_Collection_Is_Smaller_Than_Unpacked()
    {
        const string packedSource = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            using System.Linq;
            namespace PackedNs
            {
                [StorageSerializable]
                public partial record struct Bag([property: Tag(1)] IEnumerable<int> Values);

                public static class BagFactory
                {
                    public static Bag Make() => new Bag(Enumerable.Range(1, 16).ToArray());
                }
            }
            """;

        const string unpackedSource = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            using System.Linq;
            namespace UnpackedNs
            {
                [StorageSerializable]
                public partial record struct Bag([property: Tag(1), Unpacked] IEnumerable<int> Values);

                public static class BagFactory
                {
                    public static Bag Make() => new Bag(Enumerable.Range(1, 16).ToArray());
                }
            }
            """;

        var packedAsm = CompileAndLoad(packedSource);
        var unpackedAsm = CompileAndLoad(unpackedSource);

        var packedVal = packedAsm.GetType("PackedNs.BagFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var unpackedVal = unpackedAsm.GetType("UnpackedNs.BagFactory")!.GetMethod("Make")!.Invoke(null, null)!;

        var packedBytes = Serialize(packedVal);
        var unpackedBytes = Serialize(unpackedVal);

        packedBytes.Length.Should().BeLessThan(unpackedBytes.Length,
            "packed should drop per-element tag-varints and thus compact smaller than unpacked for long fixed-width primitive collections");
    }

    // Round-trip under the v2.0.0 default (packed) for every Tier-1 fixed-width element kind
    // that F2 covers. If any kind's write/read paths are asymmetric the value comparison fails.
    [Theory]
    [InlineData("int", "new int[] { 1, 2, 3, 100000, -1 }")]
    [InlineData("long", "new long[] { 1L, 2L, 3L, 100000L, -1L }")]
    [InlineData("uint", "new uint[] { 1U, 2U, 3U, 100000U }")]
    [InlineData("ulong", "new ulong[] { 1UL, 2UL, 3UL, 18446744073709551615UL }")]
    [InlineData("short", "new short[] { 1, -2, 3, -30000 }")]
    [InlineData("ushort", "new ushort[] { 1, 2, 3, 65535 }")]
    [InlineData("byte", "new byte[] { 0, 127, 255 }")]
    [InlineData("sbyte", "new sbyte[] { -128, -1, 0, 127 }")]
    [InlineData("bool", "new bool[] { true, false, true, true, false }")]
    [InlineData("float", "new float[] { 1.0f, -2.5f, 3.14159f }")]
    [InlineData("double", "new double[] { 1.0, -2.5, 3.14159 }")]
    [InlineData("System.Half", "new System.Half[] { (System.Half)1.0, (System.Half)(-2.5), (System.Half)3.5 }")]
    public void Default_Packed_Round_Trips_Every_Tier1_Fixed_Element_Kind(string clrType, string arrayLiteral)
    {
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNs
            {
                [StorageSerializable]
                public partial record struct Bag([property: Tag(1)] IEnumerable<{{clrType}}> Values);

                public static class BagFactory
                {
                    public static Bag Make() => new Bag({{arrayLiteral}});
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNs.BagFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(original);
        var roundtrip = Deserialize(asm, "TestNs.Bag", bytes);

        var originalValues = (System.Collections.IEnumerable)original.GetType().GetProperty("Values")!.GetValue(original)!;
        var roundtripValues = (System.Collections.IEnumerable)roundtrip.GetType().GetProperty("Values")!.GetValue(roundtrip)!;

        roundtripValues.Cast<object>().Should().Equal(originalValues.Cast<object>());
    }

    // [Unpacked] opt-out still round-trips. Important because an unpacked stream has a very
    // different shape (no outer LEN block, repeated tag per element).
    [Fact]
    public void Unpacked_Opt_Out_Round_Trips()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNs
            {
                [StorageSerializable]
                public partial record struct Bag([property: Tag(1), Unpacked] IEnumerable<long> Values);

                public static class BagFactory
                {
                    public static Bag Make() => new Bag(new long[] { 1, 2, 3, 100000, -1, 0 });
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNs.BagFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(original);
        var roundtrip = Deserialize(asm, "TestNs.Bag", bytes);

        var originalValues = (IEnumerable<long>)original.GetType().GetProperty("Values")!.GetValue(original)!;
        var roundtripValues = (IEnumerable<long>)roundtrip.GetType().GetProperty("Values")!.GetValue(roundtrip)!;

        roundtripValues.Should().Equal(originalValues);
    }

    // Cross-format reader tolerance (the reader dispatches on the first tag-varint's
    // wire-type bit; both encodings are accepted for the same property on a single read):
    // write with [Unpacked] on one side, read with a default-packed schema on the other, and
    // still recover every element in order.
    [Fact]
    public void Reader_Accepts_Unpacked_Bytes_Even_When_Schema_Is_Packed()
    {
        const string unpackedWriter = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace WriterNs
            {
                [StorageSerializable]
                public partial record struct Bag([property: Tag(1), Unpacked] IEnumerable<long> Values);

                public static class BagFactory
                {
                    public static Bag Make() => new Bag(new long[] { 10, 20, 30, 40 });
                }
            }
            """;

        const string packedReader = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace ReaderNs
            {
                [StorageSerializable]
                public partial record struct Bag([property: Tag(1)] IEnumerable<long> Values);
            }
            """;

        var writerAsm = CompileAndLoad(unpackedWriter);
        var readerAsm = CompileAndLoad(packedReader);

        var original = writerAsm.GetType("WriterNs.BagFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(original);
        var roundtrip = Deserialize(readerAsm, "ReaderNs.Bag", bytes);

        var roundtripValues = (IEnumerable<long>)roundtrip.GetType().GetProperty("Values")!.GetValue(roundtrip)!;
        roundtripValues.Should().Equal([10, 20, 30, 40]);
    }

    // Same, reversed direction — a packed stream consumed by a reader whose schema declared
    // [Unpacked]. The count-less LengthPrefixed block must still decode to the same list.
    [Fact]
    public void Reader_Accepts_Packed_Bytes_Even_When_Schema_Is_Unpacked()
    {
        const string packedWriter = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace WriterNs
            {
                [StorageSerializable]
                public partial record struct Bag([property: Tag(1)] IEnumerable<long> Values);

                public static class BagFactory
                {
                    public static Bag Make() => new Bag(new long[] { 10, 20, 30, 40 });
                }
            }
            """;

        const string unpackedReader = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace ReaderNs
            {
                [StorageSerializable]
                public partial record struct Bag([property: Tag(1), Unpacked] IEnumerable<long> Values);
            }
            """;

        var writerAsm = CompileAndLoad(packedWriter);
        var readerAsm = CompileAndLoad(unpackedReader);

        var original = writerAsm.GetType("WriterNs.BagFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(original);
        var roundtrip = Deserialize(readerAsm, "ReaderNs.Bag", bytes);

        var roundtripValues = (IEnumerable<long>)roundtrip.GetType().GetProperty("Values")!.GetValue(roundtrip)!;
        roundtripValues.Should().Equal([10, 20, 30, 40]);
    }

    // [Packed] explicit-intent on a default-packed property must remain a no-op — the emitted
    // wire is byte-identical to the same property without the attribute. Locks the "no-op on
    // already-packed" contract so a future element-set expansion doesn't silently change
    // semantics.
    [Fact]
    public void Explicit_Packed_Is_No_Op_On_Default_Packed_Property()
    {
        const string defaultSource = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace DefaultNs
            {
                [StorageSerializable]
                public partial record struct Bag([property: Tag(1)] IEnumerable<int> Values);

                public static class BagFactory
                {
                    public static Bag Make() => new Bag(new int[] { 1, 2, 3, 4, 5 });
                }
            }
            """;

        const string explicitSource = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace ExplicitNs
            {
                [StorageSerializable]
                public partial record struct Bag([property: Tag(1), Packed] IEnumerable<int> Values);

                public static class BagFactory
                {
                    public static Bag Make() => new Bag(new int[] { 1, 2, 3, 4, 5 });
                }
            }
            """;

        var defaultAsm = CompileAndLoad(defaultSource);
        var explicitAsm = CompileAndLoad(explicitSource);

        var defaultVal = defaultAsm.GetType("DefaultNs.BagFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var explicitVal = explicitAsm.GetType("ExplicitNs.BagFactory")!.GetMethod("Make")!.Invoke(null, null)!;

        Serialize(explicitVal).Should().Equal(Serialize(defaultVal));
    }

    // Applying both [Packed] and [Unpacked] to the same property is a user error — the
    // generator must emit BSSG018 so build fails fast with a clear message.
    [Fact]
    public void BSSG018_Fires_When_Both_Packed_And_Unpacked_Declared()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNs;
            [StorageSerializable]
            public partial record struct Bag(
                [property: Tag(1), Packed, Unpacked] IEnumerable<int> Values);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG018");
    }

    // Non-fixed-width element types (string, Guid, nested records) are always count-prefixed
    // regardless of [Packed] / [Unpacked]. Exercises the fall-through path — the attribute
    // does not apply, but neither is it an error. Size and round-trip remain as-is.
    [Fact]
    public void String_Collection_Ignores_Packed_Attribute_And_Round_Trips()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNs
            {
                [StorageSerializable]
                public partial record struct Bag([property: Tag(1), Packed] IEnumerable<string> Values);

                public static class BagFactory
                {
                    public static Bag Make() => new Bag(new[] { "hello", "world" });
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNs.BagFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(original);
        var roundtrip = Deserialize(asm, "TestNs.Bag", bytes);

        var roundtripValues = (IEnumerable<string>)roundtrip.GetType().GetProperty("Values")!.GetValue(roundtrip)!;
        roundtripValues.Should().Equal(["hello", "world"]);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Helpers — mirror TaggedWireTests so the two test surfaces stay symmetrical.
    // ═════════════════════════════════════════════════════════════════════════════════════

    private static Assembly CompileAndLoad(string source)
    {
        var asmName = "TestAsm_" + System.Guid.NewGuid().ToString("N");
        var compilation = TestHelpers.CreateCompilation(source, assemblyName: asmName);
        var generator = new StorageSourceGenerator();
        var driver = CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        errors.Should().BeEmpty(
            because: "generated code must compile before we can exercise it; got:\n" +
                     string.Join("\n", errors.Select(d => d.ToString())));

        using var ms = new MemoryStream();
        var result = outputCompilation.Emit(ms);
        result.Success.Should().BeTrue(because: "generated code must emit successfully for runtime tests");

        ms.Position = 0;
        return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    private static byte[] Serialize(object record)
    {
        var method = record.GetType().GetMethod("Serialize", [typeof(Stream)])
            ?? throw new InvalidOperationException("Record lacks Serialize(Stream).");
        using var ms = new MemoryStream();
        method.Invoke(record, [ms]);
        return ms.ToArray();
    }

    private static object Deserialize(Assembly asm, string typeFqn, byte[] bytes)
    {
        var t = asm.GetType(typeFqn) ?? throw new InvalidOperationException($"Type {typeFqn} not found.");
        var method = t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Type {typeFqn} lacks static Deserialize(Stream).");
        using var ms = new MemoryStream(bytes, writable: false);
        return method.Invoke(null, [ms])!;
    }
}
