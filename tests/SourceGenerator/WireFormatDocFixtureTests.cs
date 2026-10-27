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

// Phase F1 — byte-level fixtures mirroring every worked example in docs/wire-format-2.0.md.
// The test locks the emitter's output to the bytes shown in the spec document; a drift in
// either direction (doc text or emitter behaviour) fails this test immediately. If a
// well-meaning contributor changes the emitter's encoding, §6 of the doc loses its promise
// to third-party implementors and this suite flags that as a build break.
//
// Each fixture maps to one `hex : ...` line in the markdown. When the doc's hex line
// changes, the `Expected` array here must change in the same commit — that discipline is
// what gives the document its byte-exact contract.
public sealed class WireFormatDocFixtureTests
{
    // §1 "Empty record" — a zero-field [StorageSerializable] record encodes to the three
    // bytes shown. The generator still emits the tag-0 schema-version header even when no
    // user field is present, because the reader's §2 dispatch expects it unconditionally.
    [Fact]
    public void Section_1_Empty_Record_Matches_Doc_Hex()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace DocFixture
            {
                [StorageSerializable]
                public partial record struct Empty();

                public static class EmptyFactory
                {
                    public static Empty Make() => new Empty();
                }
            }
            """;

        AssertEncodesTo(
            source,
            "DocFixture.Empty",
            "DocFixture.EmptyFactory",
            expectedHex: "02 00 01");
    }

    // §2 "int Volume = 42, [Tag(1)]" — zigzag(42) = 84 = 0x54.
    [Fact]
    public void Section_2_Int_Field_Matches_Doc_Hex()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace DocFixture
            {
                [StorageSerializable]
                public partial record struct Order([property: Tag(1)] int Volume);

                public static class OrderFactory
                {
                    public static Order Make() => new Order(42);
                }
            }
            """;

