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
            public partial record struct MyKey(long Value);
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
            public partial record struct MyValue(string Name, int Count);
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
            public partial record struct Attraction(string Name);
            [StorageSerializable]
            public partial record struct City(string Name, IEnumerable<Attraction> Attractions);
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
            public partial record struct MyKey(long Value);
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
            public partial record struct MyKey(long Value);
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
            public partial record struct MyKey(long Value);
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
            public partial record struct MyValue(string Name);
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
            public partial record struct MyValue(string Name);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generatedSource = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("MyValue"));

        generatedSource.Should().NotBeNull();
        generatedSource.Should().Contain("<auto-generated>");
        generatedSource.Should().Contain("#nullable enable");
        generatedSource.Should().Contain("#pragma warning disable 1591");
        generatedSource.Should().Contain("#pragma warning disable CA1036");
    }

    [Fact]
    public void Generated_Code_Has_GeneratedCodeAttribute()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct MyValue(string Name);
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
            public partial record struct MyKey(long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generatedSource = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("MyKey"));

        generatedSource.Should().NotBeNull();
        generatedSource.Should().Contain("global::System.IO.BinaryWriter");
        generatedSource.Should().Contain("global::System.IO.BinaryReader");
        generatedSource.Should().Contain("global::Boutquin.Storage.Domain.Interfaces.ISerializable<MyKey>");
    }

    [Fact]
    public void Collection_Serialization_Uses_Count_Prefix()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Item(string Name);
            [StorageSerializable]
            public partial record struct Container(string Label, IEnumerable<Item> Items);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generatedSource = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("Container") && s.Contains("Serialize"));

        generatedSource.Should().NotBeNull();
        // Count is written before iterating
        generatedSource.Should().Contain("writer.Write(ItemsList.Count)");
        // Deserialization reads count then loops
        generatedSource.Should().Contain("reader.ReadInt32()");
        generatedSource.Should().Contain("Item.Deserialize(stream)");
    }

    [Fact]
    public void String_Serialization_Includes_Null_Guard()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct MyValue(string Name);
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
            public partial record struct CompositeKey(string Region, long Id);
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
            public partial record struct MyKey(long Value);
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
