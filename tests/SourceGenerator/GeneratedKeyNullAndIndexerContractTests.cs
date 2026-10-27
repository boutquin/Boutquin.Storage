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
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;

using Microsoft.CodeAnalysis.Diagnostics;

namespace Boutquin.Storage.SourceGenerator.Tests;

// Contracts the row generators and the BSSG021 analyzer owe consumers:
//
// 1. A generated reference-type [Key] honours the BCL null contract: CompareTo(null) is positive,
//    Equals(null) is false, `key == null` is a plain null test, and the generated file compiles
//    warning-free under nullable (IComparable<T>.CompareTo takes T?, IEquatable<T>.Equals takes T?).
// 2. A public indexer is not row state: a [Key] / [StorageSerializable] record that declares one
//    is accepted and serializes only its properties, like the [ColumnarStorable] sibling.
// 3. A BSSG021 length bound counts only when it dominates the allocation: a bound nested in an
//    `if (stream.CanSeek) { ... }` body leaves the non-seekable path unbounded.
public sealed class GeneratedKeyNullAndIndexerContractTests
{
    static GeneratedKeyNullAndIndexerContractTests()
    {
        _ = typeof(Boutquin.Storage.Domain.Attributes.ColumnarStorableAttribute).FullName;
        _ = typeof(System.Buffers.Binary.BinaryPrimitives).Assembly.FullName;
        _ = typeof(System.Buffers.ArrayPool<byte>).Assembly.FullName;
        _ = typeof(System.IO.Stream).Assembly.FullName;
    }

    // ───── Harness ────────────────────────────────────────────────────

