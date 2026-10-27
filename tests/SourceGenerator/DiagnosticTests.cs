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

public sealed class DiagnosticTests
{
    [Theory]
    [InlineData("IEnumerable<int>?")]
    [InlineData("IEnumerable<Child>?")]
    [InlineData("IEnumerable<string?>")]
    [InlineData("IEnumerable<Child?>")]
    public void BSSG001_NullableCollectionShapes_AreRejectedBeforeEmission(string propertyType)
    {
        var source = $$"""
            #nullable enable
            using System.Collections.Generic;
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record class Child([property: Tag(1)] int Value);
            [StorageSerializable]
            public partial record struct BadType([property: Tag(1)] {{propertyType}} Values);
            """;

        var (_, result) = TestHelpers.RunGenerator(source);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "BSSG001");
        result.Diagnostics.Should().NotContain(d => d.Id == "CS8785");
        result.GeneratedTrees.Should().NotContain(tree => tree.FilePath.Contains("BadType.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void BSSG001_NullableCollectionElement_IsRejected_NotGeneratorCrash()
    {
        // #6 (class E): IEnumerable<Nullable<T>> on a [StorageSerializable] record classified as a
        // PrimitiveCollection with ElementKind=Nullable and reached the emitter, which threw — Roslyn
        // surfaces that as a CS8785 generator-crash warning (silent under default settings), with no
        // BSSG diagnostic and zero code emitted. The nullable collection element must be rejected
        // loudly at transform time so the record is skipped with a real diagnostic. (Same class as
        // the already-fixed Nullable<enum> scalar bug.)
        const string source = """
            using System.Collections.Generic;
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct BadType([property: Tag(1)] IEnumerable<int?> Values);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);

        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG001",
            "a nullable collection element must produce a clean BSSG001, not a generator crash");
    }

    [Fact]
    public void BSSG008_Excluded_Property_Type_DateTime()
    {
        // Phase 1 moves DateTime / nint / nuint from the generic BSSG001 to
        // the targeted BSSG008 with a "use DateTimeOffset / DateOnly" remediation message.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct BadType(System.DateTime Timestamp);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var diagnostics = runResult.Diagnostics;

        diagnostics.Should().ContainSingle(d => d.Id == "BSSG008");
        diagnostics.Single(d => d.Id == "BSSG008").GetMessage(System.Globalization.CultureInfo.InvariantCulture).Should()
            .Contain("DateTimeOffset").And.Contain("DateOnly");
    }

    [Theory]
    [InlineData("nint", "int or long")]
    [InlineData("nuint", "uint or ulong")]
    public void BSSG008_Excluded_Native_Integer_Types(string typeSyntax, string expectedRemediationFragment)
    {
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct BadType({{typeSyntax}} Handle);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var diagnostics = runResult.Diagnostics;

        diagnostics.Should().ContainSingle(d => d.Id == "BSSG008");
        diagnostics.Single(d => d.Id == "BSSG008").GetMessage(System.Globalization.CultureInfo.InvariantCulture).Should().Contain(expectedRemediationFragment);
    }

    [Fact]
    public void BSSG002_Type_Must_Be_Partial()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public record struct NonPartialKey(long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var diagnostics = runResult.Diagnostics;

        diagnostics.Should().ContainSingle(d => d.Id == "BSSG002");
    }

    [Fact]
    public void BSSG002_Blocks_Code_Generation()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public record struct NonPartialKey(long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);

        // No source should be generated for a non-partial type
        runResult.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void BSSG003_Type_Should_Be_Record_Struct()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial class NotARecordStruct
            {
                public string Name { get; set; }
            }
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var diagnostics = runResult.Diagnostics;

        diagnostics.Should().ContainSingle(d => d.Id == "BSSG003");
    }

    [Fact]
    public void BSSG003_Is_Advisory_And_Still_Generates_Code()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial struct NotARecordStruct
            {
                public NotARecordStruct(string name) { Name = name; }

                [Tag(1)]
                public string Name { get; set; }
            }
            """;

        var (outputCompilation, runResult) = TestHelpers.RunGenerator(source);

        // Code should still be generated despite the advisory warning. Every
        // property carries a [Tag(N)] so BSSG010 (MissingTag) does not block emission, and
        // the constructor takes the properties positionally so BSSG025 does not either
        // (the generated Deserialize calls it; ProjectTo's `with` is valid on a struct).
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG003" && d.Severity == DiagnosticSeverity.Warning);
        runResult.GeneratedTrees.Should().NotBeEmpty();

        // Advisory means the emitted code is usable, not merely present.
        var generatedErrors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error &&
                        d.Location.SourceTree is { } tree &&
                        runResult.GeneratedTrees.Contains(tree))
            .ToList();
        generatedErrors.Should().BeEmpty(string.Join("\n", generatedErrors));
    }

    [Fact]
    public void BSSG005_Mutually_Exclusive_Attributes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            [StorageSerializable]
            public partial record struct DualAnnotated(long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var diagnostics = runResult.Diagnostics;

        diagnostics.Should().Contain(d => d.Id == "BSSG005");
    }

    [Fact]
    public void BSSG005_Blocks_Code_Generation()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            [StorageSerializable]
            public partial record struct DualAnnotated(long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);

        // No source should be generated for a type with both attributes
        runResult.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void BSSG006_Interface_Already_Implemented_Serializable()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using Boutquin.Storage.Domain.Interfaces;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct AlreadySerializable(string Name) : ISerializable<AlreadySerializable>
            {
                public void Serialize(System.IO.Stream stream) { }
                public static AlreadySerializable Deserialize(System.IO.Stream stream) => default;
            }
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var diagnostics = runResult.Diagnostics;

        diagnostics.Should().ContainSingle(d => d.Id == "BSSG006");
    }

    [Fact]
    public void BSSG008_Excluded_Collection_Element_Type()
    {
        // IEnumerable<DateTime> used to emit BSSG001; Phase 1 routes DateTime through
        // BSSG008 inside collections too so the remediation message still surfaces.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct BadCollection(IEnumerable<System.DateTime> Dates);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var diagnostics = runResult.Diagnostics;

        diagnostics.Should().ContainSingle(d => d.Id == "BSSG008");
    }

    [Fact]
    public void BSSG001_Unsupported_Collection_Element_Type_Reference()
    {
        // Reference types (other than string) still fall through to BSSG001; confirm the
        // generic unsupported-type pathway survives the classifier refactor.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct BadCollection(IEnumerable<object> Items);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var diagnostics = runResult.Diagnostics;

        diagnostics.Should().ContainSingle(d => d.Id == "BSSG001");
    }

    [Fact]
    public void No_Diagnostics_For_Valid_Key_Type()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct ValidKey([property: Tag(1)] long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);

        runResult.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void No_Diagnostics_For_Valid_StorageSerializable_Type()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct ValidValue(
                [property: Tag(1)] string Name,
                [property: Tag(2)] int Count);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);

        runResult.Diagnostics.Should().BeEmpty();
    }
}
