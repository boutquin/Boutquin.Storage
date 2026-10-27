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

// Contract: a get-only property on a [ColumnarStorable] type is excluded from the column plan
// only when it is derived state (a computed getter). A get-only AUTO-property has a backing
// field a constructor assigns — it is stored state, and with no column it would never be
// written and would rehydrate as default on every read. Such a type is rejected with a located
// BSSG025 error and no columnar code is emitted, as the row generator rejects the same shape.
//
// Contract: a nullable-annotated `string?` property on a [StorageSerializable] record writes no
// tag when its value is null — absent tag means null, the reader's default for that field and
// the contract `T?` and `Child?` already have. A value the generated reader produces from older
// bytes (the added field left at null) must therefore write back as exactly those bytes.
public sealed class ColumnarStoredPropertyAndNullStringContractTests
{
    static ColumnarStoredPropertyAndNullStringContractTests()
    {
        _ = typeof(Boutquin.Storage.Domain.Attributes.ColumnarStorableAttribute).FullName;
        _ = typeof(System.Buffers.Binary.BinaryPrimitives).Assembly.FullName;
        _ = typeof(System.Buffers.ArrayPool<byte>).Assembly.FullName;
        _ = typeof(System.IO.Stream).Assembly.FullName;
    }

    private static GeneratorDriverRunResult RunColumnar(string source)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create(
            "ColumnarStoredProperty_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return CSharpGeneratorDriver
            .Create(new ColumnarSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _)
            .GetRunResult();
    }

    public static TheoryData<string, string> StoredGetOnlyShapes() => new()
    {
        { "body auto-property assigned by a constructor", """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public Bar() { }
                public Bar(DateOnly date, double price, long volume) { Date = date; Price = price; Volume = volume; }
                public DateOnly Date { get; init; }
                public double Price { get; init; }
                public long Volume { get; }
            }
            """ },
        { "positional parameter shadowed by a get-only auto-property", """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record class Bar(DateOnly Date, double Price, long Volume)
            {
                public long Volume { get; } = Volume;
            }
            """ },
        { "record struct get-only auto-property", """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record struct Bar
            {
                public Bar(DateOnly date, long volume) { Date = date; Volume = volume; }
                public DateOnly Date { get; init; }
                public long Volume { get; }
            }
            """ },
        { "get-only property backed by the field keyword", """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public Bar() { }
                public Bar(DateOnly date, long volume) { Date = date; Volume = volume; }
                public DateOnly Date { get; init; }
                public long Volume { get => field; }
            }
            """ },
    };

    [Theory]
    [MemberData(nameof(StoredGetOnlyShapes))]
    public void GetOnlyAutoProperty_IsStoredState_IsRejectedWithLocatedBssg025(string shape, string source)
    {
        var result = RunColumnar(source);

        var error = result.Diagnostics.Should().ContainSingle(d => d.Id == "BSSG025", shape).Subject;
        error.Severity.Should().Be(DiagnosticSeverity.Error);
        error.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Should().Contain("Volume");
        error.Location.SourceSpan.Should().Be(
            new TextSpan(source.IndexOf("Volume { get", StringComparison.Ordinal), "Volume".Length),
            "the error is located on the stored get-only property itself");
        result.GeneratedTrees.Should().NotContain(
            t => t.FilePath.EndsWith("Bar.Columnar.g.cs", StringComparison.Ordinal),
            "no columnar code may be emitted that would silently drop the stored value");
    }

    [Theory]
    [InlineData("public double Mid => (High + Low) / 2.0;")]
    [InlineData("public double Mid { get { return (High + Low) / 2.0; } }")]
    [InlineData("public double Mid { get => (High + Low) / 2.0; }")]
    public void ComputedGetOnlyProperty_IsDerivedState_IsExcludedWithoutDiagnostic(string computed)
    {
        var result = RunColumnar($$"""
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                public double High { get; init; }
                public double Low { get; init; }
                {{computed}}
            }
            """);

        result.Diagnostics.Should().BeEmpty();
        var code = result.GeneratedTrees
            .Single(t => t.FilePath.EndsWith("Bar.Columnar.g.cs", StringComparison.Ordinal))
            .ToString();
        code.Should().NotContain("ColumnarFieldDescriptor(\"Mid\"");
        code.Should().Contain("ColumnarFieldDescriptor(\"High\"");
    }
    // ───── [StorageSerializable] string? null contract ───────────────