    private static (CSharpCompilation Output, GeneratorDriverRunResult Run) RunGenerators(string source)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create(
            "KeyNull_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver
            .Create(new StorageSourceGenerator().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return ((CSharpCompilation)output, driver.GetRunResult());
    }

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d => d.ToString()));

    private static Assembly CompileAndLoad(string source)
    {
        var (output, run) = RunGenerators(source);
        var generatedTrees = run.GeneratedTrees.ToHashSet();
        var bad = output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                        || (d.Severity == DiagnosticSeverity.Warning && d.Location.SourceTree is { } t && generatedTrees.Contains(t)))
            .ToList();
        run.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty(
            "the generator must accept this shape; diagnostics:\n" + Describe(run.Diagnostics));
        run.GeneratedTrees.Should().NotBeEmpty("an accepted shape gets generated code");
        bad.Should().BeEmpty("generated code must compile warning-free; generator diagnostics:\n"
            + Describe(run.Diagnostics) + "\nproblems:\n" + Describe(bad));

        using var ms = new MemoryStream();
        var emit = output.Emit(ms);
        emit.Success.Should().BeTrue(Describe(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        ms.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    // ───── Reference-type [Key] null contract ─────────────────────────

    private const string RecordClassKey = """
        using Boutquin.Storage.Domain.Attributes;
        namespace P;
        [Key]
        public sealed partial record class RK([property: Tag(1)] int A);
        """;

    private const string PlainClassKey = """
        using Boutquin.Storage.Domain.Attributes;
        namespace P;
        [Key]
        public sealed partial class CK
        {
            public CK(int a) { A = a; }
            [Tag(1)] public int A { get; }
        }
        """;

    [Fact]
    public void RecordClassKey_CompareToNull_IsPositive_AndGeneratedCodeIsWarningFree()
    {
        var asm = CompileAndLoad(RecordClassKey);
        var t = asm.GetType("P.RK")!;
        var v = Activator.CreateInstance(t, 7)!;

        var compare = typeof(IComparable<>).MakeGenericType(t).GetMethod("CompareTo")!;
        compare.Invoke(v, [null]).Should().Be(1);
        Comparer<object>.Default.Compare(v, null).Should().BePositive();
    }

    [Fact]
    public void RecordClassKey_ComparisonOperators_AreNullSafe()
    {
        var asm = CompileAndLoad(RecordClassKey);
        var t = asm.GetType("P.RK")!;
        var v = Activator.CreateInstance(t, 7)!;

        bool Op(string name, object? l, object? r) =>
            (bool)t.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [l, r])!;

        Op("op_LessThan", null, v).Should().BeTrue();
        Op("op_LessThan", v, null).Should().BeFalse();
        Op("op_GreaterThan", v, null).Should().BeTrue();
        Op("op_GreaterThan", null, v).Should().BeFalse();
        Op("op_LessThanOrEqual", null, null).Should().BeTrue();
        Op("op_GreaterThanOrEqual", null, null).Should().BeTrue();
        Op("op_GreaterThanOrEqual", null, v).Should().BeFalse();
        Op("op_LessThanOrEqual", v, null).Should().BeFalse();
    }

    [Fact]
    public void PlainClassKey_NullEqualityAndComparison_FollowTheBclContract()
    {
        var asm = CompileAndLoad(PlainClassKey);
        var t = asm.GetType("P.CK")!;
        var v = Activator.CreateInstance(t, 3)!;
        var same = Activator.CreateInstance(t, 3)!;

        typeof(IEquatable<>).MakeGenericType(t).GetMethod("Equals")!.Invoke(v, [null]).Should().Be(false);
        typeof(IComparable<>).MakeGenericType(t).GetMethod("CompareTo")!.Invoke(v, [null]).Should().Be(1);

        bool Op(string name, object? l, object? r) =>
            (bool)t.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [l, r])!;

        Op("op_Equality", v, null).Should().BeFalse();
        Op("op_Equality", null, v).Should().BeFalse();
        Op("op_Equality", null, null).Should().BeTrue();
        Op("op_Inequality", v, null).Should().BeTrue();
        Op("op_Inequality", null, null).Should().BeFalse();
        Op("op_Equality", v, same).Should().BeTrue();
        Op("op_LessThan", null, v).Should().BeTrue();
        Op("op_GreaterThan", v, null).Should().BeTrue();
    }

    [Fact]
    public void RecordStructKey_Signatures_StayNonNullable()
    {
        var asm = CompileAndLoad("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [Key]
            public partial record struct SK([property: Tag(1)] int A);
            """);
        var t = asm.GetType("P.SK")!;
        var a = Activator.CreateInstance(t, 1)!;
        var b = Activator.CreateInstance(t, 2)!;
        typeof(IComparable<>).MakeGenericType(t).GetMethod("CompareTo")!.Invoke(a, [b]).Should().Be(-1);
    }

    // ───── Indexers are not row state ─────────────────────────────────

    [Fact]
    public void Key_RecordStructWithIndexer_GeneratesCompilingCode_AndIgnoresTheIndexer()
    {
        var asm = CompileAndLoad("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [Key]
            public partial record struct K([property: Tag(1)] int A)
            {
                public int this[int i] => A * i;
            }
            """);
        var t = asm.GetType("P.K")!;
        var a = Activator.CreateInstance(t, 1)!;
        var b = Activator.CreateInstance(t, 2)!;
        typeof(IComparable<>).MakeGenericType(t).GetMethod("CompareTo")!.Invoke(a, [b]).Should().Be(-1);
    }

    [Fact]
    public void StorageSerializable_RecordStructWithIndexer_GeneratesCompilingCode_AndRoundTripsProperties()
    {
        var asm = CompileAndLoad("""
            using Boutquin.Storage.Domain.Attributes;
            namespace P;
            [StorageSerializable]
            public partial record struct Rec([property: Tag(1)] int A)
            {
                public int this[int i] => A * i;
                public string this[string s] { get => s; set { } }
            }
            """);
        var t = asm.GetType("P.Rec")!;
        var v = Activator.CreateInstance(t, 41)!;
        using var ms = new MemoryStream();
        t.GetMethod("Serialize", [typeof(Stream)])!.Invoke(v, [ms]);
        ms.Position = 0;
        var back = t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static, [typeof(Stream)])!.Invoke(null, [ms])!;
        t.GetProperty("A")!.GetValue(back).Should().Be(41);
    }

    // ───── BSSG021 guard dominance ────────────────────────────────────

    private const string Preamble = """
        using System;
        using System.Buffers;
        using System.Buffers.Binary;
        using System.IO;
        """;

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string body)
    {
        var compilation = TestHelpers.CreateCompilationFromSources(new[] { Preamble + body });
        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new UnboundedDecodedLengthAllocationAnalyzer()));
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync().ConfigureAwait(true);
        return diagnostics.Where(d => d.Id == "BSSG021").ToImmutableArray();
    }

    [Fact]
    public async Task BoundNestedInCanSeekBlock_LeavesNonSeekablePathUnbounded_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            static class C {
                static byte[] DecodeFrame(Stream stream, ReadOnlySpan<byte> header) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (stream.CanSeek) {
                        if (length > stream.Length - stream.Position)
                            throw new InvalidDataException("corrupt");
                    }
                    return new byte[length];
                }
            }
            """);

        diagnostics.Should().ContainSingle("a bound that only runs on the seekable branch does not dominate the allocation");
    }

    [Fact]
    public async Task BoundNestedInElseOrLoop_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            static class C {
                static byte[] InElse(bool strict, ReadOnlySpan<byte> header) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (strict) { } else {
                        if (length > header.Length) throw new InvalidDataException("corrupt");
                    }
                    return new byte[length];
                }
                static byte[] InLoop(int n, ReadOnlySpan<byte> header) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    for (var i = 0; i < n; i++) {
                        if (length > header.Length) throw new InvalidDataException("corrupt");
                    }
                    return new byte[length];
                }
            }
            """);

        diagnostics.Should().HaveCount(2);
    }

    [Fact]
    public async Task GuardCallNestedInCanSeekBlock_IsFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            static class C {
                static void EnsureWithin(long length, long limit) { if (length > limit) throw new InvalidDataException("corrupt"); }
                static byte[] DecodeFrame(Stream stream, ReadOnlySpan<byte> header) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (stream.CanSeek) {
                        EnsureWithin(length, stream.Length);
                    }
                    return new byte[length];
                }
            }
            """);

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task BoundInUnconditionalBlockOrEnclosingAllocationBranch_IsNotFlagged()
    {
        var diagnostics = await AnalyzeAsync("""
            static class C {
                static byte[] InPlainBlock(ReadOnlySpan<byte> header) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    {
                        if (length > header.Length) throw new InvalidDataException("corrupt");
                    }
                    return new byte[length];
                }
                static byte[] AllocInsideSameBranch(Stream stream, ReadOnlySpan<byte> header) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (stream.CanSeek) {
                        if (length > stream.Length - stream.Position) throw new InvalidDataException("corrupt");
                        return new byte[length];
                    }
                    return Array.Empty<byte>();
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }
}
