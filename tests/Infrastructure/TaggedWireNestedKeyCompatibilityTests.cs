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

// Generations of a [Key] record nested inside a [StorageSerializable] parent. The containing
// classes give each generation's records the same simple names — the shape a type evolving in
// place presents to the checker — while letting the generations compile side by side.
public static partial class NestedKeyEvolutionV1
{
    public enum Code { None = 0, Some = 1 }

    [Key]
    public partial record struct Child([property: Tag(1)] int A);

    [StorageSerializable]
    public partial record struct Parent([property: Tag(1)] string Id, [property: Tag(2)] Child C);

    [StorageSerializable]
    public partial record struct Bag([property: Tag(1)] string Id, [property: Tag(2)] IEnumerable<Child> Items);

    [Key]
    public partial record struct Pair([property: Tag(1)] int A, [property: Tag(2)] string B);

    [StorageSerializable]
    public partial record struct PairHolder([property: Tag(1)] Pair P);

    [Key]
    public partial record struct Priced([property: Tag(1), DecimalScale(2)] decimal Amount);

    [StorageSerializable]
    public partial record struct PricedHolder([property: Tag(1)] Priced P);

    [Key]
    public partial record struct Coded([property: Tag(1)] Code K);

    [StorageSerializable]
    public partial record struct CodedHolder([property: Tag(1)] Coded C);
}

public static partial class NestedKeyEvolutionV2
{
    // Underlying type changed, name unchanged: an enum is written as its underlying integer.
    public enum Code : long { None = 0, Some = 1 }

    // Signedness change: the ordered wire has no type information, so old int bytes decode as a
    // different uint with no error.
    [Key]
    public partial record struct Child([property: Tag(1)] uint A);

    [StorageSerializable]
    public partial record struct Parent([property: Tag(1)] string Id, [property: Tag(2)] Child C);

    [StorageSerializable]
    public partial record struct Bag([property: Tag(1)] string Id, [property: Tag(2)] IEnumerable<Child> Items);

    // Field renames only: the ordered wire carries no names, so the bytes are unchanged.
    [Key]
    public partial record struct Pair([property: Tag(1)] int First, [property: Tag(2)] string Second);

    [StorageSerializable]
    public partial record struct PairHolder([property: Tag(1)] Pair P);

    // Scale change: the ordered wire stores the decimal as a scaled integer, so old bytes read
    // under the new scale decode to a value 100 times smaller.
    [Key]
    public partial record struct Priced([property: Tag(1), DecimalScale(4)] decimal Amount);

    [StorageSerializable]
    public partial record struct PricedHolder([property: Tag(1)] Priced P);

    [Key]
    public partial record struct Coded([property: Tag(1)] Code K);

    [StorageSerializable]
    public partial record struct CodedHolder([property: Tag(1)] Coded C);
}

public static partial class NestedKeyEvolutionV3
{
    // Widening: on the tagged wire int → long is backward-safe, but the ordered wire is
    // fixed-width (4 bytes vs 8), so no reader of either generation decodes the other's bytes.
    [Key]
    public partial record struct Child([property: Tag(1)] long A);

    [StorageSerializable]
    public partial record struct Parent([property: Tag(1)] string Id, [property: Tag(2)] Child C);

    // An added trailing field: the ordered wire is positional and carries no tags to skip.
    [Key]
    public partial record struct Pair([property: Tag(1)] int A, [property: Tag(2)] string B, [property: Tag(3)] int C);

    [StorageSerializable]
    public partial record struct PairHolder([property: Tag(1)] Pair P);
}