    private const string NullableStringRecords = """
        #nullable enable
        using Boutquin.Storage.Domain.Attributes;
        namespace P;
        [StorageSerializable]
        public partial record struct V1([property: Tag(1)] int Id);
        [StorageSerializable]
        public partial record struct V2([property: Tag(1)] int Id, [property: Tag(2)] string? Note, [property: Tag(3)] int? Count);
        [StorageSerializable]
        public partial record class C2([property: Tag(1)] int Id, [property: Tag(2)] string? Note);
        [StorageSerializable]
        public partial record struct NonNullable([property: Tag(1)] int Id, [property: Tag(2)] string Note);
        """;

    private static Assembly CompileAndLoadSerializable(string source)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create(
            "NullString_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var run = CSharpGeneratorDriver
            .Create(new StorageSourceGenerator().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _)
            .GetRunResult();
        run.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        using var ms = new MemoryStream();
        var emit = output.Emit(ms);
        emit.Success.Should().BeTrue(string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        ms.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    private static byte[] Serialize(object record)
    {
        using var stream = new MemoryStream();
        record.GetType().GetMethod("Serialize", [typeof(Stream)])!.Invoke(record, [stream]);
        return stream.ToArray();
    }

    private static object Deserialize(Type type, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return type.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static, [typeof(Stream)])!.Invoke(null, [stream])!;
    }

    [Fact]
    public void NullableString_WhenNull_WritesNoTag_AndOldBytesReadBackWriteBackUnchanged()
    {
        var asm = CompileAndLoadSerializable(NullableStringRecords);
        var v1 = asm.GetType("P.V1")!;
        var v2 = asm.GetType("P.V2")!;
        var c2 = asm.GetType("P.C2")!;

        var oldBytes = Serialize(Activator.CreateInstance(v1, 9)!);
        var migrated = Deserialize(v2, oldBytes);
        v2.GetProperty("Note")!.GetValue(migrated).Should().BeNull("an absent string? tag reads as null");

        var writeBack = () => Serialize(migrated);
        writeBack.Should().NotThrow("a value the generated reader produced must be writable by the generated writer");
        Serialize(migrated).Should().Equal(oldBytes, "a null string? writes no tag, like the null int? beside it");

        var fresh = Activator.CreateInstance(v2, 7, null, null)!;
        Deserialize(v2, Serialize(fresh)).Should().Be(fresh);
        var classRecord = Activator.CreateInstance(c2, 7, null)!;
        Deserialize(c2, Serialize(classRecord)).Should().Be(classRecord);
    }

    [Theory]
    [InlineData("")]
    [InlineData("America/Lima")]
    [InlineData("\u00e9\U0001F600")]
    public void NullableString_WhenPopulated_RoundTripsItsValue(string note)
    {
        var asm = CompileAndLoadSerializable(NullableStringRecords);
        var v2 = asm.GetType("P.V2")!;
        var record = Activator.CreateInstance(v2, 3, note, 4)!;
        var back = Deserialize(v2, Serialize(record));
        back.Should().Be(record);
        v2.GetProperty("Note")!.GetValue(back).Should().Be(note, "an empty string is a value, not an absent tag");
    }

    [Fact]
    public void NonNullableString_HoldingNull_StillFailsLoudAtSerialize()
    {
        var asm = CompileAndLoadSerializable(NullableStringRecords);
        var type = asm.GetType("P.NonNullable")!;
        var record = Activator.CreateInstance(type, 1, null)!;
        var act = () => Serialize(record);
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*cannot be null during serialization*");
    }
}
