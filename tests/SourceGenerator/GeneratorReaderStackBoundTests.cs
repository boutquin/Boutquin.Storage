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
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Boutquin.Storage.SourceGenerator.Tests;

// The generated tagged-wire reader's stack use is bounded by the schema, not by the bytes.
//
// Every field read in a generated Deserialize sits inside the `while (TryReadTag(...))` tag loop,
// and every collection element read sits inside a per-element loop as well. `stackalloc` memory is
// released only when the method returns, so a fixed-width scratch buffer allocated per read grows
// the stack once per element or per repeated tag: a legitimate 200 000-Guid record, or forged bytes
// repeating one tag 200 000 times, kills the process with an uncatchable stack overflow. The reader
// therefore allocates its fixed-width scratch once, before the tag loop, and every read reuses it.
public sealed class GeneratorReaderStackBoundTests
{
    // ───── Harness ────────────────────────────────────────────────────

    private static (CSharpCompilation Output, GeneratorDriverRunResult Run) RunGenerator(string source)
    {
        _ = typeof(Boutquin.Storage.Domain.Attributes.StorageSerializableAttribute).FullName;
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create(
            "ReaderStack_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver
            .Create(new StorageSourceGenerator().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return ((CSharpCompilation)output, driver.GetRunResult());
    }

    private static List<Diagnostic> CompileErrors(CSharpCompilation output) =>
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d => d.ToString()));

