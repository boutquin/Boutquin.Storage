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

// Phase 3 key-shape diagnostics: BSSG014 forbids nested annotated records
// inside a [Key] (ordered wire is flat), BSSG015 forbids collections (no length semantics
// in ordered wire), BSSG016 forbids nested-nullable shapes (only leaf-level nullability).
public sealed class KeyShapeValidationTests
{
    // BSSG014 — nested [StorageSerializable] record inside a [Key].
    [Fact]
    public void BSSG014_Fires_When_Key_Contains_Nested_Annotated_Record()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Inner([property: Tag(1)] long Value);
            [Key]
            public partial record struct BadKey(
                [property: Tag(1)] long A,
                [property: Tag(2)] Inner Child);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG014");
    }

    // BSSG015 — IEnumerable<T> property of a [Key] type.
    [Fact]
    public void BSSG015_Fires_When_Key_Contains_Collection()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNamespace;
            [Key]
            public partial record struct BadKey(
                [property: Tag(1)] long A,
                [property: Tag(2)] IEnumerable<string> Tags);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG015");
    }

    [Fact]
    public void BSSG015_Fires_On_List_Property()
    {
        // List<T> implements IEnumerable<T> so it must also trigger BSSG015 in key position.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNamespace;
            [Key]
            public partial record struct BadKey(
                [property: Tag(1)] long A,
                [property: Tag(2)] List<string> Tags);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG015");
    }

    // BSSG016 — nullable nested value inside [Key] is flagged. Leaf-level nullability via
    // the 1-byte discriminator is supported; nullable-of-annotated (record?) is not.
    [Fact]
    public void BSSG016_Fires_When_Key_Contains_Nullable_Nested_Record()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Inner([property: Tag(1)] long Value);
            [Key]
            public partial record struct BadKey(
                [property: Tag(1)] long A,
                [property: Tag(2)] Inner? Child);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        // Nullable-of-annotated-record → BSSG016 (nested nullable) at minimum; may also
        // trigger BSSG014 depending on implementation. Lock BSSG016 firing explicitly.
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG016");
    }

    // BSSG019 — nullable reference string (string?) on a [Key]. The Nullable<T> unwrap only
    // covers value types, so string? classifies as a plain String: the record compiles with
    // zero diagnostics, a null-keyed instance orders fine in a memtable (CompareStringByteLex
    // sorts null first), and the failure surfaces only at flush time when Serialize throws.
    // No leaf-level null discriminator exists for strings, so this shape must fail at compile
    // time like the other unsupported key shapes (BSSG014/015/016).
    [Fact]
    public void BSSG019_Fires_When_Key_Contains_Nullable_String()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            #nullable enable
            namespace TestNamespace;
            [Key]
            public partial record struct BadKey(
                [property: Tag(1)] long A,
                [property: Tag(2)] string? Name);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG019");
    }

    // Positive case — leaf-level nullability on a key is allowed.
    [Fact]
    public void No_Diagnostics_For_Key_With_Leaf_Nullable_Primitive()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct GoodKey(
                [property: Tag(1)] long A,
                [property: Tag(2)] int? MaybeId);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().NotContain(d => d.Id == "BSSG014" || d.Id == "BSSG015" || d.Id == "BSSG016");
    }

    // Positive case — [StorageSerializable] with any nested / collection / nullable shape
    // is NOT affected by BSSG014-016 (ordered-wire rules apply only to [Key]).
    [Fact]
    public void BSSG014_Does_Not_Fire_On_StorageSerializable_With_Nested()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Inner([property: Tag(1)] long Value);
            [StorageSerializable]
            public partial record struct Outer(
                [property: Tag(1)] long A,
                [property: Tag(2)] Inner Child);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().NotContain(d => d.Id == "BSSG014" || d.Id == "BSSG015" || d.Id == "BSSG016");
    }
}