/// <summary>
/// The wire-compatibility rules applied THROUGH a field holding a nested <c>[Key]</c> record. A
/// <c>[Key]</c> child is written on the flat ordered wire — positional, fixed-width, no tags — so
/// its type name says nothing about its bytes and none of the tagged-wire evolution rules apply:
/// the child's ordered layout must be identical in every mode.
/// </summary>
public sealed class TaggedWireNestedKeyCompatibilityTests
{
    private static readonly TaggedWireCompatibilityChecker s_checker = new();

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void KeyChildFieldChangingSignedness_IsIncompatible_InEveryMode(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<NestedKeyEvolutionV1.Parent>();
        var v2 = TaggedWireSchema.FromType<NestedKeyEvolutionV2.Parent>();

        s_checker.IsCompatible(v1, v2, mode).Should().BeFalse(
            "the nested [Key] child's int field became uint — old ordered-wire bytes decode to different values");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void CollectionOfKeyChildren_WhoseLayoutChanges_IsIncompatible_InEveryMode(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<NestedKeyEvolutionV1.Bag>();
        var v2 = TaggedWireSchema.FromType<NestedKeyEvolutionV2.Bag>();

        s_checker.IsCompatible(v1, v2, mode).Should().BeFalse(
            "each element of IEnumerable<Child> is a [Key] child whose ordered layout changed");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void KeyChildFieldWidening_IsIncompatible_EvenWhenTheTaggedRuleWouldAllowIt(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<NestedKeyEvolutionV1.Parent>();
        var v3 = TaggedWireSchema.FromType<NestedKeyEvolutionV3.Parent>();

        s_checker.IsCompatible(v1, v3, mode).Should().BeFalse(
            "the ordered wire writes int as 4 bytes and long as 8 — neither generation reads the other");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void KeyChildGainingAField_IsIncompatible_InEveryMode(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<NestedKeyEvolutionV1.PairHolder>();
        var v3 = TaggedWireSchema.FromType<NestedKeyEvolutionV3.PairHolder>();

        s_checker.IsCompatible(v1, v3, mode).Should().BeFalse(
            "the ordered wire is positional with no tags, so an added field cannot be skipped or default-filled");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void KeyChildDecimalScaleChange_IsIncompatible_InEveryMode(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<NestedKeyEvolutionV1.PricedHolder>();
        var v2 = TaggedWireSchema.FromType<NestedKeyEvolutionV2.PricedHolder>();

        s_checker.IsCompatible(v1, v2, mode).Should().BeFalse(
            "the ordered wire stores a decimal as an integer at its [DecimalScale]; a new scale reinterprets old bytes");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void KeyChildEnumUnderlyingTypeChange_IsIncompatible_InEveryMode(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<NestedKeyEvolutionV1.CodedHolder>();
        var v2 = TaggedWireSchema.FromType<NestedKeyEvolutionV2.CodedHolder>();

        s_checker.IsCompatible(v1, v2, mode).Should().BeFalse(
            "the enum keeps its name but is written as a 4-byte int in one generation and an 8-byte long in the other");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void KeyChildWithRenamedFieldsOnly_StaysCompatible(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<NestedKeyEvolutionV1.PairHolder>();
        var v2 = TaggedWireSchema.FromType<NestedKeyEvolutionV2.PairHolder>();

        s_checker.IsCompatible(v1, v2, mode).Should().BeTrue(
            "the ordered wire carries no field names, so a rename leaves the bytes unchanged");
        s_checker.IsCompatible(v1, TaggedWireSchema.FromType<NestedKeyEvolutionV1.PairHolder>(), mode).Should().BeTrue(
            "an unchanged [Key] child is compatible with itself");
    }

    [Fact]
    public async Task Registry_RejectsKeyChildLayoutChange_UnderFull()
    {
        var registry = new InMemorySchemaRegistry(s_checker);
        await registry.RegisterAsync("parent", TaggedWireSchema.FromType<NestedKeyEvolutionV1.Parent>(), CompatibilityMode.Full);

        var act = async () => await registry.RegisterAsync(
            "parent", TaggedWireSchema.FromType<NestedKeyEvolutionV2.Parent>(), CompatibilityMode.Full);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void FromType_CapturesKeyChildLayouts_ByTag_ForDirectAndCollectionFields()
    {
        var parent = TaggedWireSchema.FromType<NestedKeyEvolutionV1.Parent>();
        var bag = TaggedWireSchema.FromType<NestedKeyEvolutionV1.Bag>();
        var pairs = TaggedWireSchema.FromType<NestedKeyEvolutionV1.PairHolder>();

        parent.NestedKeyLayouts.Should().Equal(new Dictionary<int, string> { [2] = "Int32" });
        bag.NestedKeyLayouts.Should().Equal(new Dictionary<int, string> { [2] = "Int32" });
        pairs.NestedKeyLayouts.Should().Equal(new Dictionary<int, string> { [1] = "Int32,String" });
        parent.NestedSchemas.Should().BeEmpty("a [Key] child has no tagged-wire schema");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void KeyLayoutOnOnlyOneSide_FailsClosed(CompatibilityMode mode)
    {
        TaggedWireField[] fields = [new(1, "Id", "string", IsOptional: false), new(2, "C", "Shop.Child", IsOptional: false)];
        var withLayout = new TaggedWireSchema(fields, nestedKeyLayouts: new Dictionary<int, string> { [2] = "Int32" });
        var withoutLayout = new TaggedWireSchema(fields);

        s_checker.IsCompatible(withLayout, withoutLayout, mode).Should().BeFalse(
            "an unverifiable [Key] child must not be assumed unchanged");
        s_checker.IsCompatible(withoutLayout, withLayout, mode).Should().BeFalse(
            "an unverifiable [Key] child must not be assumed unchanged");
    }
}