    private static Assembly Load(CSharpCompilation output, GeneratorDriverRunResult run)
    {
        var errors = CompileErrors(output);
        errors.Should().BeEmpty("generated code must compile; generator diagnostics:\n"
            + Describe(run.Diagnostics) + "\ncompile errors:\n" + Describe(errors));

        using var ms = new MemoryStream();
        var emit = output.Emit(ms);
        emit.Success.Should().BeTrue(Describe(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        ms.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    // Every stackalloc in a generated reader method whose enclosing statements, up to the method
    // body, include a loop: each such site allocates once per iteration.
    private static List<string> StackallocsInsideReaderLoops(GeneratorDriverRunResult run)
    {
        var hits = new List<string>();
        foreach (var tree in run.GeneratedTrees)
        {
            foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                         .Where(m => m.Identifier.ValueText is "Deserialize" or "__DeserializeUnbounded"))
            {
                foreach (var alloc in method.DescendantNodes()
                             .Where(n => n is StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax))
                {
                    var insideLoop = alloc.Ancestors()
                        .TakeWhile(a => a != method)
                        .Any(a => a is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);
                    if (insideLoop)
                    {
                        hits.Add(method.Identifier.ValueText + ": " + alloc);
                    }
                }
            }
        }

        return hits;
    }

    // ───── Structural: no per-iteration stack allocation in the reader ─

    // Each fixed-width element kind under each collection encoding the reader accepts — packed and
    // [Unpacked] for the packable Tier-1 numerics, count-prefixed for Guid and decimal — alongside a
    // scalar and a nullable field of the same kind, which the tag loop reads once per tag occurrence.
    public static TheoryData<string, string> FixedWidthReaderShapes() => new()
    {
        { "double", "" },
        { "double", "[property: Unpacked] " },
        { "float", "" },
        { "float", "[property: Unpacked] " },
        { "System.Half", "" },
        { "System.Half", "[property: Unpacked] " },
        { "System.Int128", "" },
        { "System.Int128", "[property: Unpacked] " },
        { "System.UInt128", "" },
        { "System.UInt128", "[property: Unpacked] " },
        { "System.Guid", "" },
        { "decimal", "" },
    };

    [Theory]
    [MemberData(nameof(FixedWidthReaderShapes))]
    public void Deserialize_FixedWidthFieldsAndElements_AllocateNoStackInsideTheTagOrElementLoops(string elementType, string encoding)
    {
        var source = $$"""
            using System.Collections.Generic;
            using Boutquin.Storage.Domain.Attributes;
            namespace S;
            [StorageSerializable]
            public partial record struct Bag(
                [property: Tag(1)] {{encoding}}IEnumerable<{{elementType}}> Items,
                [property: Tag(2)] {{elementType}} Single,
                [property: Tag(3)] {{elementType}}? Maybe);
            """;

        var (output, run) = RunGenerator(source);
        CompileErrors(output).Should().BeEmpty(Describe(run.Diagnostics));

        StackallocsInsideReaderLoops(run).Should().BeEmpty(
            "a stackalloc inside the tag or element loop grows the stack once per iteration, so a large collection or a repeated tag overflows it");
    }

    // A record whose only fixed-width state is one scalar field: the tag loop alone repeats the read
    // once per occurrence of the tag in the bytes, which a forger controls.
    [Theory]
    [InlineData("double")]
    [InlineData("float")]
    [InlineData("System.Half")]
    [InlineData("System.Int128")]
    [InlineData("System.UInt128")]
    [InlineData("System.Guid")]
    [InlineData("decimal")]
    public void Deserialize_SingleFixedWidthScalar_AllocatesNoStackInsideTheTagLoop(string fieldType)
    {
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace S;
            [StorageSerializable]
            public partial record One([property: Tag(1)] {{fieldType}} Value);
            """;

        var (output, run) = RunGenerator(source);
        CompileErrors(output).Should().BeEmpty(Describe(run.Diagnostics));

        StackallocsInsideReaderLoops(run).Should().BeEmpty(
            "the tag loop runs the scalar read once per tag occurrence, so a forged repeated tag must not grow the stack");
    }

    // ───── Behavioural: the shared scratch carries each read's own bytes ─

    public static TheoryData<string> FixedWidthTypes() => new()
    {
        "double", "float", "System.Half", "System.Int128", "System.UInt128", "System.Guid", "decimal",
    };

    // One scratch span now serves every fixed-width read in the method — scalar, nullable inner and
    // every element of every encoding. Each read must still surface its own value, never a neighbour's
    // bytes left in the shared buffer.
    [Theory]
    [MemberData(nameof(FixedWidthTypes))]
    public void Deserialize_FixedWidthFields_RoundTripEveryValueThroughTheSharedScratch(string elementType)
    {
        var packable = elementType is not ("System.Guid" or "decimal");
        var unpackedProperty = packable
            ? $"[property: Tag(4)] [property: Unpacked] IEnumerable<{elementType}> Unpacked,"
            : $"[property: Tag(4)] IEnumerable<{elementType}> Unpacked,";
        var source = $$"""
            using System.Collections.Generic;
            using Boutquin.Storage.Domain.Attributes;
            namespace S;
            [StorageSerializable]
            public partial record Bag(
                [property: Tag(1)] {{elementType}} First,
                [property: Tag(2)] {{elementType}}? Maybe,
                [property: Tag(3)] IEnumerable<{{elementType}}> Packed,
                {{unpackedProperty}}
                [property: Tag(5)] {{elementType}} Last);
            """;

        var (output, run) = RunGenerator(source);
        var asm = Load(output, run);
        var t = asm.GetType("S.Bag")!;
        var clr = t.GetProperty("First")!.PropertyType;

        var rng = new Random(20261019);
        Array Values(int n)
        {
            var array = Array.CreateInstance(clr, n);
            for (var i = 0; i < n; i++)
            {
                array.SetValue(RandomValue(clr, rng), i);
            }

            return array;
        }

        var first = RandomValue(clr, rng);
        var maybe = RandomValue(clr, rng);
        var packed = Values(193);
        var unpacked = Values(97);
        var last = RandomValue(clr, rng);
        var original = Activator.CreateInstance(t, first, maybe, packed, unpacked, last)!;

        using var ms = new MemoryStream();
        t.GetMethod("Serialize", [typeof(Stream)])!.Invoke(original, [ms]);
        ms.Position = 0;
        var back = t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [ms])!;

        t.GetProperty("First")!.GetValue(back).Should().Be(first);
        t.GetProperty("Maybe")!.GetValue(back).Should().Be(maybe);
        ((System.Collections.IEnumerable)t.GetProperty("Packed")!.GetValue(back)!).Cast<object>()
            .Should().Equal(packed.Cast<object>());
        ((System.Collections.IEnumerable)t.GetProperty("Unpacked")!.GetValue(back)!).Cast<object>()
            .Should().Equal(unpacked.Cast<object>());
        t.GetProperty("Last")!.GetValue(back).Should().Be(last);
    }

    // A body truncated mid-value must still be refused: a shared scratch holding the previous read's
    // bytes must never stand in for bytes the stream did not supply.
    [Theory]
    [MemberData(nameof(FixedWidthTypes))]
    public void Deserialize_TruncatedFixedWidthBodyAfterAFullOne_IsRefused(string fieldType)
    {
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace S;
            [StorageSerializable]
            public partial record One([property: Tag(1)] {{fieldType}} Value);
            """;

        var (output, run) = RunGenerator(source);
        var asm = Load(output, run);
        var t = asm.GetType("S.One")!;
        var clr = t.GetProperty("Value")!.PropertyType;
        var rng = new Random(77);

        using var genuine = new MemoryStream();
        t.GetMethod("Serialize", [typeof(Stream)])!.Invoke(Activator.CreateInstance(t, RandomValue(clr, rng))!, [genuine]);
        var bytes = genuine.ToArray();

        // Payload = [0x00 0x01] (tag-0 version) + one full tag-1 occurrence + the same tag again
        // with all but the last byte of its body. The single-byte length prefix holds for every
        // fixed width (at most 2 + 2 × 17 bytes).
        var body = bytes.AsSpan(1).ToArray();
        var fieldOccurrence = body.AsSpan(2).ToArray();
        var payload = body.Concat(fieldOccurrence.Take(fieldOccurrence.Length - 1)).ToArray();
        var forged = new byte[] { (byte)payload.Length }.Concat(payload).ToArray();

        var read = () =>
        {
            try
            {
                t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [new MemoryStream(forged)]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        };

        read.Should().Throw<Exception>()
            .Where(e => e is EndOfStreamException || e is InvalidDataException,
                "a truncated fixed-width body is corruption, never a value assembled from stale scratch bytes");
    }

    private static object RandomValue(Type type, Random rng)
    {
        var bytes = new byte[16];
        rng.NextBytes(bytes);
        if (type == typeof(double)) { return rng.NextDouble() * 1e6 - 5e5; }
        if (type == typeof(float)) { return (float)(rng.NextDouble() * 1e4 - 5e3); }
        if (type == typeof(Half)) { return (Half)(rng.NextDouble() * 100 - 50); }
        if (type == typeof(Int128)) { return new Int128(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8)); }
        if (type == typeof(UInt128)) { return new UInt128(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8)); }
        if (type == typeof(Guid)) { return new Guid(bytes); }
        if (type == typeof(decimal)) { return new decimal(rng.Next(), rng.Next(), rng.Next(), rng.Next(2) == 0, (byte)rng.Next(0, 29)); }
        throw new ArgumentOutOfRangeException(nameof(type), type, "no generator for this element type");
    }
}
