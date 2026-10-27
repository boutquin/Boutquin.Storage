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
using System.Runtime.Loader;

namespace Boutquin.Storage.SourceGenerator.Tests;

// Two contracts the generators owe every annotated shape they accept.
//
// 1. Construction: a shape the generated code cannot construct (an abstract record, a record
//    class whose required members a bare `new T()` leaves unset) or a member it cannot address
//    (an indexer) is either handled or rejected with a named, located Error — never emitted as
//    code that fails to compile inside the generated file.
// 2. Recursion: a self-referential [StorageSerializable] schema is supported, so its decoder
//    must bound how deeply forged bytes can nest it. Past the bound the generated Deserialize
//    throws InvalidDataException (the typed corruption signal) instead of recursing until the
//    process dies of an uncatchable stack overflow, and the writer refuses the same depth so a
//    value that serializes can always be read back.
public sealed class GeneratorUnconstructibleShapeAndRecursionTests
{
    // The documented per-type nesting bound of generated tagged-wire readers and writers.
    private const int MaxNestingDepth = 100;

    // ───── Harness ────────────────────────────────────────────────────

    private static (CSharpCompilation Output, GeneratorDriverRunResult Run) RunGenerators(
        string source,
        params IIncrementalGenerator[] generators)
    {
        _ = typeof(Boutquin.Storage.Domain.Attributes.ColumnarStorableAttribute).FullName;
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create(
            "ShapeRec_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver
            .Create(generators.Select(g => g.AsSourceGenerator()).ToArray())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return ((CSharpCompilation)output, driver.GetRunResult());
    }

    private static List<Diagnostic> CompileErrors(CSharpCompilation output) =>
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d => d.ToString()));

    private static void AssertRejectedWithBssg025(string source, params IIncrementalGenerator[] generators)
    {
        var (output, run) = RunGenerators(source, generators);
        var compileErrors = CompileErrors(output);

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG025" && d.Severity == DiagnosticSeverity.Error,
            "the generated code cannot construct this type; got generator diagnostics:\n"
            + Describe(run.Diagnostics) + "\nand compile errors:\n" + Describe(compileErrors));
        run.GeneratedTrees.Should().BeEmpty("a rejected type gets no emitted code");
        compileErrors.Should().BeEmpty("rejection must not leave uncompilable generated code behind");
    }

    private static Assembly CompileAndLoad(string source, params IIncrementalGenerator[] generators)
    {
        var (output, run) = RunGenerators(source, generators);
        var errors = CompileErrors(output);
        errors.Should().BeEmpty("generated code must compile; generator diagnostics:\n"
            + Describe(run.Diagnostics) + "\ncompile errors:\n" + Describe(errors));

        using var ms = new MemoryStream();
        var emit = output.Emit(ms);
        emit.Success.Should().BeTrue(Describe(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        ms.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    // ───── Abstract targets (BSSG025) ─────────────────────────────────

    [Fact]
    public void StorageSerializable_AbstractRecord_IsRejectedWithBssg025()
    {
        AssertRejectedWithBssg025("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public abstract partial record Rec([property: Tag(1)] int A);
            """, new StorageSourceGenerator());
    }

    [Fact]
    public void Key_AbstractRecord_IsRejectedWithBssg025()
    {
        AssertRejectedWithBssg025("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [Key]
            public abstract partial record K([property: Tag(1)] int A);
            """, new StorageSourceGenerator());
    }

    [Fact]
    public void ColumnarStorable_AbstractRecord_IsRejectedWithBssg025()
    {
        AssertRejectedWithBssg025("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [ColumnarStorable]
            public abstract partial record Row(System.DateOnly Date, double Close);
            """, new ColumnarSourceGenerator());
    }

    [Fact]
    public void StorageSerializable_RequiredMembersTheConstructorDoesNotSet_IsRejectedWithBssg025()
    {
        // The positional constructor matches, but without [SetsRequiredMembers] the generated
        // `new T(...)` in Deserialize is CS9035.
        AssertRejectedWithBssg025("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record Rec
            {
                public Rec(int a, int b) { A = a; B = b; }
                [Tag(1)] public int A { get; init; }
                [Tag(2)] public required int B { get; init; }
            }
            """, new StorageSourceGenerator());
    }

    [Fact]
    public void StorageSerializable_RequiredMembersSetByTheConstructor_Generates()
    {
        var asm = CompileAndLoad("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record Rec
            {
                [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
                public Rec(int a, int b) { A = a; B = b; }
                [Tag(1)] public int A { get; init; }
                [Tag(2)] public required int B { get; init; }
            }
            """, new StorageSourceGenerator());
        var t = asm.GetType("P.Rec")!;
        var original = Activator.CreateInstance(t, 3, 4)!;
        using var ms = new MemoryStream();
        t.GetMethod("Serialize", [typeof(Stream)])!.Invoke(original, [ms]);
        ms.Position = 0;
        t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [ms])
            .Should().Be(original);
    }

    // ───── Indexers on a [ColumnarStorable] record ────────────────────

    [Fact]
    public void ColumnarStorable_RecordWithIndexers_GeneratesCompilingCode_AndIgnoresTheIndexers()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [ColumnarStorable]
            public partial record struct Row(System.DateOnly Date, double Close)
            {
                public double this[int i] => Close * i;
                public int this[string s] { get => s.Length; set { } }
            }
            """;

        var asm = CompileAndLoad(source, new ColumnarSourceGenerator());
        var t = asm.GetType("P.Row")!;
        var columns = new Dictionary<string, Array>
        {
            ["Date"] = new[] { new DateOnly(2026, 1, 2), new DateOnly(2026, 3, 4) },
            ["Close"] = new[] { 1.5, 2.5 },
        };
        var row = t.GetMethod("Rehydrate", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [columns, 1])!;
        t.GetProperty("Date")!.GetValue(row).Should().Be(new DateOnly(2026, 3, 4));
        t.GetProperty("Close")!.GetValue(row).Should().Be(2.5);
    }

    // ───── Required members on a [ColumnarStorable] record class ──────

    [Fact]
    public void ColumnarStorable_RecordClassWithRequiredMembers_RehydrateAndProjectTo_CompileAndCarryValues()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [ColumnarStorable]
            public partial record Row
            {
                public required System.DateOnly Date { get; init; }
                public required string Symbol { get; init; }
                public double Close { get; init; }
            }
            """;

        var asm = CompileAndLoad(source, new ColumnarSourceGenerator());
        var t = asm.GetType("P.Row")!;
        var columns = new Dictionary<string, Array>
        {
            ["Date"] = new[] { new DateOnly(2026, 1, 2), new DateOnly(2026, 3, 4) },
            ["Symbol"] = new[] { "AAA", "BBB" },
            ["Close"] = new[] { 1.5, 2.5 },
        };
        var row = t.GetMethod("Rehydrate", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [columns, 1])!;
        t.GetProperty("Date")!.GetValue(row).Should().Be(new DateOnly(2026, 3, 4));
        t.GetProperty("Symbol")!.GetValue(row).Should().Be("BBB");
        t.GetProperty("Close")!.GetValue(row).Should().Be(2.5);

        var projection = new Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnProjection(["Close"]);
        var projected = t.GetMethod("ProjectTo")!.Invoke(row, [projection])!;
        t.GetProperty("Close")!.GetValue(projected).Should().Be(2.5);
        t.GetProperty("Date")!.GetValue(projected).Should().Be(default(DateOnly), "an unprojected field is clipped to default");
    }

    // A required member the column plan does not carry (a non-public property or field) is never
    // written to a segment, so Rehydrate could only seed it with default — the value is lost on
    // every read. Reject it, as the row path rejects a required member its constructor leaves unset.
    [Theory]
    [InlineData("record", "internal required double Close { get; init; }")]
    [InlineData("record", "internal required double Close;")]
    [InlineData("record struct", "internal required double Close { get; init; }")]
    public void ColumnarStorable_RequiredMemberThatIsNotAColumn_IsRejectedWithBssg025(string typeKeyword, string member)
    {
        AssertRejectedWithBssg025($$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [ColumnarStorable]
            internal partial {{typeKeyword}} Row
            {
                public required System.DateOnly Date { get; init; }
                {{member}}
            }
            """, new ColumnarSourceGenerator());
    }

    // ───── Recursive tagged-wire schemas ──────────────────────────────

    private const string RecursiveSources = """
        using System.Collections.Generic;
        using Boutquin.Storage.Domain.Attributes;
        namespace R;
        [StorageSerializable]
        public partial record Node([property: Tag(1)] int V, [property: Tag(2)] Node? Next);
        [StorageSerializable]
        public partial record struct Tree([property: Tag(1)] IEnumerable<Tree> Kids);
        [StorageSerializable]
        public partial record Ping([property: Tag(1)] Pong? Other);
        [StorageSerializable]
        public partial record Pong([property: Tag(1)] Ping? Other);
        """;

    private static Assembly? s_recursive;

    private static Assembly Recursive => s_recursive ??= CompileAndLoad(RecursiveSources, new StorageSourceGenerator());

    // Varint helpers mirror the wire format's ULEB128 so forged frames are built independently
    // of the generated writer.
    private static void WriteVarint(List<byte> buf, ulong value)
    {
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            buf.Add(value != 0 ? (byte)(b | 0x80) : b);
        }
        while (value != 0);
    }

    private static byte[] Frame(List<byte> payload)
    {
        var framed = new List<byte>();
        WriteVarint(framed, (ulong)payload.Count);
        framed.AddRange(payload);
        return framed.ToArray();
    }

    // Node chain: each level is `len | 00 01 (tag-0 version 1) | 0x14 (tag 2, LEN) | child`.
    // The self-framed child's own length prefix is the field's LEN.
    internal static byte[] ForgeNodeChain(int depth)
    {
        var current = Frame([0x00, 0x01]);
        for (var level = 1; level < depth; level++)
        {
            var payload = new List<byte> { 0x00, 0x01, 0x14 };
            payload.AddRange(current);
            current = Frame(payload);
        }

        return current;
    }

    // Tree: each level is `len | 00 01 | 0x0C (tag 1, LEN) | blockLen | count=1 | child`.
    internal static byte[] ForgeTree(int depth)
    {
        var current = Frame([0x00, 0x01]);
        for (var level = 1; level < depth; level++)
        {
            var block = new List<byte>();
            WriteVarint(block, 1);
            block.AddRange(current);
            var payload = new List<byte> { 0x00, 0x01, 0x0C };
            WriteVarint(payload, (ulong)block.Count);
            payload.AddRange(block);
            current = Frame(payload);
        }

        return current;
    }

    // Ping/Pong alternation: each level is `len | 00 01 | 0x0C (tag 1, LEN) | child`.
    internal static byte[] ForgePingPong(int depth)
    {
        var current = Frame([0x00, 0x01]);
        for (var level = 1; level < depth; level++)
        {
            var payload = new List<byte> { 0x00, 0x01, 0x0C };
            payload.AddRange(current);
            current = Frame(payload);
        }

        return current;
    }

    private static object Deserialize(string typeFqn, byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        return Recursive.GetType(typeFqn)!.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [ms])!;
    }

    private static int NodeChainDepth(object node)
    {
        var depth = 0;
        var next = node.GetType().GetProperty("Next")!;
        for (object? current = node; current is not null; current = next.GetValue(current))
        {
            depth++;
        }

        return depth;
    }

    private static object BuildNodeChain(int depth)
    {
        var t = Recursive.GetType("R.Node")!;
        object? current = null;
        for (var i = 0; i < depth; i++)
        {
            current = Activator.CreateInstance(t, i, current);
        }

        return current!;
    }

    [Fact]
    public void RecursiveRecord_ForgedChainDeeperThanTheNestingBound_ThrowsInvalidDataException()
    {
        var forged = ForgeNodeChain(MaxNestingDepth + 1);

        var act = () => Deserialize("R.Node", forged);

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidDataException>("a chain nested past the bound is corrupt or hostile input");
    }

    [Fact]
    public void RecursiveCollection_ForgedTreeDeeperThanTheNestingBound_ThrowsInvalidDataException()
    {
        var forged = ForgeTree(MaxNestingDepth + 1);

        var act = () => Deserialize("R.Tree", forged);

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidDataException>("recursion through a collection is bounded the same way");
    }

    [Fact]
    public void MutuallyRecursiveRecords_ForgedAlternationFarPastTheBound_ThrowsInvalidDataException()
    {
        // Ping → Pong → Ping …: each type is guarded, so the alternation is refused once either
        // type has nested past the bound, instead of recursing for the whole forged depth.
        var forged = ForgePingPong(4 * MaxNestingDepth);

        var act = () => Deserialize("R.Ping", forged);

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidDataException>("mutual recursion is bounded by the per-type guard");
    }

    [Fact]
    public void RecursiveRecord_ChainAtTheNestingBound_RoundTripsThroughTheGeneratedWriterAndReader()
    {
        var chain = BuildNodeChain(MaxNestingDepth);
        using var ms = new MemoryStream();
        chain.GetType().GetMethod("Serialize", [typeof(Stream)])!.Invoke(chain, [ms]);

        var decoded = Deserialize("R.Node", ms.ToArray());

        NodeChainDepth(decoded).Should().Be(MaxNestingDepth);
        decoded.Should().Be(chain, "a value inside the bound round-trips unchanged");
    }

    [Fact]
    public void RecursiveRecord_SerializingAChainDeeperThanTheNestingBound_IsRefused()
    {
        // The writer enforces the reader's bound, so no value is ever persisted that the
        // generated reader would then reject as corrupt.
        var chain = BuildNodeChain(MaxNestingDepth + 1);
        using var ms = new MemoryStream();

        var act = () => chain.GetType().GetMethod("Serialize", [typeof(Stream)])!.Invoke(chain, [ms]);

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>("the writer must refuse what the reader would refuse");
    }

    [Fact]
    public void ForgedRecursiveFrames_DecodeToTheirDeclaredDepth_WhenInsideTheBound()
    {
        // Pins the forged layouts above against the real reader, so the refusal tests measure
        // depth and not a malformed frame.
        NodeChainDepth(Deserialize("R.Node", ForgeNodeChain(3))).Should().Be(3);
        var tree = Deserialize("R.Tree", ForgeTree(3));
        var kids = (System.Collections.IEnumerable)tree.GetType().GetProperty("Kids")!.GetValue(tree)!;
        kids.Cast<object>().Should().HaveCount(1);
        Deserialize("R.Ping", ForgePingPong(3)).Should().NotBeNull();
    }
}
