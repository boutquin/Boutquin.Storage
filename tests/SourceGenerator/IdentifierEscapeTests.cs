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

// Identifier-escaping tests. A user may name a property with a verbatim C# keyword (e.g.
// `@class`, `@event`). The semantic model strips the leading `@`, so IPropertySymbol.Name is the
// bare keyword "class". Every emitter interpolates that name straight into identifier positions in
// the generated source (`this.class`, `case "class":`, ...). Without re-escaping, the generated
// code is uncompilable C#. These tests compile the generator output for each pipeline and assert
// it is error-free — the emitter must round-trip keyword-named identifiers through @-escaping.
public sealed class IdentifierEscapeTests
{
    // CodeGenerator + TaggedWireEmitter + ProjectionEmitter all emit `this.<Name>` and
    // `case "<Name>":` for [StorageSerializable] records. A keyword-named property must compile.
    [Fact]
    public void StorageSerializable_KeywordNamedProperty_GeneratedCodeCompiles()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Bar(
                [property: Tag(1)] long @class,
                [property: Tag(2)] double @event);
            """;

        AssertGeneratedCompiles_Storage(source);
    }

    // OrderedCodeGenerator emits the byte-lex CompareTo / serialization for [Key] records. A
    // keyword-named key property must round-trip through @-escaping too.
    [Fact]
    public void Key_KeywordNamedProperty_GeneratedCodeCompiles()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct OrderKey(
                [property: Tag(1)] long @class,
                [property: Tag(2)] long @event);
            """;

        AssertGeneratedCompiles_Storage(source);
    }

    // ColumnarCodeGenerator emits Rehydrate / ProjectTo / sort comparers for [ColumnarStorable]
    // records. A keyword-named column must compile through the columnar pipeline.
    [Fact]
    public void ColumnarStorable_KeywordNamedProperty_GeneratedCodeCompiles()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [ColumnarStorable]
            public partial record Bar
            {
                public System.DateOnly Date { get; init; }
                public long @class { get; init; }
                public Bar() { }
            }
            """;

        var compilation = TestHelpers.CreateCompilation(source);
        var generator = new ColumnarSourceGenerator();
        _ = CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        AssertNoErrors(outputCompilation);
    }

    private static void AssertGeneratedCompiles_Storage(string source)
    {
        var compilation = TestHelpers.CreateCompilation(source);
        var generator = new StorageSourceGenerator();
        _ = CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        AssertNoErrors(outputCompilation);
    }

    private static void AssertNoErrors(Compilation outputCompilation)
    {
        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        errors.Should().BeEmpty(
            because: "generated code must compile when a property is named with a verbatim C# "
                + "keyword; got:\n" + string.Join("\n", errors.Select(d => d.ToString())));
    }
}