        AssertEncodesTo(
            source,
            "DocFixture.Order",
            "DocFixture.OrderFactory",
            expectedHex: "04 00 01 08 54");
    }

    // §5 "string Name = \"hi\", [Tag(1)]" — UTF-8 "hi" = 0x68 0x69.
    [Fact]
    public void Section_5_String_Field_Matches_Doc_Hex()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace DocFixture
            {
                [StorageSerializable]
                public partial record struct Person([property: Tag(1)] string Name);

                public static class PersonFactory
                {
                    public static Person Make() => new Person("hi");
                }
            }
            """;

        AssertEncodesTo(
            source,
            "DocFixture.Person",
            "DocFixture.PersonFactory",
            expectedHex: "06 00 01 0C 02 68 69");
    }

    // §6.1 "IEnumerable<int> Values = [1, 2, 3], [Tag(1)]" — packed block of three zigzag
    // varints, no count.
    [Fact]
    public void Section_6_1_Packed_Int_Collection_Matches_Doc_Hex()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace DocFixture
            {
                [StorageSerializable]
                public partial record struct Bag([property: Tag(1)] IEnumerable<int> Values);

                public static class BagFactory
                {
                    public static Bag Make() => new Bag(new int[] { 1, 2, 3 });
                }
            }
            """;

        AssertEncodesTo(
            source,
            "DocFixture.Bag",
            "DocFixture.BagFactory",
            expectedHex: "07 00 01 0C 03 02 04 06");
    }

    // §6.2 "IEnumerable<long> Values = [1, 2], [Tag(1), Unpacked]" — one tag+body per
    // element. Tag header is `(1 << 3) | 0 = 0x08` (Varint wire type).
    [Fact]
    public void Section_6_2_Unpacked_Long_Collection_Matches_Doc_Hex()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace DocFixture
            {
                [StorageSerializable]
                public partial record struct Bag([property: Tag(1), Unpacked] IEnumerable<long> Values);

                public static class BagFactory
                {
                    public static Bag Make() => new Bag(new long[] { 1, 2 });
                }
            }
            """;

        AssertEncodesTo(
            source,
            "DocFixture.Bag",
            "DocFixture.BagFactory",
            expectedHex: "06 00 01 08 02 08 04");
    }

    // §6.3 "IEnumerable<string> Tags = [\"a\", \"b\"], [Tag(1)]" — count-prefixed wire with
    // backpatch-LEN trick: LEN + count are written as 10-byte padded ULEB128 slots,
    // backpatched at end-of-block. LEN = 10 (padded count) + 4 (string bodies) = 14; count
    // = 2. Both encode as `0x8X 0x80 0x80 0x80 0x80 0x80 0x80 0x80 0x80 0x00` where X = the
    // canonical low-7-bits payload (0xE for 14, 0x2 for 2).
    [Fact]
    public void Section_6_3_CountPrefixed_String_Collection_Matches_Doc_Hex()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace DocFixture
            {
                [StorageSerializable]
                public partial record struct StringBag([property: Tag(1)] IEnumerable<string> Tags);

                public static class StringBagFactory
                {
                    public static StringBag Make() => new StringBag(new string[] { "a", "b" });
                }
            }
            """;

        AssertEncodesTo(
            source,
            "DocFixture.StringBag",
            "DocFixture.StringBagFactory",
            expectedHex: "1B 00 01 0C " +
                         "8E 80 80 80 80 80 80 80 80 00 " +
                         "82 80 80 80 80 80 80 80 80 00 " +
                         "01 61 01 62");
    }

    // §7.1 "[Key] Pt At = Pt(5, 7), [Tag(1)]" — a [Key] child writes no length prefix of its
    // own, so the parent frames it with the padded 10-byte LEN slot (LEN = 8). The child bytes
    // are the ordered wire: each int big-endian with the sign bit flipped.
    [Fact]
    public void Section_7_1_Parent_Framed_Key_Child_Matches_Doc_Hex()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace DocFixture
            {
                [Key]
                public partial record struct Pt([property: Tag(1)] int X, [property: Tag(2)] int Y);

                [StorageSerializable]
                public partial record struct Pin([property: Tag(1)] Pt At);

                public static class PinFactory
                {
                    public static Pin Make() => new Pin(new Pt(5, 7));
                }
            }
            """;

        AssertEncodesTo(
            source,
            "DocFixture.Pin",
            "DocFixture.PinFactory",
            expectedHex: "15 00 01 0C " +
                         "88 80 80 80 80 80 80 80 80 00 " +
                         "80 00 00 05 80 00 00 07");
    }

    // The docs-file smoke: the worked-example hex strings above must literally appear as
    // `hex : <expected>` lines in docs/wire-format-2.0.md. This test fails if an editor
    // changes the doc without updating the test — or vice versa.
    [Fact]
    public void Doc_File_Contains_Every_Worked_Example_Hex_Line()
    {
        var docPath = LocateDoc();
        var docText = File.ReadAllText(docPath);

        foreach (var hex in s_workedExampleHex)
        {
            var expectedLine = "hex  : " + hex;
            docText.Should().Contain(
                expectedLine,
                $"docs/wire-format-2.0.md must contain a `{expectedLine}` line; if the emitter " +
                "output changed, update the doc in the same commit as the emitter change.");
        }
    }

    private static readonly string[] s_workedExampleHex =
    [
        "02 00 01",
        "04 00 01 08 54",
        "06 00 01 0C 02 68 69",
        "07 00 01 0C 03 02 04 06",
        "06 00 01 08 02 08 04",
        "1B 00 01 0C 8E 80 80 80 80 80 80 80 80 00 82 80 80 80 80 80 80 80 80 00 01 61 01 62",
        "15 00 01 0C 88 80 80 80 80 80 80 80 80 00 80 00 00 05 80 00 00 07",
    ];

    // The reverse direction of the smoke above: every `hex  :` worked-example line IN the
    // doc must be one of the fixture-verified strings. Without this, a 7th worked example
    // added to the doc with no fixture ships unverified while the suite stays green.
    [Fact]
    public void Every_Doc_Hex_Line_Has_A_Verifying_Fixture()
    {
        var docPath = LocateDoc();
        var docHexLines = File.ReadAllLines(docPath)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("hex  :", StringComparison.Ordinal))
            .Select(l => l["hex  :".Length..].Trim())
            .ToList();

        docHexLines.Should().NotBeEmpty("the doc's worked examples are its load-bearing spec surface");
        foreach (var hex in docHexLines)
        {
            s_workedExampleHex.Should().Contain(
                hex,
                $"every `hex  : {hex}` worked example in docs/wire-format-2.0.md must have a " +
                "fixture in this file that locks it against the emitter's actual output");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Helpers — same compile-emit-execute shape as TaggedWireTests/PackedAttributeTests so
    // the three test surfaces stay symmetrical.
    // ═════════════════════════════════════════════════════════════════════════════════════

    private static void AssertEncodesTo(string source, string recordFqn, string factoryFqn, string expectedHex)
    {
        var asm = CompileAndLoad(source);
        var factory = asm.GetType(factoryFqn)!;
        var original = factory.GetMethod("Make")!.Invoke(null, null)!;

        var actualBytes = Serialize(original);
        var expectedBytes = ParseHex(expectedHex);

        actualBytes.Should().Equal(expectedBytes,
            $"encoding of {recordFqn} must match the hex line in docs/wire-format-2.0.md; " +
            $"got {FormatHex(actualBytes)}, expected {expectedHex}");

        // Round-trip back to the record and re-serialise. Value equality on records with
        // `IEnumerable<T>` fields uses reference equality on the enumerable, so comparing
        // the bytes of two serialisations is the honest closed-loop — it locks the decoder
        // side without requiring a deep-structural comparator in every fixture.
        var roundtrip = Deserialize(asm, recordFqn, actualBytes);
        Serialize(roundtrip).Should().Equal(actualBytes,
            $"re-serialising the deserialised {recordFqn} must reproduce the original bytes byte-for-byte");
    }

    private static byte[] ParseHex(string hex)
    {
        var tokens = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            bytes[i] = Convert.ToByte(tokens[i], 16);
        }
        return bytes;
    }

    private static string FormatHex(byte[] bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 3);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // The test binary sits at `tests/SourceGenerator/bin/<config>/net10.0/`; the doc lives
    // at `docs/wire-format-2.0.md` relative to the repo root. Walk up the directory tree
    // until we find a sibling `docs/` with the file, or bail with a clear message.
    private static string LocateDoc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "wire-format-2.0.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate docs/wire-format-2.0.md by walking up from the test binary's directory. " +
            "Confirm the Phase F1 deliverable was created and is tracked by git.");
    }

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
