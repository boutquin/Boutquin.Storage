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
using Boutquin.Storage.Domain.Attributes;
using Boutquin.Storage.Infrastructure.Schemas;

namespace Boutquin.Storage.Infrastructure.Tests;

// Generations of nested generated records whose SIMPLE NAMES are integer keywords (Long, UInt64).
// A legal type name must not route a nested record into the integer width rule: its bytes are
// decided by its own layout / tagged schema, not by what its name happens to spell.
public static partial class KeywordNamedNestedV1
{
    [Key]
    public partial record struct Long([property: Tag(1)] int A);

    [StorageSerializable]
    public partial record struct Parent([property: Tag(1)] string Id, [property: Tag(2)] Long C);

    [Key]
    public partial record struct UInt64([property: Tag(1)] long A);

    [StorageSerializable]
    public partial record struct WideParent([property: Tag(1)] UInt64 C);

    [StorageSerializable]
    public partial record struct Int32([property: Tag(1)] int A);

    [StorageSerializable]
    public partial record struct RecordParent([property: Tag(1)] Int32 R);

    [StorageSerializable]
    public partial record struct PrimitiveParent([property: Tag(1)] string Id, [property: Tag(2)] long C);
}

public static partial class KeywordNamedNestedV2
{
    // Signedness change inside a [Key] child named Long: old int bytes decode as a different uint.
    [Key]
    public partial record struct Long([property: Tag(1)] uint A);

    [StorageSerializable]
    public partial record struct Parent([property: Tag(1)] string Id, [property: Tag(2)] Long C);

    // A [Key] child named UInt64 whose ordered layout is unchanged: renaming nothing, changing nothing.
    [Key]
    public partial record struct UInt64([property: Tag(1)] long A);

    [StorageSerializable]
    public partial record struct WideParent([property: Tag(1)] UInt64 C);

    // A nested [StorageSerializable] record named Int32 whose stable tag 1 changed class
    // (int → string): fatal on the tagged wire in every mode.
    [StorageSerializable]
    public partial record struct Int32([property: Tag(1)] string A);

    [StorageSerializable]
    public partial record struct RecordParent([property: Tag(1)] Int32 R);
}

/// <summary>
/// A nested generated record whose simple name is an integer keyword (<c>Long</c>, <c>UInt64</c>,
/// <c>Int32</c>) is judged by its captured descriptor — the <c>[Key]</c> ordered-wire layout or the
/// nested tagged-wire schema — never by the integer width its name would imply.
/// </summary>
public sealed class TaggedWireKeywordNamedKeyLayoutTests
{
    private static readonly TaggedWireCompatibilityChecker s_checker = new();

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void KeyChildNamedLong_WhoseLayoutChanges_IsIncompatible_InEveryMode(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<KeywordNamedNestedV1.Parent>();
        var v2 = TaggedWireSchema.FromType<KeywordNamedNestedV2.Parent>();

        v1.NestedKeyLayouts[2].Should().NotBe(v2.NestedKeyLayouts[2], "the fixture must actually change the layout");
        s_checker.IsCompatible(v1, v2, mode).Should().BeFalse(
            "the [Key] child named 'Long' changed its ordered layout (int → uint); its name is not an integer width");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void KeyChildNamedUInt64_WithIdenticalLayout_IsCompatible_InEveryMode(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<KeywordNamedNestedV1.WideParent>();
        var v2 = TaggedWireSchema.FromType<KeywordNamedNestedV2.WideParent>();

        s_checker.IsCompatible(v1, v2, mode).Should().BeTrue(
            "the [Key] child's ordered layout is identical, so the bytes are unchanged");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void NestedRecordNamedInt32_WhoseOwnTagChangesClass_IsIncompatible_InEveryMode(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<KeywordNamedNestedV1.RecordParent>();
        var v2 = TaggedWireSchema.FromType<KeywordNamedNestedV2.RecordParent>();

        s_checker.IsCompatible(v1, v2, mode).Should().BeFalse(
            "the nested [StorageSerializable] record named 'Int32' changed its own tag 1 from int to string");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void PrimitiveLong_ReplacedByKeyChildNamedLong_IsIncompatible_InEveryMode(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<KeywordNamedNestedV1.PrimitiveParent>();
        var v2 = TaggedWireSchema.FromType<KeywordNamedNestedV2.Parent>();

        s_checker.IsCompatible(v1, v2, mode).Should().BeFalse(
            "a zig-zag varint long and an ordered-wire [Key] record share only a spelling, not bytes");
    }
}
