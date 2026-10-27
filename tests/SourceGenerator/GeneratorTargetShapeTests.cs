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
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Boutquin.Storage.SourceGenerator.Tests;

// Target-shape contract for both generators: every annotated declaration shape is either
// generated into code that compiles (and behaves), or rejected with a named Error diagnostic
// and no emission. The failure mode these pin is the third outcome — the generator accepts
// the shape and emits code that does not compile, or that silently drops state, with at most
// a warning (CS8785 / BSSG003) as the signal.
public sealed class GeneratorTargetShapeTests
{
    // ───── Harness ────────────────────────────────────────────────────

    private static (CSharpCompilation Output, GeneratorDriverRunResult Run) RunGenerators(
        string source,
        params IIncrementalGenerator[] generators)
    {
        // Real Domain attributes and interfaces (no test-local mirrors), so the generated code
        // is compiled against the same surface a consumer project sees.
        _ = typeof(Boutquin.Storage.Domain.Attributes.ColumnarStorableAttribute).FullName;
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create(
            "Shape_" + Guid.NewGuid().ToString("N"),
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

    // Name of the method declaration that contains an error in a generated tree, or "".
    private static string EnclosingMethod(Diagnostic d)
    {
        var tree = d.Location.SourceTree;
        if (tree is null)
        {
            return "";
        }

        var node = tree.GetRoot().FindNode(d.Location.SourceSpan);
        return node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "";
    }

    private static Assembly Load(CSharpCompilation output)
    {
        using var ms = new MemoryStream();
        var emit = output.Emit(ms);
        emit.Success.Should().BeTrue(Describe(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        ms.Position = 0;
        return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    // ───── Inherited public state (row generator) ─────────────────────

    [Fact]
    public void StorageSerializable_RecordClass_InheritingPublicState_IsRejected_WithError()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            public record BaseRec
            {
                public int BaseValue { get; init; }
            }
            [StorageSerializable]
            public partial record DerivedRec : BaseRec
            {
                public DerivedRec(int ownValue) { OwnValue = ownValue; }
                [Tag(2)] public int OwnValue { get; init; }
            }
            """;

        var (_, run) = RunGenerators(source, new StorageSourceGenerator());

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG024" && d.Severity == DiagnosticSeverity.Error,
            "a type whose inherited public state the wire plan cannot see must fail the build, not round-trip it to default");
        run.GeneratedTrees.Should().NotContain(t => t.FilePath.EndsWith("DerivedRec.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void StorageSerializable_FlatRecordClass_IsNotFlaggedAsInheriting()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record FlatRec([property: Tag(1)] int BaseValue, [property: Tag(2)] int OwnValue);
            """;

        var (output, run) = RunGenerators(source, new StorageSourceGenerator());

        run.Diagnostics.Should().NotContain(d => d.Id == "BSSG024");
        CompileErrors(output).Should().BeEmpty();
    }

    // ───── Inherited public state (columnar generator) ────────────────

    [Fact]
    public void ColumnarStorable_RecordInheritingSettableState_IsRejected_WithError()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            public record BaseQuote
            {
                public long Volume { get; init; }
            }
            [ColumnarStorable]
            public partial record Quote : BaseQuote
            {
                public System.DateOnly Date { get; init; }
            }
            """;

        var (_, run) = RunGenerators(source, new ColumnarSourceGenerator());

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG024" && d.Severity == DiagnosticSeverity.Error,
            "an inherited settable property has no column and would rehydrate as default(T)");
        run.GeneratedTrees.Should().BeEmpty();
    }

    // ───── Generic annotated targets ──────────────────────────────────

    [Fact]
    public void StorageSerializable_GenericTarget_IsRejected_WithError()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record struct Box<T>([property: Tag(1)] int Value);
            """;

        var (output, run) = RunGenerators(source, new StorageSourceGenerator());

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG023" && d.Severity == DiagnosticSeverity.Error,
            "a generic target would be emitted as a separate non-generic partial; got:\n" + Describe(CompileErrors(output)));
        run.GeneratedTrees.Should().BeEmpty();
        CompileErrors(output).Should().BeEmpty("no generated code may reach the consumer compilation");
    }

    [Fact]
    public void ColumnarStorable_GenericTarget_IsRejected_WithError()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [ColumnarStorable]
            public partial record struct Series<T>(System.DateOnly Date, long Volume);
            """;

        var (output, run) = RunGenerators(source, new ColumnarSourceGenerator());

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG023" && d.Severity == DiagnosticSeverity.Error,
            "a generic columnar target would be emitted as a separate non-generic partial; got:\n" + Describe(CompileErrors(output)));
        run.GeneratedTrees.Should().BeEmpty();
    }

    // ───── Columnar hint-name identity ────────────────────────────────

    [Fact]
    public void ColumnarStorable_SameSimpleNameInTwoNamespaces_BothGenerate()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace One { [ColumnarStorable] public partial record struct Bar { public DateOnly Date { get; init; } public double Close { get; init; } } }
            namespace Two { [ColumnarStorable] public partial record struct Bar { public DateOnly Date { get; init; } public double Close { get; init; } } }
            """;

        var (output, run) = RunGenerators(source, new ColumnarSourceGenerator());

        run.Results.Should().AllSatisfy(r => r.Exception.Should().BeNull("hint names must be unique per type identity"));
        run.GeneratedTrees.Length.Should().Be(2, "both namespace-distinct [ColumnarStorable] types must get generated output");
        CompileErrors(output).Should().BeEmpty();
    }

    // ───── Columnar positional record class ───────────────────────────

    private const string PositionalColumnarRecordClass = """
        using System;
        using Boutquin.Storage.Domain.Attributes;
        namespace P;
        [ColumnarStorable]
        public partial record Quote(DateOnly Date, long Volume);
        """;

    [Fact]
    public void ColumnarStorable_PositionalRecordClass_Rehydrate_CompilesAndPopulatesColumns()
    {
        var (output, _) = RunGenerators(PositionalColumnarRecordClass, new ColumnarSourceGenerator());

        var rehydrateErrors = CompileErrors(output).Where(d => EnclosingMethod(d) == "Rehydrate").ToList();
        rehydrateErrors.Should().BeEmpty("Rehydrate must not name a constructor the type does not have; got:\n" + Describe(rehydrateErrors));

        var asm = Load(output);
        var t = asm.GetType("P.Quote")!;
        var columns = new Dictionary<string, Array>
        {
            ["Date"] = new[] { new DateOnly(2026, 1, 2), new DateOnly(2026, 3, 4) },
            ["Volume"] = new[] { 11L, 22L },
        };
        var row = t.GetMethod("Rehydrate", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [columns, 1])!;
        t.GetProperty("Date")!.GetValue(row).Should().Be(new DateOnly(2026, 3, 4));
        t.GetProperty("Volume")!.GetValue(row).Should().Be(22L);
    }

    [Fact]
    public void ColumnarStorable_PositionalRecordClass_ProjectTo_CompilesAndClipsUnprojectedFields()
    {
        var (output, _) = RunGenerators(PositionalColumnarRecordClass, new ColumnarSourceGenerator());

        var projectErrors = CompileErrors(output).Where(d => EnclosingMethod(d) == "ProjectTo").ToList();
        projectErrors.Should().BeEmpty("ProjectTo must not name a constructor the type does not have; got:\n" + Describe(projectErrors));

        var asm = Load(output);
        var t = asm.GetType("P.Quote")!;
        var original = Activator.CreateInstance(t, new DateOnly(2026, 5, 6), 99L)!;
        var projection = new Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnProjection(["Volume"]);
        var projected = t.GetMethod("ProjectTo")!.Invoke(original, [projection])!;
        t.GetProperty("Volume")!.GetValue(projected).Should().Be(99L);
        t.GetProperty("Date")!.GetValue(projected).Should().Be(default(DateOnly), "an unprojected field is clipped to default");
    }

    // ───── Columnar nested target ─────────────────────────────────────

    [Fact]
    public void ColumnarStorable_NestedTarget_GeneratesInsideContainingType()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            public partial class Outer
            {
                [ColumnarStorable]
                public partial record struct Row(int Value);
            }
            """;

        var (output, _) = RunGenerators(source, new ColumnarSourceGenerator());

        var errors = CompileErrors(output);
        errors.Should().BeEmpty("the nested target's generated partial must extend Outer.Row, not a phantom top-level Row; got:\n" + Describe(errors));
        var asm = Load(output);
        var plan = asm.GetType("P.Outer+Row")!.GetProperty("ColumnarFieldPlan", BindingFlags.Public | BindingFlags.Static);
        plan.Should().NotBeNull("Outer.Row itself must carry the generated plan");
    }
}
