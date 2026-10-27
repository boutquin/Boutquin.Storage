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
namespace Boutquin.Storage.SourceGenerator.Tests;

// Construction-shape contract for the row generator. The generated Deserialize rebuilds the
// value with a positional constructor call over every serialized property, and the generated
// ProjectTo rebuilds it with `with` expressions. A declaration that offers neither must be
// rejected with BSSG025 and get no emitted code — never emitted code that fails to compile
// (CS1729 for a missing constructor, CS8858 for `with` on a non-record class).
public sealed class GeneratorConstructorShapeTests
{
    private static (CSharpCompilation Output, GeneratorDriverRunResult Run) RunGenerator(string source)
    {
        // Real Domain attributes and interfaces, so the generated code is compiled against the
        // same surface a consumer project sees.
        _ = typeof(Boutquin.Storage.Domain.Attributes.StorageSerializableAttribute).FullName;
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create(
            "CtorShape_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver
            .Create(new StorageSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return ((CSharpCompilation)output, driver.GetRunResult());
    }

    private static List<Diagnostic> CompileErrors(CSharpCompilation output) =>
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d => d.ToString()));

    private static void AssertRejected(string source)
    {
        var (output, run) = RunGenerator(source);
        var compileErrors = CompileErrors(output);

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG025" && d.Severity == DiagnosticSeverity.Error,
            "the generated code cannot construct this type; got: " + Describe(compileErrors));
        run.GeneratedTrees.Should().BeEmpty("a rejected type gets no emitted code");
        compileErrors.Should().BeEmpty("rejection must not leave uncompilable generated code behind");
    }

    private static void AssertGeneratedAndCompiles(string source)
    {
        var (output, run) = RunGenerator(source);

        run.Diagnostics.Should().NotContain(d => d.Id == "BSSG025");
        run.GeneratedTrees.Should().NotBeEmpty();
        CompileErrors(output).Should().BeEmpty(Describe(CompileErrors(output)));
    }

    // ───── Rejected shapes ────────────────────────────────────────────

    [Fact]
    public void StorageSerializable_BodyPropertyRecordStruct_WithoutMatchingConstructor_IsRejected()
    {
        AssertRejected("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record struct BodyRec
            {
                [Tag(1)] public int Id { get; init; }
                [Tag(2)] public string Name { get; init; }
            }
            """);
    }

    [Fact]
    public void Key_BodyPropertyRecordStruct_WithoutMatchingConstructor_IsRejected()
    {
        AssertRejected("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [Key]
            public partial record struct BodyKey
            {
                [Tag(1)] public long Id { get; init; }
            }
            """);
    }

    [Fact]
    public void StorageSerializable_Constructor_WithWrongParameterTypes_IsRejected()
    {
        // Two parameters exist, but not in the property order: `new T(_Id, _Name)` cannot bind.
        AssertRejected("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record struct Swapped
            {
                public Swapped(string name, int id) { Name = name; Id = id; }
                [Tag(1)] public int Id { get; init; }
                [Tag(2)] public string Name { get; init; }
            }
            """);
    }

    [Fact]
    public void StorageSerializable_Constructor_WithSameTypesInAnotherOrder_IsRejected()
    {
        // `new T(_First, _Second)` compiles against (second, first) because both are ints, but it
        // swaps the two values on every read. Parameters are matched to properties by name.
        AssertRejected("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record struct SameTypes
            {
                public SameTypes(int second, int first) { Second = second; First = first; }
                [Tag(1)] public int First { get; init; }
                [Tag(2)] public int Second { get; init; }
            }
            """);
    }

    [Fact]
    public void StorageSerializable_PlainClass_IsRejected_EvenWithMatchingConstructor()
    {
        // Deserialize can bind the constructor, but the generated ProjectTo seeds its result with
        // `this with { ... }`, which C# allows only on records and structs (CS8858).
        AssertRejected("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial class PlainClass
            {
                public PlainClass(string name) { Name = name; }
                [Tag(1)] public string Name { get; set; }
            }
            """);
    }

    // ───── Accepted shapes (controls) ─────────────────────────────────

    [Fact]
    public void StorageSerializable_BodyPropertyRecordStruct_WithMatchingConstructor_Compiles()
    {
        AssertGeneratedAndCompiles("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record struct BodyRecCtor
            {
                public BodyRecCtor(int id, string name) { Id = id; Name = name; }
                [Tag(1)] public int Id { get; init; }
                [Tag(2)] public string Name { get; init; }
            }
            """);
    }

    [Fact]
    public void StorageSerializable_Constructor_WithTrailingOptionalParameter_Compiles()
    {
        AssertGeneratedAndCompiles("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record struct WithOptional
            {
                public WithOptional(int id, string? note = null) { Id = id; }
                [Tag(1)] public int Id { get; init; }
            }
            """);
    }

    [Fact]
    public void StorageSerializable_PlainStruct_WithMatchingConstructor_Compiles()
    {
        AssertGeneratedAndCompiles("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial struct PlainStruct
            {
                public PlainStruct(string name) { Name = name; }
                [Tag(1)] public string Name { get; set; }
            }
            """);
    }

    [Fact]
    public void Positional_RecordClass_And_RecordStruct_Compile()
    {
        AssertGeneratedAndCompiles("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record PosClass([property: Tag(1)] int Id, [property: Tag(2)] string Name);
            [Key]
            public partial record struct PosKey(long Id, int Sub);
            """);
    }
}
