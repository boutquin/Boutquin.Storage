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
using Boutquin.Storage.Domain.Interfaces.Schemas;
using Boutquin.Storage.Infrastructure.Schemas;
using Boutquin.Storage.Infrastructure.Tests.Schemas;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Contract tests every <see cref="IAvroStyleVersionedSerializer{T}"/> implementation must pass
/// identically. A caller written against the interface must see the same guard from each member
/// of the family: a divergent sibling silently does the wrong thing for that caller.
/// </summary>
public sealed class VersionedSerializerContractTests
{
    public static TheoryData<string> Implementations => new() { "json", "tagged-wire" };

    private static IAvroStyleVersionedSerializer<BridgePerson> Create(string implementation) => implementation switch
    {
        "json" => new JsonVersionedSerializer<BridgePerson>(requireExtensionData: false),
        "tagged-wire" => new TaggedWireVersionedSerializer<BridgePerson>(),
        _ => throw new ArgumentOutOfRangeException(nameof(implementation), implementation, null),
    };

    private static InMemorySchemaRegistry RegistryWithPersonV1()
    {
        var registry = new InMemorySchemaRegistry(new TaggedWireCompatibilityChecker());
        _ = registry.RegisterAsync(
            "person", TaggedWireSchema.FromType<BridgePerson>(), CompatibilityMode.Backward).AsTask().GetAwaiter().GetResult();
        return registry;
    }

    // default(SchemaVersion) is not a version: every reader in the family rejects a zero header,
    // so every writer must refuse it with ArgumentException before any byte is written.
    [Theory]
    [MemberData(nameof(Implementations))]
    public async Task SerializeAsync_DefaultSchemaVersion_IsRefusedByEveryImplementation(string implementation)
    {
        var serializer = Create(implementation);
        using var ms = new MemoryStream();

        var act = async () => await serializer.SerializeAsync(ms, new BridgePerson("Ada", 36), default).ConfigureAwait(true);

        await act.Should().ThrowAsync<ArgumentException>(
            $"the {implementation} serializer must refuse a version its reader rejects").ConfigureAwait(true);
        ms.Length.Should().Be(0, "a refused record must leave no bytes on the stream");
    }

    // The accepting half of the same contract: a positive registered version round-trips.
    [Theory]
    [MemberData(nameof(Implementations))]
    public async Task SerializeThenDeserialize_PositiveVersion_RoundTripsInEveryImplementation(string implementation)
    {
        var serializer = Create(implementation);
        var original = new BridgePerson("Ada", 36);

        using var ms = new MemoryStream();
        await serializer.SerializeAsync(ms, original, new SchemaVersion(1)).ConfigureAwait(true);
        ms.Position = 0;
        var (version, value) = await serializer.DeserializeAsync(ms, RegistryWithPersonV1(), "person").ConfigureAwait(true);

        version.Value.Should().Be(1);
        value.Should().Be(original);
    }
}
