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

// Two contracts the generators owe every annotated type they accept.
//
// 1. Stack use is bounded by the schema, not by the data. `stackalloc` memory is released only
//    when the method returns, so a scratch buffer allocated inside a per-element loop grows the
//    stack once per element: a 100 000-element collection of doubles or Guids then kills the
//    process with an uncatchable stack overflow. Generated code allocates its fixed-width scratch
//    once, outside every loop.
// 2. Every piece of public instance state is either persisted or rejected. The generators plan
//    public instance properties; a public instance field (declared or inherited) is not a
//    property, so it would silently round-trip as default. BSSG024 rejects it with a located
//    Error and no code is emitted for the type.
public sealed class GeneratorFieldDiscoveryAndStackBoundTests
{
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
            "FieldStack_" + Guid.NewGuid().ToString("N"),
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

    // Every stackalloc in a generated method named in methodNames whose enclosing statements, up to
    // the method body, include a loop: each such site allocates once per iteration.
    private static List<string> StackallocsInsideLoops(GeneratorDriverRunResult run, params string[] methodNames)
    {
        var hits = new List<string>();
        foreach (var tree in run.GeneratedTrees)
        {
            foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                         .Where(m => methodNames.Contains(m.Identifier.ValueText)))
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

    // ───── 1. Writer stack use per collection element ─────────────────

    // Each element type whose body the writer formats through a fixed-width scratch buffer, under
    // each collection encoding it can take: packed (the default for Tier-1 numerics), [Unpacked],
    // and count-prefixed (Guid and decimal are never packable).
    public static TheoryData<string, string> FixedWidthCollectionShapes() => new()
    {
        { "double", "" },
        { "double", "[property: Unpacked] " },
        { "float", "" },
        { "float", "[property: Unpacked] " },
        { "System.Half", "" },
        { "System.Int128", "" },
        { "System.UInt128", "[property: Unpacked] " },
        { "System.Guid", "" },
        { "decimal", "" },
    };

    [Theory]
    [MemberData(nameof(FixedWidthCollectionShapes))]
    public void Serialize_FixedWidthCollectionElements_AllocateNoStackInsideTheElementLoop(string elementType, string encoding)
    {
        var source = $$"""
            using System.Collections.Generic;
            using Boutquin.Storage.Domain.Attributes;
            namespace S;
            [StorageSerializable]
            public partial record struct Bag(
                [property: Tag(1)] {{encoding}}IEnumerable<{{elementType}}> Items,
                [property: Tag(2)] {{elementType}} Single);
            """;

        var (output, run) = RunGenerators(source, new StorageSourceGenerator());
        CompileErrors(output).Should().BeEmpty(Describe(run.Diagnostics));

        StackallocsInsideLoops(run, "Serialize", "__SerializeUnbounded").Should().BeEmpty(
            "a stackalloc inside the per-element loop grows the stack once per element, so a large collection overflows it");
    }

    public static TheoryData<string> RoundTripElementTypes() => new()
    {
        "double", "float", "System.Half", "System.Int128", "System.UInt128", "System.Guid", "decimal",
    };

    // The hoisted scratch buffer is reused for every element; each element must still carry its
    // own value through the wire.
    [Theory]
    [MemberData(nameof(RoundTripElementTypes))]
    public void Serialize_FixedWidthCollection_RoundTripsEveryElementThroughTheReusedScratchBuffer(string elementType)
    {
        var source = $$"""
            using System.Collections.Generic;
            using Boutquin.Storage.Domain.Attributes;
            namespace S;
            [StorageSerializable]
            public partial record Bag(
                [property: Tag(1)] IEnumerable<{{elementType}}> Packed,
                [property: Tag(2)] [property: Unpacked] IEnumerable<{{elementType}}> Unpacked);
            """;

        var (output, run) = RunGenerators(source, new StorageSourceGenerator());
        var asm = Load(output, run);
        var t = asm.GetType("S.Bag")!;
        var elementClr = t.GetProperty("Packed")!.PropertyType.GetGenericArguments()[0];

        var rng = new Random(20261019);
        Array Values(int n)
        {
            var array = Array.CreateInstance(elementClr, n);
            for (var i = 0; i < n; i++)
            {
                array.SetValue(RandomValue(elementClr, rng), i);
            }

            return array;
        }

        var packed = Values(257);
        var unpacked = Values(131);
        var original = Activator.CreateInstance(t, packed, unpacked)!;
        using var ms = new MemoryStream();
        t.GetMethod("Serialize", [typeof(Stream)])!.Invoke(original, [ms]);
        ms.Position = 0;
        var back = t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [ms])!;

        ((System.Collections.IEnumerable)t.GetProperty("Packed")!.GetValue(back)!).Cast<object>()
            .Should().Equal(packed.Cast<object>());
        ((System.Collections.IEnumerable)t.GetProperty("Unpacked")!.GetValue(back)!).Cast<object>()
            .Should().Equal(unpacked.Cast<object>());
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

    // ───── 2. Public instance fields ──────────────────────────────────

    private static void AssertRejectedWithBssg024(string source, IIncrementalGenerator generator, string fieldName)
    {
        var (output, run) = RunGenerators(source, generator);
        var compileErrors = CompileErrors(output);

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG024" && d.Severity == DiagnosticSeverity.Error
                 && d.GetMessage(null).Contains(fieldName, StringComparison.Ordinal),
            "a public instance field is not planned, so it would round-trip as default; got generator diagnostics:\n"
            + Describe(run.Diagnostics) + "\nand compile errors:\n" + Describe(compileErrors));
        run.GeneratedTrees.Should().BeEmpty("a rejected type gets no emitted code");
        compileErrors.Should().BeEmpty("rejection must not leave uncompilable generated code behind");
    }

