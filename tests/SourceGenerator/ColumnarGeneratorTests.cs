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

public sealed partial class ColumnarGeneratorTests
{
    private const string ColumnarAttributeSource = """
        using System;
        using System.Diagnostics;
        namespace Boutquin.Storage.Domain.Enums
        {
            public enum ColumnEncoding
            {
                Dictionary = 0,
                BitmapRle = 1,
                DeltaVlq = 2,
                DeltaOfDeltaVlq = 3,
                GorillaXor = 4,
                RawDouble = 5,
                ScaledIntegerBitPacked = 6,
            }
        }
        namespace Boutquin.Storage.Domain.Attributes
        {
            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
            [Conditional("BOUTQUIN_STORAGE_GENERATOR")]
            public sealed class ColumnarStorableAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
            [Conditional("BOUTQUIN_STORAGE_GENERATOR")]
            public sealed class ColumnEncodingAttribute : Attribute
            {
                public ColumnEncodingAttribute(Boutquin.Storage.Domain.Enums.ColumnEncoding encoding) { Encoding = encoding; }
                public Boutquin.Storage.Domain.Enums.ColumnEncoding Encoding { get; }
            }
        }
        namespace Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar
        {
            public readonly record struct ColumnarFieldDescriptor(string Name, Boutquin.Storage.Domain.Enums.ColumnEncoding Encoding);
        }
        """;

    private static (CSharpCompilation Compilation, GeneratorDriverRunResult RunResult) RunColumnar(string userSource)
    {
        var sources = new[] { ColumnarAttributeSource, userSource };
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            sources.Select(s => CSharpSyntaxTree.ParseText(s, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))),
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver
            .Create(new ColumnarSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        return ((CSharpCompilation)outputCompilation, driver.GetRunResult());
    }

    // ───── Defaults + emission ────────────────────────────────────────

