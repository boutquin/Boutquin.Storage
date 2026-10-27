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

/// <summary>
/// Tests that the generator emits the runtime-visible <c>TaggedWireSchemaFields</c> /
/// <c>TaggedWireReservedTags</c> descriptors on <c>[StorageSerializable]</c> records — the bridge's
/// runtime tag metadata (the <c>[Tag]</c>/<c>[ReservedTag]</c> attributes are <c>[Conditional]</c> and
/// absent from IL, so a descriptor is the only way to surface tags at runtime).
/// </summary>
public sealed class TaggedWireSchemaDescriptorTests
{
    [Fact]
    public void StorageSerializable_Emits_TaggedWireSchemaFields_Descriptor()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Point([property: Tag(1)] int X, [property: Tag(2)] string Label);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generated = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("Point"));

        generated.Should().NotBeNull();
        generated.Should().Contain("TaggedWireSchemaFields");
        generated.Should().Contain("global::Boutquin.Storage.Domain.ValueObjects.TaggedWireField");
        generated.Should().Contain("1, \"X\"");
        generated.Should().Contain("2, \"Label\"");
    }

    [Fact]
    public void StorageSerializable_Emits_TaggedWireReservedTags_FromReservedTagAttribute()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            [ReservedTag(3)]
            public partial record struct Point([property: Tag(1)] int X, [property: Tag(2)] string Label);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generated = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("Point"));

        generated.Should().NotBeNull();
        generated.Should().Contain("TaggedWireReservedTags");
        generated.Should().Contain("3");
    }

    [Fact]
    public void KeyType_DoesNotEmit_TaggedWireSchemaFields()
    {
        // [Key] types use the ordered wire and are not registry-schema subjects; no descriptor.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct MyKey([property: Tag(1)] long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generated = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("MyKey"));

        generated.Should().NotBeNull();
        generated.Should().NotContain("TaggedWireSchemaFields");
    }

    [Fact]
    public void KeyType_Emits_OrderedWireLayout_RecordingEveryByteDecidingAttribute()
    {
        // The ordered wire is positional and carries no tags or names, so the layout records,
        // per field in declaration order: the encoded kind, a leading '?' for the null
        // discriminator, an enum's underlying kind (an enum is written as that integer) and a
        // decimal's [DecimalScale]. Names are absent — they never reach the wire.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            public enum Code : ushort { None = 0 }
            [Key]
            public partial record struct MyKey(
                [property: Tag(2)] string Name,
                [property: Tag(1)] long Value,
                [property: Tag(3)] int? Maybe,
                [property: Tag(4)] Code Kind,
                [property: Tag(6)] Code? MaybeKind,
                [property: Tag(5), DecimalScale(4)] decimal Price);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var generated = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .FirstOrDefault(s => s.Contains("MyKey"));

        generated.Should().NotBeNull();
        generated.Should().Contain(
            "public static string OrderedWireLayout { get; } = \"String,Int64,?Int32,UInt16,?UInt16,Decimal(4)\";");
    }
}
