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

public sealed class SnapshotTests
{
    [Fact]
    public Task Key_Type_Generates_Serializable_And_Comparable()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct MyKey(long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        return Verify(runResult);
    }

    [Fact]
    public Task StorageSerializable_Type_Generates_Only_Serialization()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct MyValue(string Name, int Count);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        return Verify(runResult);
    }

    [Fact]
    public Task Key_With_Multiple_Properties_Generates_Chained_CompareTo()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct CompositeKey(string Region, long Id);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        return Verify(runResult);
    }

    [Fact]
    public Task StorageSerializable_With_Nested_Annotated_Type()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Inner(string Name);
            [StorageSerializable]
            public partial record struct Outer(string Label, Inner Child);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        return Verify(runResult);
    }

    [Fact]
    public Task StorageSerializable_With_Collection_Of_Annotated_Type()
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
        return Verify(runResult);
    }

    [Fact]
    public Task StorageSerializable_With_Primitive_Collection()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Tags(string Label, IEnumerable<string> Values);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        return Verify(runResult);
    }

    [Fact]
    public Task Key_In_Global_Namespace()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            [Key]
            public partial record struct GlobalKey(int Id);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        return Verify(runResult);
    }

    [Fact]
    public Task All_Primitive_Types_Are_Supported()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct AllPrimitives(
                int IntProp, long LongProp, string StringProp,
                float FloatProp, double DoubleProp, bool BoolProp,
                byte ByteProp, char CharProp);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        return Verify(runResult);
    }

    [Fact]
    public Task Nested_Type_Inside_Parent_Class()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            public partial class Outer
            {
                [Key]
                public partial record struct InnerKey(long Value);
            }
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        return Verify(runResult);
    }

    [Fact]
    public Task Internal_Type_Gets_Internal_Accessibility()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            internal partial record struct InternalValue(string Data);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        return Verify(runResult);
    }
}
