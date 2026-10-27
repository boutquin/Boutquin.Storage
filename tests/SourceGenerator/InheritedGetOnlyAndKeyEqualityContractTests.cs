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

namespace Boutquin.Storage.SourceGenerator.Tests;

/// <summary>
/// Two member-discovery contracts that sit beside the round-2 declared-member repairs.
/// (1) A stored get-only property reached through a base type is stored state exactly as a declared
/// one is: the columnar plan has no column for it, so it must be rejected (BSSG024), never dropped
/// to rehydrate as <c>default</c>; a computed inherited getter stays derived state.
/// (2) A <c>[Key]</c> is its planned components: the generated <c>CompareTo</c> and ordered bytes
/// read only the public planned properties, so instance state outside them would make the key's
/// equality (which equality-keyed engine maps — tombstones — rely on) disagree with its order and
/// with its own round trip. Such state is rejected (BSSG024) instead.
/// </summary>
public sealed class InheritedGetOnlyAndKeyEqualityContractTests
{
    private static (CSharpCompilation Output, GeneratorDriverRunResult Run) RunGenerators(
        string source,
        params IIncrementalGenerator[] generators)
    {
        _ = typeof(Boutquin.Storage.Domain.Attributes.ColumnarStorableAttribute).FullName;
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create(
            "Contract_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver
            .Create(generators.Select(g => g.AsSourceGenerator()).ToArray())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return ((CSharpCompilation)output, driver.GetRunResult());
    }

    private static Assembly Load(CSharpCompilation output)
    {
        using var ms = new MemoryStream();
        var emit = output.Emit(ms);
        emit.Success.Should().BeTrue(string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        ms.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    // ───── (1) inherited stored get-only state in the columnar plan ─────────────────────────

    public static TheoryData<string, string> InheritedStoredGetOnlyShapes() => new()
    {
        { "auto-property assigned by the base constructor", """
            public abstract record BaseRow
            {
                protected BaseRow() { }
                protected BaseRow(long volume) { Volume = volume; }
                public long Volume { get; }
            }
            """ },
        { "positional base record re-declaring its parameter get-only", """
            public record BaseRow(long Volume)
            {
                public BaseRow() : this(0) { }
                public long Volume { get; } = Volume;
            }
            """ },
        { "field-keyword get-only property", """
            public abstract record BaseRow
            {
                protected BaseRow() { }
                protected BaseRow(long volume) { Volume = volume; }
                public long Volume { get => field; }
            }
            """ },
    };

    [Theory]
    [MemberData(nameof(InheritedStoredGetOnlyShapes))]
    public void ColumnarStorable_InheritedStoredGetOnlyProperty_IsRejected(string shape, string baseRecord)
    {
        var source = $$"""
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Q;
            {{baseRecord}}
            [ColumnarStorable]
            public partial record Bar : BaseRow
            {
                public Bar() { }
                public DateOnly Date { get; init; }
            }
            """;

        var (output, run) = RunGenerators(source, new ColumnarSourceGenerator());
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && !d.Id.StartsWith("BSSG", StringComparison.Ordinal))
            .Should().BeEmpty($"the '{shape}' case must compile");

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG024" && d.Severity == DiagnosticSeverity.Error && d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("Volume"),
            $"shape '{shape}': an inherited get-only property with a backing field is stored state with no column — it "
            + "would rehydrate as default, exactly as the declared shape BSSG025 rejects");
        run.GeneratedTrees.Should().BeEmpty($"shape '{shape}' must not emit a lossy plan");
    }

    [Fact]
    public void ColumnarStorable_InheritedComputedGetter_IsDerivedState_AndRoundTrips()
    {
        // The precision half: a computed inherited getter has no backing field and recomputes from
        // planned columns, so excluding it loses nothing and the type must still generate.
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Q;
            public abstract record BaseRow
            {
                public abstract DateOnly Date { get; init; }
                public int Year => Date.Year;
            }
            [ColumnarStorable]
            public partial record Bar : BaseRow
            {
                public override DateOnly Date { get; init; }
            }
            """;

        var (output, run) = RunGenerators(source, new ColumnarSourceGenerator());

        run.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        var bar = Load(output).GetType("Q.Bar")!;
        var columns = new Dictionary<string, Array> { ["Date"] = new[] { new DateOnly(2026, 3, 4) } };
        var re = bar.GetMethod("Rehydrate", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [columns, 0])!;
        bar.GetProperty("Year")!.GetValue(re).Should().Be(2026);
    }

    // ───── (2) [Key] instance state outside the planned components ─────────────────────────

    public static TheoryData<string, string> KeyNonPublicStateShapes() => new()
    {
        { "private memo field on a record struct key", """
            [Key]
            public partial record struct K([property: Tag(1)] int A)
            {
                private string? _display;
                public string Display() => _display ??= "K" + A;
            }
            """ },
        { "internal init property on a record struct key", """
            [Key]
            public partial record struct K([property: Tag(1)] int A)
            {
                internal int Region { get; init; }
            }
            """ },
        { "private field on a sealed record class key", """
            [Key]
            public sealed partial record K([property: Tag(1)] int A)
            {
                private int _hits;
                public void Touch() => _hits++;
            }
            """ },
    };

    [Theory]
    [MemberData(nameof(KeyNonPublicStateShapes))]
    public void Key_NonPublicInstanceState_IsRejected(string shape, string declaration)
    {
        var source = $$"""
            #nullable enable
            using Boutquin.Storage.Domain.Attributes;
            namespace K1;
            {{declaration}}
            """;

        var (_, run) = RunGenerators(source, new StorageSourceGenerator());

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG024" && d.Severity == DiagnosticSeverity.Error,
            $"shape '{shape}': the generated CompareTo and ordered bytes ignore this state while the record's "
            + "equality compares it, so two keys that order equal would be unequal and a round trip unequal to itself");
    }

    public static TheoryData<string, string> KeyScratchStateShapes() => new()
    {
        { "internal settable scratch property (record struct)", """
            [Key]
            public partial record struct RK([property: Tag(1)] int A)
            {
                internal int Scratch { get; set; }
            }
            """ },
        { "private settable scratch property (record class)", """
            [Key]
            public partial record RK([property: Tag(1)] int A)
            {
                private int Scratch { get; set; }
                public void Mark(int value) => Scratch = value;
            }
            """ },
    };

    [Theory]
    [MemberData(nameof(KeyScratchStateShapes))]
    public void Key_Record_WithStateOutsideThePlan_IsRejected_SoEqualityCannotDisagreeWithCompareTo(string shape, string declaration)
    {
        // The engine-facing property: tombstone maps key by equality, lookups by CompareTo. A key
        // record whose synthesized equality covers state the plan does not serialize serves a
        // deleted value or ignores a delete; such a record must not generate at all.
        var source = $$"""
            #nullable enable
            using Boutquin.Storage.Domain.Attributes;
            namespace K2;
            {{declaration}}
            """;

        var (_, run) = RunGenerators(source, new StorageSourceGenerator());

        run.Diagnostics.Should().Contain(
            d => d.Id == "BSSG024" && d.Severity == DiagnosticSeverity.Error && d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("Scratch"),
            $"shape '{shape}'");
        run.GeneratedTrees.Should().BeEmpty($"shape '{shape}' must not emit a key whose Equals disagrees with CompareTo");
    }

    [Fact]
    public void Key_Record_WithOnlyPlannedState_StillGenerates_AndEqualityMatchesCompareTo()
    {
        // The precision half: static state and constants are not instance state, so a key
        // carrying them still generates, and its equality agrees with CompareTo after a round trip.
        const string source = """
            #nullable enable
            using Boutquin.Storage.Domain.Attributes;
            namespace K3;
            [Key]
            public partial record struct K([property: Tag(1)] int A, [property: Tag(2)] string B)
            {
                private static int s_created;
                private const int Bias = 3;
                public static int Created => s_created + Bias;
            }
            """;

        var (output, run) = RunGenerators(source, new StorageSourceGenerator());

        run.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        var k = Load(output).GetType("K3.K")!;
        var original = Activator.CreateInstance(k, 5, "x")!;
        using var s = new MemoryStream();
        k.GetMethod("Serialize", [typeof(Stream)])!.Invoke(original, [s]);
        s.Position = 0;
        var back = k.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static, [typeof(Stream)])!.Invoke(null, [s])!;
        k.GetMethod("CompareTo", [k])!.Invoke(original, [back]).Should().Be(0);
        back.Should().Be(original);
    }
}
