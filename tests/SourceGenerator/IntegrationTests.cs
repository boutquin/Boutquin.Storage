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

public sealed class IntegrationTests
{
    [Fact]
    public void Key_Type_Compiles_Without_Errors()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct MyKey([property: Tag(1)] long Value);
            """;

        var (outputCompilation, _) = TestHelpers.RunGenerator(source);
        var compileDiagnostics = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        compileDiagnostics.Should().BeEmpty(
            because: "generated code for a [Key] type should compile without errors");
    }

    [Fact]
    public void StorageSerializable_Type_Compiles_Without_Errors()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct MyValue([property: Tag(1)] string Name, [property: Tag(2)] int Count);
            """;

        var (outputCompilation, _) = TestHelpers.RunGenerator(source);
        var compileDiagnostics = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        compileDiagnostics.Should().BeEmpty(
            because: "generated code for a [StorageSerializable] type should compile without errors");
    }

    [Fact]
    public void Nested_Type_With_Collection_Compiles_Without_Errors()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Attraction([property: Tag(1)] string Name);
            [StorageSerializable]
            public partial record struct City([property: Tag(1)] string Name, [property: Tag(2)] IEnumerable<Attraction> Attractions);
            """;

        var (outputCompilation, _) = TestHelpers.RunGenerator(source);
        var compileDiagnostics = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        compileDiagnostics.Should().BeEmpty(
            because: "generated code for types with annotated collections should compile without errors");
    }

    [Fact]
    public void Key_Type_Generates_Serialize_Method()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct MyKey([property: Tag(1)] long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generatedSource = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("MyKey"));

        generatedSource.Should().NotBeNull();
        generatedSource.Should().Contain("void Serialize(global::System.IO.Stream stream)");
        generatedSource.Should().Contain("static MyKey Deserialize(global::System.IO.Stream stream)");
    }

    [Fact]
    public void Key_Type_Generates_CompareTo_Method()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct MyKey([property: Tag(1)] long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generatedSource = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("MyKey"));

        generatedSource.Should().NotBeNull();
        generatedSource.Should().Contain("int CompareTo(MyKey other)");
        generatedSource.Should().Contain("ComparisonHelper.SafeCompareTo");
    }

    [Fact]
    public void Key_Type_Generates_Comparison_Operators()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct MyKey([property: Tag(1)] long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generatedSource = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("MyKey"));

        generatedSource.Should().NotBeNull();
        generatedSource.Should().Contain("operator <(");
        generatedSource.Should().Contain("operator >(");
        generatedSource.Should().Contain("operator <=(");
        generatedSource.Should().Contain("operator >=(");
    }

    [Fact]
    public void StorageSerializable_Does_Not_Generate_CompareTo()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct MyValue([property: Tag(1)] string Name);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generatedSource = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("MyValue"));

        generatedSource.Should().NotBeNull();
        generatedSource.Should().NotContain("CompareTo");
        generatedSource.Should().NotContain("IComparable");
    }

    [Fact]
    public void Generated_Code_Has_Auto_Generated_Header()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct MyValue([property: Tag(1)] string Name);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generatedSource = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("MyValue"));

        generatedSource.Should().NotBeNull();
        generatedSource.Should().Contain("<auto-generated>");
        generatedSource.Should().Contain("#nullable enable");
        // Pragma suppression removed: CS1591 is suppressed by the .g.cs extension recognised
        // by Roslyn as auto-generated; IDE0073 is suppressed via [*.g.cs] in .editorconfig;
        // CA1036 is handled by StorageDiagnosticSuppressor for [Key] types.
        generatedSource.Should().NotContain("#pragma warning disable");
    }

    [Fact]
    public void Generated_Code_Has_GeneratedCodeAttribute()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct MyValue([property: Tag(1)] string Name);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generatedSource = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("MyValue"));

        generatedSource.Should().NotBeNull();
        generatedSource.Should().Contain("GeneratedCodeAttribute");
        generatedSource.Should().Contain("ExcludeFromCodeCoverage");
    }

    [Fact]
    public void Generated_Code_Uses_Global_Prefixes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct MyKey([property: Tag(1)] long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generatedSource = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("MyKey"));

        generatedSource.Should().NotBeNull();
        // Phase 3 [Key] emits ordered-wire via OrderedByteHelpers (not BinaryWriter/Reader).
        // Lock the helper routing + the IStorageKey<T> aggregator interface declaration —
        // [Key] types implement the storage-key aggregator (which extends ISerializable,
        // IComparable, IEquatable) so engine signatures' `where TKey : IStorageKey<TKey>`
        // constraint is satisfied without consumers spelling out the three base interfaces.
        generatedSource.Should().Contain("global::Boutquin.Storage.Domain.Helpers.OrderedByteHelpers");
        generatedSource.Should().Contain("global::Boutquin.Storage.Domain.Interfaces.IStorageKey<MyKey>");
    }

    [Fact]
    public void Collection_Serialization_Uses_Count_Prefix()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Item([property: Tag(1)] string Name);
            [StorageSerializable]
            public partial record struct Container(
                [property: Tag(1)] string Label,
                [property: Tag(2)] IEnumerable<Item> Items);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generatedSource = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("Container") && s.Contains("Serialize"));

        generatedSource.Should().NotBeNull();
        // Phase 2 tagged wire + backpatch-LEN trick: collections are LEN-prefixed and
        // both the LEN and the count varint are reserved as 10-byte slots up front, then
        // backpatched once the body has streamed through __payload directly. This eliminates
        // the earlier __collBuf/__elemBuf double-buffering on the count-prefixed and
        // annotated paths. The padded LEN/count are still legal ULEB128 — the reader accepts
        // any encoding up to 10 bytes — so wire compatibility is preserved.
        generatedSource.Should().Contain("__payload.Reserve(10)");
        generatedSource.Should().Contain("WriteVarUInt64Padded(__payload.GetBackpatchSpan(__countOffset, 10), __elemCount, 10)");
        generatedSource.Should().Contain("__item.Serialize(__payload)");
        generatedSource.Should().Contain("Item.Deserialize(");
    }

    [Fact]
    public void String_Serialization_Includes_Null_Guard()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct MyValue([property: Tag(1)] string Name);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generatedSource = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("MyValue"));

        generatedSource.Should().NotBeNull();
        generatedSource.Should().Contain("InvalidOperationException");
        generatedSource.Should().Contain("cannot be null during serialization");
    }

    [Fact]
    public void Multi_Property_Key_Uses_Chained_Comparison()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct CompositeKey([property: Tag(1)] string Region, [property: Tag(2)] long Id);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generatedSource = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("CompositeKey"));

        generatedSource.Should().NotBeNull();
        // First property comparison stored in variable
        generatedSource.Should().Contain("var cmp0 = ");
        generatedSource.Should().Contain("if (cmp0 != 0) return cmp0;");
    }

    [Fact]
    public void Record_Struct_Key_Does_Not_Emit_Equality_Members()
    {
        // Record structs automatically synthesize Equals, ==, != — the generator should not re-emit them
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct MyKey([property: Tag(1)] long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generatedSource = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("MyKey"));

        generatedSource.Should().NotBeNull();
        // Record structs should not get generated Equals/==/!=
        generatedSource.Should().NotContain("bool Equals(MyKey other)");
        generatedSource.Should().NotContain("operator ==(");
        generatedSource.Should().NotContain("operator !=(");
        generatedSource.Should().NotContain("IEquatable");
    }
}
