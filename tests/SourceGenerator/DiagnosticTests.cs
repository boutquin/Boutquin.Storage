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
    [Fact]
    public void BSSG001_Unsupported_Property_Type()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct BadType(System.DateTime Timestamp);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var diagnostics = runResult.Diagnostics;

        diagnostics.Should().ContainSingle(d => d.Id == "BSSG001");
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
            public partial class NotARecordStruct
            {
                public string Name { get; set; }
            }
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);

        // Code should still be generated despite the advisory warning
        runResult.GeneratedTrees.Should().NotBeEmpty();
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
    public void BSSG001_Unsupported_Collection_Element_Type()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct BadCollection(IEnumerable<System.DateTime> Dates);
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
            public partial record struct ValidKey(long Value);
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
            public partial record struct ValidValue(string Name, int Count);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);

        runResult.Diagnostics.Should().BeEmpty();
    }
}
