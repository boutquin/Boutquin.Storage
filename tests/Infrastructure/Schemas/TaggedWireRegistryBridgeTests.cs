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

namespace Boutquin.Storage.Infrastructure.Tests.Schemas;

// Real [StorageSerializable] records — the source generator (referenced as an analyzer by this
// test project) emits the TaggedWireSchemaFields / TaggedWireReservedTags descriptors that
// TaggedWireSchema.FromType<T>() reads.
[StorageSerializable]
public partial record struct BridgePerson([property: Tag(1)] string Name, [property: Tag(2)] int Age);

[StorageSerializable]
public partial record struct BridgePersonAddOptional(
    [property: Tag(1)] string Name,
    [property: Tag(2)] int Age,
    [property: Tag(3)] string? Email);

[StorageSerializable]
public partial record struct BridgePersonTypeChange([property: Tag(1)] int Name, [property: Tag(2)] int Age);

/// <summary>
/// End-to-end tests for the real-type bridge: building a <see cref="TaggedWireSchema"/> from a
/// generated descriptor and evolving it through <see cref="InMemorySchemaRegistry"/> (G1).
/// </summary>
public sealed class TaggedWireRegistryBridgeTests
{
    [Fact]
    public void FromType_ReadsGeneratedDescriptor()
    {
        var schema = TaggedWireSchema.FromType<BridgePerson>();

        schema.TaggedFields.Select(f => (f.Tag, f.Name)).Should().Equal((1, "Name"), (2, "Age"));
    }

    [Fact]
    public async Task Registry_AcceptsBackwardCompatibleEvolution()
    {
        var registry = new InMemorySchemaRegistry(new TaggedWireCompatibilityChecker());

        var v1 = await registry.RegisterAsync(
            "person", TaggedWireSchema.FromType<BridgePerson>(), CompatibilityMode.Backward);
        var v2 = await registry.RegisterAsync(
            "person", TaggedWireSchema.FromType<BridgePersonAddOptional>(), CompatibilityMode.Backward);

        v1.Value.Should().Be(1);
        v2.Value.Should().Be(2);
    }

    [Fact]
    public async Task Registry_RejectsTypeChangeOnStableTag()
    {
        var registry = new InMemorySchemaRegistry(new TaggedWireCompatibilityChecker());
        await registry.RegisterAsync(
            "person", TaggedWireSchema.FromType<BridgePerson>(), CompatibilityMode.Backward);

        var act = async () => await registry.RegisterAsync(
            "person", TaggedWireSchema.FromType<BridgePersonTypeChange>(), CompatibilityMode.Backward);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