    [Fact]
    public void AllSupportedTypes_GetTheirDefaultEncoding()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                public int Tick { get; init; }
                public long Volume { get; init; }
                public decimal Close { get; init; }
                public double Vwap { get; init; }
                public bool IsAdjusted { get; init; }
                public string Ticker { get; init; } = "";
            }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().BeEmpty();
        var generated = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("Bar.Columnar.g.cs", StringComparison.Ordinal));
        var code = generated.ToString();

        code.Should().Contain("ColumnarFieldDescriptor(\"Date\", global::Boutquin.Storage.Domain.Enums.ColumnEncoding.DeltaOfDeltaVlq)");
        code.Should().Contain("ColumnarFieldDescriptor(\"Tick\", global::Boutquin.Storage.Domain.Enums.ColumnEncoding.DeltaVlq)");
        code.Should().Contain("ColumnarFieldDescriptor(\"Volume\", global::Boutquin.Storage.Domain.Enums.ColumnEncoding.DeltaVlq)");
        code.Should().Contain("ColumnarFieldDescriptor(\"Close\", global::Boutquin.Storage.Domain.Enums.ColumnEncoding.Dictionary)");
        code.Should().Contain("ColumnarFieldDescriptor(\"Vwap\", global::Boutquin.Storage.Domain.Enums.ColumnEncoding.GorillaXor)");
        code.Should().Contain("ColumnarFieldDescriptor(\"IsAdjusted\", global::Boutquin.Storage.Domain.Enums.ColumnEncoding.BitmapRle)");
        code.Should().Contain("ColumnarFieldDescriptor(\"Ticker\", global::Boutquin.Storage.Domain.Enums.ColumnEncoding.Dictionary)");
    }

    [Fact]
    public void EmittedType_IsPartialAndCarriesColumnarFieldPlanProperty()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar { public DateOnly Date { get; init; } }
            """;

        var (_, result) = RunColumnar(source);
        var code = result.GeneratedTrees.Single().ToString();

        code.Should().Contain("public partial record Bar");
        code.Should().Contain("public static global::System.Collections.Generic.IReadOnlyList<global::Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnarFieldDescriptor> ColumnarFieldPlan { get; }");
    }

    [Fact]
    public void InternalType_GetsInternalPartialDeclaration()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            internal partial record Bar { public DateOnly Date { get; init; } }
            """;

        var (_, result) = RunColumnar(source);
        var code = result.GeneratedTrees.Single().ToString();

        code.Should().Contain("internal partial record Bar");
    }

    // ───── Overrides ──────────────────────────────────────────────────

    [Fact]
    public void ColumnEncodingOverride_OnDouble_RoutesToRawDouble()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            using Boutquin.Storage.Domain.Enums;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                [ColumnEncoding(ColumnEncoding.RawDouble)]
                public double HashedResidual { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().BeEmpty();
        var code = result.GeneratedTrees.Single().ToString();
        code.Should().Contain("ColumnarFieldDescriptor(\"HashedResidual\", global::Boutquin.Storage.Domain.Enums.ColumnEncoding.RawDouble)");
    }

    [Fact]
    public void ColumnEncodingOverride_OnDecimal_CanSelectScaledIntegerBitPacked()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            using Boutquin.Storage.Domain.Enums;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                [ColumnEncoding(ColumnEncoding.ScaledIntegerBitPacked)]
                public decimal Close { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().BeEmpty();
        var code = result.GeneratedTrees.Single().ToString();
        code.Should().Contain("ColumnarFieldDescriptor(\"Close\", global::Boutquin.Storage.Domain.Enums.ColumnEncoding.ScaledIntegerBitPacked)");
    }

    // ───── BSSG007 (incompatible override) ────────────────────────────

    [Fact]
    public void BSSG007_IncompatibleOverride_OnStringField_WithGorillaXor()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            using Boutquin.Storage.Domain.Enums;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                [ColumnEncoding(ColumnEncoding.GorillaXor)]
                public string Ticker { get; init; } = "";
            }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "BSSG007");
    }

    [Fact]
    public void BSSG007_IncompatibleOverride_DoesNotEmitColumnarPlan()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            using Boutquin.Storage.Domain.Enums;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                [ColumnEncoding(ColumnEncoding.BitmapRle)]
                public decimal Close { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "BSSG007");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void BSSG007_Message_ListsCompatibleTypesForTheSelectedEncoding()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            using Boutquin.Storage.Domain.Enums;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                [ColumnEncoding(ColumnEncoding.DeltaOfDeltaVlq)]
                public int NotADate { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        var diagnostic = result.Diagnostics.Single(d => d.Id == "BSSG007");
        var message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        message.Should().Contain("NotADate");
        message.Should().Contain("DeltaOfDeltaVlq");
        message.Should().Contain("DateOnly");
    }

    // ───── Partial requirement (shared BSSG002) ───────────────────────

    [Fact]
    public void NonPartialType_TriggersBSSG002_AndEmitsNoCode()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public record Bar { public DateOnly Date { get; init; } }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "BSSG002");
        result.GeneratedTrees.Should().BeEmpty();
    }

    // ───── Non-record type (AC-13.1 / BSSG022) ───────────────────────

    [Fact]
    public void NonRecordClass_WithColumnarStorable_TriggersBSSG022_AndEmitsNoCode()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial class Prices { public DateOnly Date { get; init; } }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "BSSG022");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void NonRecordStruct_WithColumnarStorable_TriggersBSSG022_AndEmitsNoCode()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial struct Prices { public DateOnly Date { get; init; } }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "BSSG022");
        result.GeneratedTrees.Should().BeEmpty();
    }

    // ───── Rehydrate emission ─────────────────────────────────────────

    [Fact]
    public void RecordStruct_EmitsRehydrateMethod_WithCorrectSignature()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record struct Bar
            {
                public DateOnly Date { get; init; }
                public decimal Close { get; init; }
                public long Volume { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().BeEmpty();
        var code = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("Bar.Columnar.g.cs", StringComparison.Ordinal)).ToString();

        code.Should().Contain("public static Bar Rehydrate(global::System.Collections.Generic.IReadOnlyDictionary<string, global::System.Array> columns, int rowIndex)");
    }

    [Fact]
    public void RecordStruct_Rehydrate_Uses_DefaultInit_And_WithExpressions()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record struct Bar
            {
                public DateOnly Date { get; init; }
                public decimal Close { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        var code = result.GeneratedTrees.Single().ToString();

        // record struct uses default(Bar), not new Bar()
        code.Should().Contain("default(Bar)");
        // with-expression for each field
        code.Should().Contain("__result with { Date =");
        code.Should().Contain("__result with { Close =");
        // TryGetValue pattern
        code.Should().Contain("columns.TryGetValue(\"Date\", out var __Date)");
        code.Should().Contain("columns.TryGetValue(\"Close\", out var __Close)");
    }

    [Fact]
    public void RecordClass_Rehydrate_Uses_NewInstance_And_WithExpressions()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                public decimal Close { get; init; }
                public Bar() { }
            }
            """;

        var (_, result) = RunColumnar(source);

        var code = result.GeneratedTrees.Single().ToString();

        // record class uses new Bar()
        code.Should().Contain("new Bar()");
        code.Should().Contain("__result with { Date =");
        code.Should().Contain("__result with { Close =");
    }

    [Fact]
    public void RecordStruct_EmitsIColumnarRehydratable_InBaseList()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record struct Bar { public DateOnly Date { get; init; } }
            """;

        var (_, result) = RunColumnar(source);

        var code = result.GeneratedTrees.Single().ToString();

        code.Should().Contain("global::Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar.IColumnarRehydratable<Bar>");
    }

    [Fact]
    public void UserDeclaredRehydrate_SkipsEmission()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record struct Bar : IUserRehydratable
            {
                public DateOnly Date { get; init; }
                // user-provided Rehydrate — generator must not emit a second one
                public static Bar Rehydrate(IReadOnlyDictionary<string, Array> columns, int rowIndex)
                    => default;
            }
            public interface IUserRehydratable { }
            """;

        var (_, result) = RunColumnar(source);

        var code = result.GeneratedTrees.Single().ToString();

        // Generator must not emit Rehydrate when user has already declared it
        var rehydrateCount = MyRegex().Count(code);
        rehydrateCount.Should().Be(0, "generator must not emit Rehydrate when user already declared it");
    }

    [System.Text.RegularExpressions.GeneratedRegex("public static Bar Rehydrate")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();

    // ───── CreateColumnSortComparer emission ─────────────────

    [Fact]
    public void RecordStruct_EmitsCreateColumnSortComparer_WithCorrectSignature()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record struct Bar
            {
                public DateOnly Date { get; init; }
                public decimal Close { get; init; }
                public long Volume { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().BeEmpty();
        var code = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("Bar.Columnar.g.cs", StringComparison.Ordinal)).ToString();

        code.Should().Contain(
            "public static global::System.Collections.Generic.IComparer<Bar> CreateColumnSortComparer(",
            "factory must return IComparer<TSelf>");
        code.Should().Contain(
            "global::Boutquin.Storage.Domain.ValueObjects.TimeSeries.ColumnSortSpec spec",
            "factory must take a ColumnSortSpec");
    }

    [Fact]
    public void RecordStruct_SortComparer_DispatchesToTypedComparerDefault_PerProperty()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record struct Bar
            {
                public DateOnly Date { get; init; }
                public decimal Close { get; init; }
                public long Volume { get; init; }
                public string Ticker { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        var code = result.GeneratedTrees.Single().ToString();

        // Each sortable property must dispatch to a strongly-typed Comparer<T>.Default chain
        // — no PropertyInfo / IComparable boxing in the hot path. Strings are the exception:
        // they compare ordinally (string.CompareOrdinal) so on-disk row order is
        // locale-independent and matches the rest of the columnar stack.
        code.Should().Contain(
            "\"Date\" => static (a, b) => global::System.Collections.Generic.Comparer<global::System.DateOnly>.Default.Compare(a.Date, b.Date)");
        code.Should().Contain(
            "\"Close\" => static (a, b) => global::System.Collections.Generic.Comparer<decimal>.Default.Compare(a.Close, b.Close)");
        code.Should().Contain(
            "\"Volume\" => static (a, b) => global::System.Collections.Generic.Comparer<long>.Default.Compare(a.Volume, b.Volume)");
        code.Should().Contain(
            "\"Ticker\" => static (a, b) => string.CompareOrdinal(a.Ticker, b.Ticker)");
    }

    [Fact]
    public void RecordStruct_SortComparer_RejectsNonAscending_WithArgumentOutOfRange()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record struct Bar
            {
                public DateOnly Date { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        var code = result.GeneratedTrees.Single().ToString();

        code.Should().Contain("global::Boutquin.Storage.Domain.Enums.SortDirection.Ascending");
        code.Should().Contain("ArgumentOutOfRangeException",
            "non-Ascending direction must surface ArgumentOutOfRangeException — matches the legacy comparator contract");
    }

    [Fact]
    public void RecordStruct_SortComparer_UnknownColumn_ThrowsArgumentException()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record struct Bar
            {
                public DateOnly Date { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        var code = result.GeneratedTrees.Single().ToString();

        code.Should().Contain(
            "_ => throw new global::System.ArgumentException(",
            "the default switch arm must throw ArgumentException for unrecognised column names");
        code.Should().Contain("does not match any sortable public property on Bar");
    }

    [Fact]
    public void RecordStruct_SortComparer_OmitsNullableProperties()
    {
        // Maybe is get-only so it is excluded from the column plan as derived state (no BSSG020,
        // which would abort emission). It is still a candidate sort key — the point of this test
        // is that the generator excludes Nullable<T> from the sort comparer regardless.
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record struct Bar
            {
                public DateOnly Date { get; init; }
                public int Raw { get; init; }
                public int? Maybe => Raw == 0 ? (int?)null : Raw;
            }
            """;

        var (_, result) = RunColumnar(source);

        var code = result.GeneratedTrees.Single().ToString();

        // Date is sortable; Maybe (Nullable<int>) must be excluded so the generated lambdas
        // never dereference a null target. Reflection fallback handles nullable sort columns.
        code.Should().Contain("\"Date\" => static (a, b) =>");
        code.Should().NotContain("\"Maybe\" => ");
    }

    [Fact]
    public void RecordClass_SortComparer_UsesNullableCompareSignature()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                public Bar() { }
            }
            """;

        var (_, result) = RunColumnar(source);

        var code = result.GeneratedTrees.Single().ToString();

        // Reference-type emission must declare Compare(T?, T?) — bang at the call site
        // forwards the (asserted-non-null) records into the typed Comparison<T>.
        code.Should().Contain("public int Compare(Bar? x, Bar? y)");
        code.Should().Contain("_comparers[__i](x!, y!)");
    }

    [Fact]
    public void UserDeclaredSortComparer_SkipsEmission()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using Boutquin.Storage.Domain.Attributes;
            using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
            namespace Test;
            [ColumnarStorable]
            public partial record struct Bar
            {
                public DateOnly Date { get; init; }

                // user-provided factory — generator must not emit a second one
                public static IComparer<Bar> CreateColumnSortComparer(ColumnSortSpec spec)
                    => Comparer<Bar>.Default;
            }
            """;

        var (_, result) = RunColumnar(source);

        var code = result.GeneratedTrees.Single().ToString();

        code.Should().NotContain(
            "public static global::System.Collections.Generic.IComparer<Bar> CreateColumnSortComparer(",
            "generator must defer to user-declared factory (avoids CS0111)");
    }
}
