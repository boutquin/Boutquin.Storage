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
/// Contract of the code the row generator emits for property types:
/// <list type="bullet">
/// <item>every emitted type reference binds to the user's own symbol, even when the target's
/// namespace has a sibling namespace that shadows the property type's root namespace;</item>
/// <item>a nested child whose <c>Serialize</c> does not write its own length prefix (a
/// <c>[Key]</c> record, or a hand-implemented <c>ISerializable&lt;T&gt;</c>) is length-framed by
/// the parent, so a reader that does not know the tag can skip it without misparsing the
/// fields that follow.</item>
/// </list>
/// </summary>
public sealed class GeneratorEmittedCodeContractTests
{
    private static CSharpCompilation CreateNullableEnabled(string source)
    {
        _ = typeof(Boutquin.Storage.Domain.Attributes.StorageSerializableAttribute).FullName;
        _ = typeof(Boutquin.Storage.Domain.Helpers.VarintHelpers).FullName;
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));
        return CSharpCompilation.Create(
            "EmittedCodeContract_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static (CSharpCompilation Input, CSharpCompilation Output) Run(string source)
    {
        var input = CreateNullableEnabled(source);
        CSharpGeneratorDriver
            .Create(new StorageSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(input, out var output, out _);
        return (input, (CSharpCompilation)output);
    }

    private static List<string> Errors(CSharpCompilation compilation) =>
        compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} @{d.Location.SourceTree?.FilePath}: {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}")
            .ToList();

    private static Assembly CompileAndLoad(string source)
    {
        var (input, output) = Run(source);
        Errors(input).Should().BeEmpty("the user source must be legal C# before the generator runs");
        Errors(output).Should().BeEmpty("the generated code must compile");
        using var ms = new MemoryStream();
        output.Emit(ms).Success.Should().BeTrue();
        ms.Position = 0;
        return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    private static byte[] Serialize(object value)
    {
        using var ms = new MemoryStream();
        value.GetType().GetMethod("Serialize", [typeof(Stream)])!.Invoke(value, [ms]);
        return ms.ToArray();
    }

    private static object Deserialize(Type type, byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        try
        {
            return type.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [ms])!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    // The old reader's view of the bytes: its ToString on success, or the exception it threw —
    // so a throw and a silent field loss both fail at the same string assertion.
    private static string OldReaderView(Type oldReader, byte[] bytes)
    {
        try
        {
            return Deserialize(oldReader, bytes).ToString()!;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or InvalidOperationException)
        {
            return "THROW " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    // ── Emitted type references bind to the user's symbol ───────────────────────────────────
    // `App.Models` sees `Shared.Unit` through `using Shared;`, but inside the generated file's
    // `namespace App.Models` scope a display-form `Shared.Unit` resolves `Shared` to the sibling
    // namespace `App.Shared` first (CS0234). Only a `global::`-qualified reference is immune.

    [Fact]
    public void TaggedWire_EnumPropertyUnderShadowingSiblingNamespace_GeneratesCompilableCode()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using Shared;
            namespace Shared { public enum Unit { A, B } }
            namespace App.Shared { public static class Marker { } }
            namespace App.Models
            {
                [StorageSerializable]
                public partial record struct Row([property: Tag(1)] Unit U, [property: Tag(2)] Unit? N, [property: Tag(3)] int V);
            }
            """;

        var (input, output) = Run(source);

        Errors(input).Should().BeEmpty("the user source is legal C#");
        Errors(output).Should().BeEmpty(
            "an enum property's emitted type references must be global::-qualified so they bind to Shared.Unit, not App.Shared");
    }

    [Fact]
    public void OrderedWire_EnumAndNullableEnumKeyUnderShadowingSiblingNamespace_GeneratesCompilableCode()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using Shared;
            namespace Shared { public enum Unit { A, B } }
            namespace App.Shared { public static class Marker { } }
            namespace App.Models
            {
                [Key]
                public partial record struct K([property: Tag(1)] Unit U, [property: Tag(2)] Unit? N, [property: Tag(3)] int V);
            }
            """;

        var (input, output) = Run(source);

        Errors(input).Should().BeEmpty("the user source is legal C#");
        Errors(output).Should().BeEmpty(
            "the ordered-wire reader's locals and casts must be global::-qualified so they bind to Shared.Unit");
    }

    [Fact]
    public void TaggedWire_NestedRecordAndRecordCollectionUnderShadowingSiblingNamespace_RoundTrip()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using Shared;
            namespace Shared
            {
                [StorageSerializable]
                public partial record struct Inner([property: Tag(1)] int X);
            }
            namespace App.Shared { public static class Marker { } }
            namespace App.Models
            {
                [StorageSerializable]
                public partial record struct Outer([property: Tag(1)] Inner I, [property: Tag(2)] System.Collections.Generic.IEnumerable<Inner> Many);

                public static class Factory
                {
                    public static Outer Make() => new Outer(new Inner(7), new[] { new Inner(1), new Inner(2) });
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var outerType = asm.GetType("App.Models.Outer")!;
        var original = asm.GetType("App.Models.Factory")!.GetMethod("Make")!.Invoke(null, null)!;

        var bytes = Serialize(original);
        var back = Deserialize(outerType, bytes);

        Serialize(back).Should().Equal(bytes, "the nested record and record collection must round-trip");
        outerType.GetProperty("I")!.GetValue(back)!.ToString().Should().Be("Inner { X = 7 }");
    }

    // The runtime schema descriptor's declared-type text is a schema-registry contract compared
    // across schema versions; qualifying emitted code must not change it.
    [Fact]
    public void TaggedWireSchemaDescriptor_DeclaredTypeText_StaysInDisplayForm()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace Shared
            {
                public enum Unit { A, B }
                [StorageSerializable]
                public partial record struct Inner([property: Tag(1)] int X);
            }
            namespace App.Models
            {
                [StorageSerializable]
                public partial record class Row(
                    [property: Tag(1)] global::Shared.Unit U,
                    [property: Tag(2)] global::Shared.Unit? N,
                    [property: Tag(3)] global::Shared.Inner I,
                    [property: Tag(4)] IEnumerable<global::Shared.Inner> Many,
                    [property: Tag(5)] IEnumerable<int> Ints,
                    [property: Tag(6)] string? S,
                    [property: Tag(7)] System.Int128 Big);
            }
            """;

        var asm = CompileAndLoad(source);
        var fields = ((System.Collections.IEnumerable)asm.GetType("App.Models.Row")!
                .GetProperty("TaggedWireSchemaFields")!.GetValue(null)!)
            .Cast<object>()
            .Select(f => (string)f.GetType().GetProperty("TypeName")!.GetValue(f)!)
            .ToList();

        fields.Should().Equal(
            "Shared.Unit",
            "Shared.Unit?",
            "Shared.Inner",
            "System.Collections.Generic.IEnumerable<Shared.Inner>",
            "System.Collections.Generic.IEnumerable<int>",
            "string?",
            "System.Int128");
    }

    // ── A non-self-framing nested child is framed by the parent ─────────────────────────────
    // V2 adds tag 2 holding the child; V1 (tags 1 and 3 only) must skip tag 2 by its
    // LengthPrefixed wire type and still read C. The child values include the two pairs whose
    // leading ordered-key bytes, read as a LEN, previously made V1 silently lose C.

    private const string NestedKeyChildSource = """
        using Boutquin.Storage.Domain.Attributes;
        namespace P
        {
            [Key] public partial record struct KeyChild([property: Tag(1)] int X, [property: Tag(2)] int Y);
        }
        namespace P.V2
        {
            [StorageSerializable] public partial record struct OuterK([property: Tag(1)] int A, [property: Tag(2)] P.KeyChild K, [property: Tag(3)] int C);
        }
        namespace P.V1
        {
            [StorageSerializable] public partial record struct OuterK([property: Tag(1)] int A, [property: Tag(3)] int C);
        }
        """;

    [Theory]
    [InlineData(5, 7)]
    [InlineData(0, 0)]
    [InlineData(1 << 20, 99)]
    [InlineData(unchecked((int)0x89000000), 1)]
    [InlineData(unchecked((int)0x84000000), 0x000A0300)]
    public void NestedKeyChild_OldReaderSkipsIt_AndKeepsTheFieldsAfterIt(int x, int y)
    {
        var asm = CompileAndLoad(NestedKeyChildSource);
        var child = Activator.CreateInstance(asm.GetType("P.KeyChild")!, x, y)!;
        var v2Type = asm.GetType("P.V2.OuterK")!;
        var v2 = Activator.CreateInstance(v2Type, 11, child, 33)!;

        var bytes = Serialize(v2);

        OldReaderView(asm.GetType("P.V1.OuterK")!, bytes).Should().Be(
            "OuterK { A = 11, C = 33 }",
            "a reader that does not know tag 2 must skip the nested [Key] child by its LEN frame");
        Deserialize(v2Type, bytes).Should().Be(v2, "the current reader must round-trip the nested [Key] child");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(200)]
    public void HandImplementedSerializableChild_OldReaderSkipsIt_AndKeepsTheFieldsAfterIt(int x)
    {
        // The hand-implemented body writes one raw byte and no length prefix — a legal
        // ISerializable<T> implementation that makes no framing promise.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using Boutquin.Storage.Domain.Interfaces;
            namespace P
            {
                [StorageSerializable]
                public partial record struct RawChild([property: Tag(1)] int X) : ISerializable<RawChild>
                {
                    public void Serialize(System.IO.Stream stream) => stream.WriteByte((byte)X);
                    public static RawChild Deserialize(System.IO.Stream stream) => new RawChild(stream.ReadByte());
                }
            }
            namespace P.V2
            {
                [StorageSerializable] public partial record struct OuterR([property: Tag(1)] int A, [property: Tag(2)] P.RawChild R, [property: Tag(3)] int C);
            }
            namespace P.V1
            {
                [StorageSerializable] public partial record struct OuterR([property: Tag(1)] int A, [property: Tag(3)] int C);
            }
            """;

        var asm = CompileAndLoad(source);
        var child = Activator.CreateInstance(asm.GetType("P.RawChild")!, x)!;
        var v2Type = asm.GetType("P.V2.OuterR")!;
        var v2 = Activator.CreateInstance(v2Type, 11, child, 33)!;

        var bytes = Serialize(v2);

        OldReaderView(asm.GetType("P.V1.OuterR")!, bytes).Should().Be(
            "OuterR { A = 11, C = 33 }",
            "a reader that does not know tag 2 must skip the hand-implemented child by its LEN frame");
        Deserialize(v2Type, bytes).Should().Be(v2, "the current reader must round-trip the hand-implemented child");
    }

    [Fact]
    public void NestedKeyChild_FrameWithUnreadTrailingBytes_IsRejectedAsCorrupt()
    {
        var asm = CompileAndLoad(NestedKeyChildSource);
        var child = Activator.CreateInstance(asm.GetType("P.KeyChild")!, 5, 7)!;
        var childBytes = Serialize(child);

        // A frame one byte longer than the child's own encoding: tag-0 + version, A = 11
        // (zigzag 22), tag 2 LengthPrefixed with LEN = child + 1, the child bytes, one stray
        // byte, then C = 33 (zigzag 66).
        var payload = new List<byte> { 0x00, 0x01, 0x08, 0x16, 0x14, (byte)(childBytes.Length + 1) };
        payload.AddRange(childBytes);
        payload.Add(0x00);
        payload.AddRange(new byte[] { 0x18, 0x42 });
        var record = new List<byte> { (byte)payload.Count };
        record.AddRange(payload);

        var act = () => Deserialize(asm.GetType("P.V2.OuterK")!, record.ToArray());

        act.Should().Throw<InvalidDataException>(
            "bytes left unread inside a parent-framed child mean the frame and the child disagree — corruption, not data");
    }

    // ── A collection block is decoded whole, and every element round-trips ──────────────────
    // Element framing inside an annotated collection matches the scalar field: a child that does
    // not self-frame (a [Key] record or a hand-implemented ISerializable<T>) gets its own LEN, and
    // the block's count must account for every byte the block carries.

    private const string CollectionSource = """
        using Boutquin.Storage.Domain.Attributes;
        using Boutquin.Storage.Domain.Interfaces;
        using System.Collections.Generic;
        using System.Linq;
        namespace C
        {
            [StorageSerializable] public partial record struct Inner([property: Tag(1)] int X);
            [StorageSerializable] public partial record struct OuterA([property: Tag(1)] IEnumerable<Inner> Xs, [property: Tag(2)] int After);
            [StorageSerializable] public partial record struct OuterS([property: Tag(1)] IEnumerable<string> Xs, [property: Tag(2)] int After);

            [Key] public partial record struct Empty();
            [StorageSerializable] public partial record struct EmptyHolder([property: Tag(1)] IEnumerable<Empty> Xs, [property: Tag(2)] int After);

            [Key] public partial record struct K([property: Tag(1)] int X);
            [StorageSerializable] public partial record struct KHolder([property: Tag(1)] IEnumerable<K> Xs, [property: Tag(2)] int After);

            // Raw UTF-8 with no length prefix; Deserialize reads to the end of its stream — a legal
            // ISerializable<T> body that makes no framing promise.
            [StorageSerializable]
            public partial record struct Blob([property: Tag(1)] string Text) : ISerializable<Blob>
            {
                public void Serialize(System.IO.Stream stream) { var b = System.Text.Encoding.UTF8.GetBytes(Text); stream.Write(b, 0, b.Length); }
                public static Blob Deserialize(System.IO.Stream stream) { var ms = new System.IO.MemoryStream(); stream.CopyTo(ms); return new Blob(System.Text.Encoding.UTF8.GetString(ms.ToArray())); }
            }
            [StorageSerializable] public partial record struct BlobHolder([property: Tag(1)] Blob One, [property: Tag(2)] IEnumerable<Blob> Many, [property: Tag(3)] int After);

            public static class Make
            {
                public static OuterA A() => new OuterA(new[] { new Inner(1), new Inner(2) }, 9);
                public static OuterS S() => new OuterS(new[] { "a", "b" }, 9);
                public static EmptyHolder E(int n) => new EmptyHolder(System.Linq.Enumerable.Repeat(new Empty(), n).ToArray(), 7);
                public static KHolder Ks() => new KHolder(new[] { new K(1), new K(-5) }, 7);
                public static KHolder K1() => new KHolder(new[] { new K(1) }, 7);
                public static BlobHolder B(string[] texts) => new BlobHolder(new Blob("one"), System.Linq.Enumerable.Select(texts, t => new Blob(t)).ToArray(), 7);
            }
        }
        """;

    private static readonly Lazy<Assembly> s_collectionAssembly = new(() => CompileAndLoad(CollectionSource));

    // The decoded view of a holder: its scalar text plus each collection element, so a merged or
    // dropped element fails the same string assertion as a throw.
    private static string View(object holder, string collection)
    {
        var elements = ((System.Collections.IEnumerable)holder.GetType().GetProperty(collection)!.GetValue(holder)!)
            .Cast<object>()
            .Select(e => e.ToString());
        var after = holder.GetType().GetProperty("After")!.GetValue(holder);
        return $"[{string.Join(", ", elements)}] After={after}";
    }

    private static object MakeValue(string factory, params object[] args) =>
        s_collectionAssembly.Value.GetType("C.Make")!.GetMethod(factory)!.Invoke(null, args)!;

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void EmptyKeyElementCollection_RoundTrips(int count)
    {
        var value = MakeValue("E", count);
        var bytes = Serialize(value);

        Func<object> read = () => Deserialize(s_collectionAssembly.Value.GetType("C.EmptyHolder")!, bytes);
        var back = read.Should().NotThrow("a zero-byte element is a value the writer accepted").Subject;

        View(back, "Xs").Should().Be(View(value, "Xs"), "every element the writer accepted must read back");
    }

    [Theory]
    [InlineData("x|yy")]
    [InlineData("alpha|beta|gamma")]
    [InlineData("|")]
    [InlineData("solo")]
    public void HandImplementedChildCollection_RoundTripsEachElementSeparately(string joinedTexts)
    {
        var value = MakeValue("B", (object)joinedTexts.Split('|'));
        var bytes = Serialize(value);

        Func<object> read = () => Deserialize(s_collectionAssembly.Value.GetType("C.BlobHolder")!, bytes);
        var back = read.Should().NotThrow("every element the writer accepted must read back").Subject;

        View(back, "Many").Should().Be(
            View(value, "Many"),
            "a child that makes no framing promise must be framed per element, as the scalar field is");
    }

    [Fact]
    public void KeyElementCollection_RoundTrips()
    {
        var value = MakeValue("Ks");

        var back = Deserialize(s_collectionAssembly.Value.GetType("C.KHolder")!, Serialize(value));

        View(back, "Xs").Should().Be("[K { X = 1 }, K { X = -5 }] After=7");
    }

    [Fact]
    public void ParentFramedElement_FrameWithUnreadTrailingBytes_IsRejectedAsCorrupt()
    {
        var bytes = Serialize(MakeValue("K1")).ToList();

        // Record length (1) + tag-0 header and version (2) + tag-1 header (1) put the block's
        // padded LEN slot at offset 4, the padded count slot at 14, the element's padded LEN slot
        // at 24 and the element's 4 ordered-wire bytes at 34..37. Grow the element frame by one
        // stray byte and keep every enclosing length consistent with it.
        bytes[24].Should().Be(0x84, "a [Key] element holding one int is framed with LEN 4");
        bytes.Insert(38, 0x00);
        bytes[0]++;
        bytes[4]++;
        bytes[24]++;

        var act = () => Deserialize(s_collectionAssembly.Value.GetType("C.KHolder")!, bytes.ToArray());

        act.Should().Throw<InvalidDataException>(
            "bytes left unread inside a parent-framed element mean the frame and the element disagree — corruption, not data");
    }
}