    public static TheoryData<string, string> RowAndKeyTypesWithPublicFields() => new()
    {
        // A classic field struct: no properties at all, so CompareTo/Equals/Serialize would cover nothing.
        { "[Key] public partial struct FieldKey { public int A; public int B; }", "A" },
        { "[StorageSerializable] public partial record struct FieldRow { public double Price; public int Qty; }", "Price" },
        { "[StorageSerializable] public partial record struct Rec([property: Tag(1)] int A) { public int B; }", "B" },
        { "[Key] public partial record struct K([property: Tag(1)] int A) { public int B; }", "B" },
        { "[StorageSerializable] public partial record Rec([property: Tag(1)] int A) { public readonly int B; }", "B" },
        { "[StorageSerializable] public partial record Rec([property: Tag(1)] int A) { public required int B; }", "B" },
        // Inherited: the base record's public field is invisible to the derived type's discovery.
        { "public record Base { public int X; } [StorageSerializable] public partial record Rec([property: Tag(1)] int A) : Base;", "X" },
    };

    [Theory]
    [MemberData(nameof(RowAndKeyTypesWithPublicFields))]
    public void KeyAndStorageSerializable_PublicInstanceField_IsRejectedWithBssg024(string declaration, string fieldName)
    {
        AssertRejectedWithBssg024(
            "using Boutquin.Storage.Domain.Attributes;\nnamespace F;\n" + declaration,
            new StorageSourceGenerator(),
            fieldName);
    }

    public static TheoryData<string, string> ColumnarTypesWithPublicFields() => new()
    {
        { "[ColumnarStorable] public partial record Row { public System.DateOnly Date { get; init; } public double Close; }", "Close" },
        { "[ColumnarStorable] public partial record ColRow(System.DateOnly Date) { public double Close; }", "Close" },
        { "[ColumnarStorable] public partial record struct Row(System.DateOnly Date) { public readonly double Close; }", "Close" },
        { "[ColumnarStorable] public partial record Row { public required System.DateOnly Date { get; init; } public required double Close; }", "Close" },
        { "public record Base { public double X; } [ColumnarStorable] public partial record Row(System.DateOnly Date) : Base;", "X" },
    };

    [Theory]
    [MemberData(nameof(ColumnarTypesWithPublicFields))]
    public void ColumnarStorable_PublicInstanceField_IsRejectedWithBssg024(string declaration, string fieldName)
    {
        AssertRejectedWithBssg024(
            "using Boutquin.Storage.Domain.Attributes;\nnamespace F;\n" + declaration,
            new ColumnarSourceGenerator(),
            fieldName);
    }

    // Static and constant fields are type state, not row state, and non-public fields are outside
    // the public-member contract every generator plans — none of them is rejected.
    [Fact]
    public void StaticConstantAndNonPublicFields_AreNotRejected()
    {
        var (rowOutput, rowRun) = RunGenerators("""
            using Boutquin.Storage.Domain.Attributes;
            namespace F;
            [StorageSerializable]
            public partial record Rec([property: Tag(1)] int A)
            {
                public static int Shared;
                public const int Limit = 3;
                private int _cache;
                internal int Scratch;
                public int Peek() => _cache + Scratch;
            }
            """, new StorageSourceGenerator());
        rowRun.Diagnostics.Should().NotContain(d => d.Id == "BSSG024", Describe(rowRun.Diagnostics));
        Load(rowOutput, rowRun);

        var (colOutput, colRun) = RunGenerators("""
            using Boutquin.Storage.Domain.Attributes;
            namespace F;
            [ColumnarStorable]
            public partial record Row(System.DateOnly Date, double Close)
            {
                public static int Shared;
                public const int Limit = 3;
                private int _cache;
                public int Peek() => _cache;
            }
            """, new ColumnarSourceGenerator());
        colRun.Diagnostics.Should().NotContain(d => d.Id == "BSSG024", Describe(colRun.Diagnostics));
        Load(colOutput, colRun);
    }
}
