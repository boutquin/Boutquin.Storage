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

// Encodability fail-closed contract: every settable public property on a [ColumnarStorable]
// record either becomes a column or raises a compile-time error. Silent omission turned a
// shape mistake into runtime data loss — the column was never written, and Rehydrate left
// the property at default(T) on every read.
public sealed partial class ColumnarGeneratorTests
{
    [Theory]
    [InlineData("public char Grade { get; init; }", "Grade")]
    [InlineData("public Guid Id { get; init; }", "Id")]
    [InlineData("public short Small { get; init; }", "Small")]
    [InlineData("public DateTimeOffset Stamp { get; init; }", "Stamp")]
    // Nullable enum classifies as Nullable (not Enum) — must stay BSSG020-rejected now that
    // bare enums are encodable, or it would silently rehydrate as null.
    [InlineData("public DayOfWeek? MaybeDay { get; init; }", "MaybeDay")]
    public void SettableProperty_WithNoColumnarEncoding_RaisesBssg020Error(
        string propertySource, string propertyName)
    {
        var source = $$"""
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                {{propertySource}}
            }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "BSSG020")
            .Which.Severity.Should().Be(DiagnosticSeverity.Error);

        // BSSG020 is data-loss-grade: emission is aborted entirely, so no columnar partial is
        // produced for the type. (Previously the property was merely dropped from the plan while
        // the partial was still emitted — a lossy Rehydrate that left '{propertyName}' at
        // default(T) on every read. The abort closes that gap.)
        _ = propertyName;
        result.GeneratedTrees
            .Should().NotContain(t => t.FilePath.EndsWith("Bar.Columnar.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void GetOnlyComputedProperty_IsExcludedFromPlan_WithoutDiagnostic()
    {
        // A get-only property is derived state: Rehydrate cannot assign it (`with { }` requires
        // an init/set accessor) and excluding it loses nothing — the getter recomputes from the
        // stored components. It must be silently excluded, not error.
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                public double High { get; init; }
                public double Low { get; init; }
                public double Mid => (High + Low) / 2.0;
            }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().BeEmpty();
        var code = result.GeneratedTrees
            .Single(t => t.FilePath.EndsWith("Bar.Columnar.g.cs", StringComparison.Ordinal))
            .ToString();
        code.Should().NotContain("ColumnarFieldDescriptor(\"Mid\"",
            "a get-only property has no setter for Rehydrate to assign");
        code.Should().NotContain("with { Mid =",
            "assigning a get-only property in Rehydrate would not compile (CS8852)");
        // The settable doubles still get their default columns.
        code.Should().Contain("ColumnarFieldDescriptor(\"High\"");
        code.Should().Contain("ColumnarFieldDescriptor(\"Low\"");
    }

    [Fact]
    public void SortComparer_StringColumn_UsesOrdinalComparison()
    {
        // Comparer<string>.Default resolves to culture-sensitive string.CompareTo — on-disk
        // row order would depend on the writing machine's locale. The columnar stack is
        // ordinal everywhere else (ZoneMapComputer uses string.CompareOrdinal); the generated
        // sort comparer must match.
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                public string Ticker { get; init; } = "";
            }
            """;

        var (_, result) = RunColumnar(source);

        var code = result.GeneratedTrees
            .Single(t => t.FilePath.EndsWith("Bar.Columnar.g.cs", StringComparison.Ordinal))
            .ToString();
        code.Should().Contain("string.CompareOrdinal(a.Ticker, b.Ticker)");
        code.Should().NotContain("Comparer<string>.Default");
    }
    [Fact]
    public void FloatProperty_GetsGorillaXorDefault_WithoutDiagnostic()
    {
        // float columns ride the GorillaXor stream as zero-extended raw float bits — the
        // format layer already carries the Single field-type code, zone-map, bitmap, and
        // canonical-CRC arms; the generator default is what makes them reachable.
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                public float Residual { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().BeEmpty();
        var code = result.GeneratedTrees
            .Single(t => t.FilePath.EndsWith("Bar.Columnar.g.cs", StringComparison.Ordinal))
            .ToString();
        code.Should().Contain(
            "ColumnarFieldDescriptor(\"Residual\", global::Boutquin.Storage.Domain.Enums.ColumnEncoding.GorillaXor)");
    }

    [Fact]
    public void GorillaXorOverride_OnFloatProperty_IsCompatible()
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
                public float Residual { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().BeEmpty("GorillaXor carries float columns as raw bits");
    }

    [Fact]
    public void EnumProperty_GetsDeltaVlqDefault_WithoutDiagnostic()
    {
        // Enum columns store the underlying value widened to int64 on the DeltaVlq stream —
        // runs of equal enum values (the common low-cardinality time-series shape) collapse
        // into RLE runs.
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            public enum Venue { Nyse = 0, Nasdaq = 1, Otc = 2 }
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                public Venue TradeVenue { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().BeEmpty();
        var code = result.GeneratedTrees
            .Single(t => t.FilePath.EndsWith("Bar.Columnar.g.cs", StringComparison.Ordinal))
            .ToString();
        code.Should().Contain(
            "ColumnarFieldDescriptor(\"TradeVenue\", global::Boutquin.Storage.Domain.Enums.ColumnEncoding.DeltaVlq)");
    }

    [Fact]
    public void DeltaVlqOverride_OnEnumProperty_IsCompatible()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            using Boutquin.Storage.Domain.Enums;
            namespace Test;
            public enum Venue { Nyse = 0, Nasdaq = 1 }
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                [ColumnEncoding(ColumnEncoding.DeltaVlq)]
                public Venue TradeVenue { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().BeEmpty("DeltaVlq carries enum columns as widened int64");
    }

}
