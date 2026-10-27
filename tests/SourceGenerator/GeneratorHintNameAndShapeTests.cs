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

// Two contracts both generators must keep for every legal declaration shape:
//   1. Hint names are unique per type identity. Roslyn rejects a duplicate hint name (compared
//      case-insensitively) with an ArgumentException that aborts the whole generator run, so a
//      collision silently drops the generated code of every annotated type in the compilation.
//   2. A shape the emitters cannot turn into compiling code is rejected with a named Error and no
//      emission, never with a compile error inside a generated file.
public sealed class GeneratorHintNameAndShapeTests
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
            "HintShape_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver
            .Create(generators.Select(g => g.AsSourceGenerator()).ToArray())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return ((CSharpCompilation)output, driver.GetRunResult());
    }

    private static List<Diagnostic> CompileErrors(CSharpCompilation output) =>
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d => d.ToString()));

    private static string DescribeRun(GeneratorDriverRunResult run) =>
        string.Join("\n", run.Results.Select(r => r.Exception?.Message ?? "no exception"))
        + "\n" + Describe(run.Diagnostics);

    private static Assembly Load(CSharpCompilation output)
    {
        using var ms = new MemoryStream();
        var emit = output.Emit(ms);
        emit.Success.Should().BeTrue(Describe(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        ms.Position = 0;
        return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    // ───── Row generator hint names ───────────────────────────────────

    [Fact]
    public void RowGenerator_SameNestedNameUnderGenericAndNonGenericParent_GeneratesEveryType()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            public partial class Outer
            {
                [StorageSerializable]
                public partial record struct Row([property: Tag(1)] int Value);
            }
            public partial class Outer<T>
            {
                [StorageSerializable]
                public partial record struct Row([property: Tag(1)] int Value);
            }
            [StorageSerializable]
            public partial record struct Bystander([property: Tag(1)] int Value);
            """;

        var (output, run) = RunGenerators(source, new StorageSourceGenerator());

        run.Results.Single().Exception.Should().BeNull("Outer.Row and Outer<T>.Row are distinct types and need distinct hint names; got:\n" + DescribeRun(run));
        run.GeneratedTrees.Length.Should().Be(3, "every annotated type, including the unrelated Bystander, must get generated output");
        CompileErrors(output).Should().BeEmpty(Describe(CompileErrors(output)));
        var asm = Load(output);
        foreach (var name in new[] { "P.Outer+Row", "P.Outer`1+Row", "P.Bystander" })
        {
            asm.GetType(name)!.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)
                .Should().NotBeNull($"{name} must carry its generated Deserialize");
        }
    }

    [Fact]
    public void RowGenerator_TypeNamesDifferingOnlyInCase_GenerateEveryType()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record struct Row([property: Tag(1)] int Value);
            [StorageSerializable]
            public partial record struct row([property: Tag(1)] int Value);
            [StorageSerializable]
            public partial record struct Bystander([property: Tag(1)] int Value);
            """;

        var (output, run) = RunGenerators(source, new StorageSourceGenerator());

        run.Results.Single().Exception.Should().BeNull("Row and row are distinct C# types; hint names compare case-insensitively; got:\n" + DescribeRun(run));
        run.GeneratedTrees.Length.Should().Be(3);
        CompileErrors(output).Should().BeEmpty(Describe(CompileErrors(output)));
        var asm = Load(output);
        foreach (var name in new[] { "P.Row", "P.row", "P.Bystander" })
        {
            asm.GetType(name)!.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)
                .Should().NotBeNull($"{name} must carry its generated Deserialize");
        }
    }

    // ───── Columnar generator hint names ──────────────────────────────

    [Fact]
    public void ColumnarGenerator_SameNestedNameUnderGenericAndNonGenericParent_GeneratesEveryType()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            public partial class Outer
            {
                [ColumnarStorable]
                public partial record struct Row(int Value);
            }
            public partial class Outer<T>
            {
                [ColumnarStorable]
                public partial record struct Row(int Value);
            }
            [ColumnarStorable]
            public partial record struct Bystander(int Value);
            """;

        var (output, run) = RunGenerators(source, new ColumnarSourceGenerator());

        run.Results.Single().Exception.Should().BeNull("Outer.Row and Outer<T>.Row need distinct columnar hint names; got:\n" + DescribeRun(run));
        run.GeneratedTrees.Length.Should().Be(3, "every [ColumnarStorable] type, including the unrelated Bystander, must get generated output");
        CompileErrors(output).Should().BeEmpty(Describe(CompileErrors(output)));
        var asm = Load(output);
        foreach (var name in new[] { "P.Outer+Row", "P.Outer`1+Row", "P.Bystander" })
        {
            asm.GetType(name)!.GetProperty("ColumnarFieldPlan", BindingFlags.Public | BindingFlags.Static)
                .Should().NotBeNull($"{name} must carry its generated ColumnarFieldPlan");
        }
    }

    [Fact]
    public void ColumnarGenerator_TypeNamesDifferingOnlyInCase_GenerateEveryType()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [ColumnarStorable]
            public partial record struct Bar(int Value);
            [ColumnarStorable]
            public partial record struct bar(int Value);
            [ColumnarStorable]
            public partial record struct Bystander(int Value);
            """;

        var (output, run) = RunGenerators(source, new ColumnarSourceGenerator());

        run.Results.Single().Exception.Should().BeNull("Bar and bar are distinct C# types; hint names compare case-insensitively; got:\n" + DescribeRun(run));
        run.GeneratedTrees.Length.Should().Be(3);
        CompileErrors(output).Should().BeEmpty(Describe(CompileErrors(output)));
        var asm = Load(output);
        foreach (var name in new[] { "P.Bar", "P.bar", "P.Bystander" })
        {
            asm.GetType(name)!.GetProperty("ColumnarFieldPlan", BindingFlags.Public | BindingFlags.Static)
                .Should().NotBeNull($"{name} must carry its generated ColumnarFieldPlan");
        }
    }

    // ───── Non-record [Key] GetHashCode arity ─────────────────────────

    private static string NonRecordKeyStruct(int propertyCount)
    {
        var names = Enumerable.Range(1, propertyCount).Select(i => $"P{i}").ToList();
        var parameters = string.Join(", ", names.Select(n => "int " + n.ToLowerInvariant()));
        var assignments = string.Join(" ", names.Select(n => $"{n} = {n.ToLowerInvariant()};"));
        var declarations = string.Join("\n", names.Select((n, i) => $"    [Tag({i + 1})] public int {n} {{ get; }}"));
        return $$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [Key]
            public partial struct Wide
            {
                public Wide({{parameters}}) { {{assignments}} }
            {{declarations}}
            }
            """;
    }

    [Fact]
    public void NonRecordKey_NineProperties_CompilesAndHashAgreesWithEquality()
    {
        var (output, _) = RunGenerators(NonRecordKeyStruct(9), new StorageSourceGenerator());

        CompileErrors(output).Should().BeEmpty("a nine-property non-record key must compile; got:\n" + Describe(CompileErrors(output)));
        var type = Load(output).GetType("P.Wide")!;
        object Make(int last) => Activator.CreateInstance(type, 1, 2, 3, 4, 5, 6, 7, 8, last)!;
        var a = Make(9);
        var b = Make(9);
        var c = Make(10);
        a.Equals(b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode(), "equal keys must hash equally");
        a.Equals(c).Should().BeFalse("the ninth component participates in equality");
    }

    // ───── Nullable-annotated nested record ───────────────────────────

    private const string NullableNestedRecord = """
        #nullable enable
        using Boutquin.Storage.Domain.Attributes;
        namespace P;
        [StorageSerializable]
        public partial record Child([property: Tag(1)] int V);
        [StorageSerializable]
        public partial record Parent([property: Tag(1)] int Id, [property: Tag(2)] Child? Inner);
        """;

    [Fact]
    public void StorageSerializable_NullableAnnotatedNestedRecord_RoundTripsNullAndValue()
    {
        var (output, _) = RunGenerators(NullableNestedRecord, new StorageSourceGenerator());

        CompileErrors(output).Should().BeEmpty("Child? is a documented shape (absent tag => null); got:\n" + Describe(CompileErrors(output)));
        var asm = Load(output);
        var parentType = asm.GetType("P.Parent")!;
        var childType = asm.GetType("P.Child")!;
        object RoundTrip(object value)
        {
            using var ms = new MemoryStream();
            parentType.GetMethod("Serialize")!.Invoke(value, [ms]);
            ms.Position = 0;
            return parentType.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [ms])!;
        }

        var withNull = Activator.CreateInstance(parentType, 7, null)!;
        RoundTrip(withNull).Should().Be(withNull, "a null nested record must round-trip as null");
        var withChild = Activator.CreateInstance(parentType, 8, Activator.CreateInstance(childType, 42))!;
        RoundTrip(withChild).Should().Be(withChild, "a present nested record must round-trip by value");
    }

    [Fact]
    public void StorageSerializable_CollectionOfNullableAnnotatedRecords_IsRejectedWithBssg001()
    {
        const string source = """
            #nullable enable
            using System.Collections.Generic;
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record Child([property: Tag(1)] int V);
            [StorageSerializable]
            public partial record Holder([property: Tag(1)] IEnumerable<Child?> Items);
            """;

        var (output, run) = RunGenerators(source, new StorageSourceGenerator());

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG001" && d.Severity == DiagnosticSeverity.Error && d.GetMessage(null).Contains("Items", StringComparison.Ordinal),
            "collection elements carry no null discriminator; got:\n" + Describe(run.Diagnostics) + "\n" + Describe(CompileErrors(output)));
        CompileErrors(output).Should().BeEmpty("the rejected type must not reach the consumer compilation as broken code");
    }

    // ───── Collection property declared types ─────────────────────────

    [Theory]
    [InlineData("List<int>")]
    [InlineData("IReadOnlyList<string>")]
    [InlineData("HashSet<int>")]
    public void StorageSerializable_CollectionDeclaredAsOtherThanIEnumerable_IsRejectedWithBssg001(string declaredType)
    {
        var source = $$"""
            using System.Collections.Generic;
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record struct WithCollection([property: Tag(1)] {{declaredType}} Items);
            """;

        var (output, run) = RunGenerators(source, new StorageSourceGenerator());

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG001" && d.Severity == DiagnosticSeverity.Error,
            $"Deserialize decodes into IEnumerable<T>, which does not convert to {declaredType}; got:\n" + Describe(run.Diagnostics) + "\n" + Describe(CompileErrors(output)));
        CompileErrors(output).Should().BeEmpty("the rejected type must not reach the consumer compilation as broken code");
    }

    [Fact]
    public void StorageSerializable_CollectionDeclaredAsIEnumerable_StillGeneratesAndRoundTrips()
    {
        const string source = """
            using System.Collections.Generic;
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record WithCollection([property: Tag(1)] IEnumerable<int> Items);
            """;

        var (output, run) = RunGenerators(source, new StorageSourceGenerator());

        run.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        CompileErrors(output).Should().BeEmpty(Describe(CompileErrors(output)));
        var type = Load(output).GetType("P.WithCollection")!;
        var value = Activator.CreateInstance(type, new object[] { new[] { 3, 1, 2 } })!;
        using var ms = new MemoryStream();
        type.GetMethod("Serialize")!.Invoke(value, [ms]);
        ms.Position = 0;
        var back = type.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [ms])!;
        ((IEnumerable<int>)type.GetProperty("Items")!.GetValue(back)!).Should().Equal(3, 1, 2);
    }

    // ───── Get-only properties under the generated ProjectTo ──────────

    [Theory]
    [InlineData("record struct")]
    [InlineData("struct")]
    public void StorageSerializable_GetOnlyPropertySetByConstructor_IsRejectedWithBssg025(string kind)
    {
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial {{kind}} ReadOnlyShape
            {
                public ReadOnlyShape(int id) { Id = id; }
                [Tag(1)] public int Id { get; }
            }
            """;

        var (output, run) = RunGenerators(source, new StorageSourceGenerator());

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG025" && d.Severity == DiagnosticSeverity.Error && d.GetMessage(null).Contains("Id", StringComparison.Ordinal),
            "ProjectTo assigns every serialized property with 'with', which a get-only property refuses; got:\n" + Describe(run.Diagnostics) + "\n" + Describe(CompileErrors(output)));
        CompileErrors(output).Should().BeEmpty("the rejected type must not reach the consumer compilation as broken code");
    }

    [Fact]
    public void StorageSerializable_GetOnlyPropertyWithHandWrittenProjectTo_StillGenerates()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using Boutquin.Storage.Domain.Interfaces;
            using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
            namespace P;
            [StorageSerializable]
            public partial record struct ReadOnlyShape : IProjectable<ReadOnlyShape>
            {
                public ReadOnlyShape(int id) { Id = id; }
                [Tag(1)] public int Id { get; }
                public ReadOnlyShape ProjectTo(ColumnProjection projection) => this;
            }
            """;

        var (output, run) = RunGenerators(source, new StorageSourceGenerator());

        run.Diagnostics.Should().NotContain(d => d.Id == "BSSG025", "no generated ProjectTo assigns the property");
        CompileErrors(output).Should().BeEmpty(Describe(CompileErrors(output)));
    }

    // ───── Non-partial containing type ────────────────────────────────

    [Fact]
    public void RowGenerator_NestedTargetInNonPartialContainingType_IsRejectedWithBssg026()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            public class Outer
            {
                [StorageSerializable]
                public partial record struct Row([property: Tag(1)] int Value);
            }
            [StorageSerializable]
            public partial record struct Bystander([property: Tag(1)] int Value);
            """;

        var (output, run) = RunGenerators(source, new StorageSourceGenerator());

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG026" && d.Severity == DiagnosticSeverity.Error && d.GetMessage(null).Contains("Outer", StringComparison.Ordinal),
            "re-opening a non-partial containing type declares it twice; got:\n" + Describe(run.Diagnostics) + "\n" + Describe(CompileErrors(output)));
        CompileErrors(output).Should().BeEmpty("the rejected type must not reach the consumer compilation as broken code");
        run.GeneratedTrees.Length.Should().Be(1, "the unrelated Bystander still generates");
    }

    [Fact]
    public void ColumnarGenerator_NestedTargetInNonPartialContainingType_IsRejectedWithBssg026()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            public class Outer
            {
                public struct Mid
                {
                    [ColumnarStorable]
                    public partial record struct Bar(int Value);
                }
            }
            [ColumnarStorable]
            public partial record struct Bystander(int Value);
            """;

        var (output, run) = RunGenerators(source, new ColumnarSourceGenerator());

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG026" && d.Severity == DiagnosticSeverity.Error && d.GetMessage(null).Contains("Outer", StringComparison.Ordinal),
            "re-opening a non-partial containing type declares it twice; got:\n" + Describe(run.Diagnostics) + "\n" + Describe(CompileErrors(output)));
        CompileErrors(output).Should().BeEmpty("the rejected type must not reach the consumer compilation as broken code");
        run.GeneratedTrees.Length.Should().Be(1, "the unrelated Bystander still generates");
    }
}
