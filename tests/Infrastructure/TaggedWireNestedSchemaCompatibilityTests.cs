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

// Two generations of one nested-record schema. The containing classes give each generation's
// records the same simple names (Money, Order) — the shape a type evolving in place, or moving
// namespace, presents to the checker — while letting both generations compile side by side.
public static partial class NestedEvolutionV1
{
    [StorageSerializable]
    public partial record struct Money([property: Tag(1)] long Cents);

    [StorageSerializable]
    public partial record struct Order([property: Tag(1)] string Id, [property: Tag(2)] Money Price);

    [StorageSerializable]
    public partial record struct Basket([property: Tag(1)] string Id, [property: Tag(2)] IEnumerable<Money> Items);

    [StorageSerializable]
    public partial record struct Narrow([property: Tag(1)] int Cents);

    [StorageSerializable]
    public partial record struct Holder([property: Tag(1)] Narrow Amount);
}

public static partial class NestedEvolutionV2
{
    // Tag 1 changes wire class (zig-zag varint → length-prefixed UTF-8): a v2 reader skips the
    // v1 bytes and default-fills.
    [StorageSerializable]
    public partial record struct Money([property: Tag(1)] string? Cents);

    [StorageSerializable]
    public partial record struct Order([property: Tag(1)] string Id, [property: Tag(2)] Money Price);

    [StorageSerializable]
    public partial record struct Basket([property: Tag(1)] string Id, [property: Tag(2)] IEnumerable<Money> Items);

    // Width change inside the nested record: backward-safe only.
    [StorageSerializable]
    public partial record struct Narrow([property: Tag(1)] long Cents);

    [StorageSerializable]
    public partial record struct Holder([property: Tag(1)] Narrow Amount);
}

public static partial class NestedEvolutionV3
{
    // A wire-compatible nested evolution: tag 1 unchanged, optional tag 2 added.
    [StorageSerializable]
    public partial record struct Money([property: Tag(1)] long Cents, [property: Tag(2)] string? Currency);

    [StorageSerializable]
    public partial record struct Order([property: Tag(1)] string Id, [property: Tag(2)] Money Price);
}

