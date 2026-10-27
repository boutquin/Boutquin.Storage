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

// End-to-end tests exercising the Phase 1 type-set expansion. Each test drives a property of
// a given CLR type through the generator and asserts both that the generator raises no
// diagnostics and that the emitted code compiles cleanly against net10. This is the canary
// for the Phase 1 exit criteria: all 803+ tests pass, and MarketData-shape records
// compile zero-warning.
public sealed class Phase1CoverageTests
{
    // Tier 1 numerics (13) — sbyte/byte/short/ushort/int/uint/long/ulong/Int128/UInt128/Half/float/double.
    [Theory]
    [InlineData("sbyte")]
    [InlineData("byte")]
    [InlineData("short")]
    [InlineData("ushort")]
    [InlineData("int")]
    [InlineData("uint")]
    [InlineData("long")]
    [InlineData("ulong")]
    [InlineData("System.Int128")]
    [InlineData("System.UInt128")]
    [InlineData("System.Half")]
    [InlineData("float")]
    [InlineData("double")]
    public void Tier1_Numeric_Property_Compiles_Without_Errors(string clrType)
        => AssertGeneratedCodeCompiles($"public partial record struct Holder([property: Tag(1)] {clrType} P);");

    // Tier 2 non-numerics (3) — bool/char/string.
    [Theory]
    [InlineData("bool")]
    [InlineData("char")]
    [InlineData("string")]
    public void Tier2_NonNumeric_Property_Compiles_Without_Errors(string clrType)
        => AssertGeneratedCodeCompiles($"public partial record struct Holder([property: Tag(1)] {clrType} P);");

    // Tier 3 BCL primitives (6) — DateOnly/TimeOnly/TimeSpan/DateTimeOffset/Guid/decimal.
    [Theory]
    [InlineData("System.DateOnly")]
    [InlineData("System.TimeOnly")]
    [InlineData("System.TimeSpan")]
    [InlineData("System.DateTimeOffset")]
    [InlineData("System.Guid")]
    [InlineData("decimal")]
    public void Tier3_BCL_Property_Compiles_Without_Errors(string clrType)
        => AssertGeneratedCodeCompiles($"public partial record struct Holder([property: Tag(1)] {clrType} P);");

    // Tier 4 enums with each supported underlying numeric.
    [Theory]
    [InlineData("byte")]
    [InlineData("sbyte")]
    [InlineData("short")]
    [InlineData("ushort")]
    [InlineData("int")]
    [InlineData("uint")]
    [InlineData("long")]
    [InlineData("ulong")]
    public void Tier4_Enum_Property_Compiles_Without_Errors(string underlying)
    {
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            public enum MyEnum : {{underlying}} { A, B, C }
            [StorageSerializable]
            public partial record struct Holder([property: Tag(1)] MyEnum P);
            """;
        AssertFullSourceCompiles(source);
    }

    // Tier 6 nullable value types — exercise the HasValue prefix path.
    [Theory]
    [InlineData("int?")]
    [InlineData("long?")]
    [InlineData("double?")]
    [InlineData("bool?")]
    [InlineData("System.DateOnly?")]
    [InlineData("System.Guid?")]
    [InlineData("decimal?")]
    public void Tier6_Nullable_Property_Compiles_Without_Errors(string clrType)
        => AssertGeneratedCodeCompiles($"public partial record struct Holder([property: Tag(1)] {clrType} P);");

    // Key variants — ordered-wire stub still uses Phase 1 positional emission, so a [Key]
    // record with a new primitive shape must compile and declare the IStorageKey<T> aggregator
    // (which extends IComparable<T>, IEquatable<T>, and ISerializable<T>) so engine signatures
    // accept it without the consumer spelling out the three base interfaces.
    [Fact]
    public void Key_With_DateOnly_Compiles_And_Generates_CompareTo()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct DateKey([property: Tag(1)] System.DateOnly Day);
            """;

        var (outputCompilation, runResult) = TestHelpers.RunGenerator(source);
        outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
        runResult.Diagnostics.Should().BeEmpty();

        var generated = runResult.GeneratedTrees.Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("DateKey"));
        generated.Should().NotBeNull();
        generated.Should().Contain("int CompareTo(DateKey other)");
        generated.Should().Contain("global::Boutquin.Storage.Domain.Interfaces.IStorageKey<DateKey>");
    }

    // A MarketData-shaped record compiles zero-warning. This is
    // the single most load-bearing consumer check for Phase 1.
    [Fact]
    public void MarketData_Shape_Record_Compiles_Without_Errors_Or_Warnings()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Bar(
                [property: Tag(1)] System.DateOnly Date,
                [property: Tag(2)] decimal Open,
                [property: Tag(3)] decimal High,
                [property: Tag(4)] decimal Low,
                [property: Tag(5)] decimal Close,
                [property: Tag(6)] long Volume);
            """;

        var (outputCompilation, runResult) = TestHelpers.RunGenerator(source);

        runResult.Diagnostics.Should().BeEmpty();
        // Filter CS0436 (duplicate type between injected attribute stubs and the force-loaded
        // Domain assembly) — this is test-harness noise from TestHelpers.AttributeSource
        // declaring mock attribute types alongside the real ones for generator discovery.
        // Consumer projects don't see this warning because they reference Domain directly.
        outputCompilation.GetDiagnostics()
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Where(d => d.Id != "CS0436")
            .Should().BeEmpty();
    }

    // A DatasetKey-shape with an enum + string property
    // compiles cleanly; the enum is no longer stringified at the consumer boundary.
    [Fact]
    public void DatasetKey_Shape_With_Enum_Compiles_Without_Errors()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            public enum BarGranularity { Daily, Hourly, Minute }
            [Key]
            public partial record struct PriceBarDatasetKey(
                [property: Tag(1)] string Provider,
                [property: Tag(2)] BarGranularity Frequency,
                [property: Tag(3)] string Ticker,
                [property: Tag(4)] string Exchange);
            """;

        var (outputCompilation, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().BeEmpty();
        outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    // ColumnarSourceGenerator no longer contains string-dispatch for
    // DateOnly/decimal. This is a structural grep check on the classifier integration.
    [Fact]
    public void ColumnarSourceGenerator_No_Longer_Uses_String_Dispatch_For_DateOnly()
    {
        var sourceText = System.IO.File.ReadAllText(FindGeneratorSourceFile("ColumnarSourceGenerator.cs"));
        // The 1.x dispatch had a literal "System.DateOnly" => ColumnEncoding.DeltaOfDeltaVlq
        // arm. Phase 1 routes through the classifier so the comparison is on EncodableTypeKind,
        // not the FQN string.
        sourceText.Should().NotContain("\"System.DateOnly\" =>");
        sourceText.Should().NotContain("\"decimal\" or \"System.Decimal\" =>");
    }

    // Collections of new types — IEnumerable<DateOnly> and IEnumerable<decimal>.
    [Theory]
    [InlineData("System.DateOnly")]
    [InlineData("decimal")]
    [InlineData("System.Guid")]
    public void Primitive_Collection_Of_New_Tier_Compiles(string elementType)
    {
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Bag([property: Tag(1)] IEnumerable<{{elementType}}> Items);
            """;
        AssertFullSourceCompiles(source);
    }

    private static void AssertGeneratedCodeCompiles(string holderDecl)
    {
        // Every holder passed here already carries [property: Tag(1)] on its single property so
        // Phase 2's BSSG010 stays silent — these tests are about type-coverage, not validation.
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            {{holderDecl}}
            """;
        AssertFullSourceCompiles(source);
    }

    private static void AssertFullSourceCompiles(string source)
    {
        var (outputCompilation, runResult) = TestHelpers.RunGenerator(source);

        // Generator should raise no BSSG diagnostics for supported types.
        runResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty(
                because: "generator error diagnostics block code emission");

        // Generated code must compile. Surface the full diagnostic for diagnosis.
        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        errors.Should().BeEmpty(
            because: "generated code should compile; got:\n" + string.Join("\n", errors));
    }

    // The ColumnarSourceGenerator grep check resolves the file from the test-run working
    // directory up to the repo root. Test assemblies run from bin/Release/net10.0/, so the
    // source lives four levels up under src/SourceGenerator/.
    private static string FindGeneratorSourceFile(string fileName)
    {
        var current = System.IO.Path.GetDirectoryName(typeof(Phase1CoverageTests).Assembly.Location)!;
        for (var i = 0; i < 10; i++)
        {
            var candidate = System.IO.Path.Combine(current, "src", "SourceGenerator", fileName);
            if (System.IO.File.Exists(candidate))
            {
                return candidate;
            }
            var parent = System.IO.Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }
        throw new System.IO.FileNotFoundException(
            $"Could not locate '{fileName}' by walking up from the test assembly location.");
    }
}