/// <summary>
/// The tagged-wire compatibility rules applied THROUGH a nested <c>[StorageSerializable]</c> field:
/// a nested record's own tagged fields are part of its wire identity, so a change to them is
/// judged exactly as the same change made at top level would be.
/// </summary>
public sealed class TaggedWireNestedSchemaCompatibilityTests
{
    private static readonly TaggedWireCompatibilityChecker s_checker = new();

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void NestedRecordTagChangingWireClass_IsIncompatible_InEveryMode(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<NestedEvolutionV1.Order>();
        var v2 = TaggedWireSchema.FromType<NestedEvolutionV2.Order>();

        s_checker.IsCompatible(v1, v2, mode).Should().BeFalse(
            "the nested Money's tag 1 leaves its wire class (long → string), which the generated reader "
            + "skips and default-fills — the same change is rejected at top level");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void CollectionOfNestedRecords_WhoseElementChangesWireClass_IsIncompatible(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<NestedEvolutionV1.Basket>();
        var v2 = TaggedWireSchema.FromType<NestedEvolutionV2.Basket>();

        s_checker.IsCompatible(v1, v2, mode).Should().BeFalse(
            "each element of IEnumerable<Money> is a nested Money, whose tag 1 leaves its wire class");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward, true)]
    [InlineData(CompatibilityMode.Forward, false)]
    [InlineData(CompatibilityMode.Full, false)]
    public void NestedRecordWidening_FollowsTheTopLevelModeRule(CompatibilityMode mode, bool expected)
    {
        var v1 = TaggedWireSchema.FromType<NestedEvolutionV1.Holder>();
        var v2 = TaggedWireSchema.FromType<NestedEvolutionV2.Holder>();
        var topLevel = s_checker.IsCompatible(
            TaggedWireSchema.FromType<NestedEvolutionV1.Narrow>(), TaggedWireSchema.FromType<NestedEvolutionV2.Narrow>(), mode);

        topLevel.Should().Be(expected, "int → long is widening: backward-safe only");
        s_checker.IsCompatible(v1, v2, mode).Should().Be(
            topLevel, "a nested width change must be judged exactly as the same change made at top level");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void NestedRecordAddingAnOptionalTag_StaysCompatible(CompatibilityMode mode)
    {
        var v1 = TaggedWireSchema.FromType<NestedEvolutionV1.Order>();
        var v3 = TaggedWireSchema.FromType<NestedEvolutionV3.Order>();

        s_checker.IsCompatible(v1, v3, mode).Should().BeTrue(
            "adding a tag inside the nested record is skipped by old readers and default-filled by new ones");
        s_checker.IsCompatible(v1, TaggedWireSchema.FromType<NestedEvolutionV1.Order>(), mode).Should().BeTrue(
            "an unchanged nested record is compatible with itself");
    }

    [Fact]
    public async Task Registry_RejectsNestedWireClassChange_UnderFull()
    {
        var registry = new InMemorySchemaRegistry(s_checker);
        await registry.RegisterAsync("order", TaggedWireSchema.FromType<NestedEvolutionV1.Order>(), CompatibilityMode.Full);

        var act = async () => await registry.RegisterAsync(
            "order", TaggedWireSchema.FromType<NestedEvolutionV2.Order>(), CompatibilityMode.Full);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void NestedRecordEvolvingInPlace_UnderAnUnchangedTypeName_IsIncompatible(CompatibilityMode mode)
    {
        // The deployment-to-deployment case: the field's declared type name is byte-identical on
        // both sides; only the nested record's own descriptor tells the two generations apart.
        var existing = Order(new TaggedWireSchema([new TaggedWireField(1, "Cents", "long", IsOptional: false)]));
        var candidate = Order(new TaggedWireSchema([new TaggedWireField(1, "Cents", "string?", IsOptional: true)]));

        s_checker.IsCompatible(existing, candidate, mode).Should().BeFalse(
            "the nested record's tag 1 left its wire class even though its type name did not change");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void NestedDescriptorOnOnlyOneSide_FailsClosed(CompatibilityMode mode)
    {
        var withDescriptor = Order(new TaggedWireSchema([new TaggedWireField(1, "Cents", "long", IsOptional: false)]));
        var withoutDescriptor = new TaggedWireSchema(withDescriptor.TaggedFields);

        s_checker.IsCompatible(withDescriptor, withoutDescriptor, mode).Should().BeFalse(
            "an unverifiable nested record must not be assumed unchanged");
        s_checker.IsCompatible(withoutDescriptor, withDescriptor, mode).Should().BeFalse(
            "an unverifiable nested record must not be assumed unchanged");
    }

    [Fact]
    public void FromType_CapturesNestedRecordSchemas_ByTag_ForDirectAndCollectionFields()
    {
        var order = TaggedWireSchema.FromType<NestedEvolutionV1.Order>();
        var basket = TaggedWireSchema.FromType<NestedEvolutionV1.Basket>();

        order.NestedSchemas.Keys.Should().Equal(2);
        order.NestedSchemas[2].TaggedFields.Should().Equal(TaggedWireSchema.FromType<NestedEvolutionV1.Money>().TaggedFields);
        basket.NestedSchemas.Keys.Should().Equal(2);
        basket.NestedSchemas[2].TaggedFields.Should().Equal(TaggedWireSchema.FromType<NestedEvolutionV1.Money>().TaggedFields);
    }

    private static TaggedWireSchema Order(TaggedWireSchema money) => new(
        [new TaggedWireField(1, "Id", "string", IsOptional: false), new TaggedWireField(2, "Price", "Shop.Money", IsOptional: false)],
        nestedSchemas: new Dictionary<int, TaggedWireSchema> { [2] = money });
}
