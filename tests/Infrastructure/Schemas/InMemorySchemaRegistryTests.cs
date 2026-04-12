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
using Boutquin.Storage.Infrastructure.Schemas;

namespace Boutquin.Storage.Infrastructure.Tests.Schemas;

/// <summary>
/// Tests for <see cref="InMemorySchemaRegistry"/>.
/// </summary>
public sealed class InMemorySchemaRegistryTests
{
    private readonly InMemorySchemaRegistry _registry;

    public InMemorySchemaRegistryTests()
    {
        _registry = new InMemorySchemaRegistry(new FieldLevelCompatibilityChecker());
    }

    [Fact]
    public async Task RegisterFirstSchema_ReturnsVersion1()
    {
        // Arrange
        var schema = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false));

        // Act
        var version = await _registry.RegisterAsync("test-subject", schema, CompatibilityMode.Backward).ConfigureAwait(true);

        // Assert
        version.Value.Should().Be(1);
    }

    [Fact]
    public async Task RegisterCompatibleEvolution_ReturnsVersion2()
    {
        // Arrange
        var v1 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false));
        var v2 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false),
            new SchemaField("Age", SchemaFieldType.Int32, IsOptional: true)); // Optional new field = backward compatible

        // Act
        await _registry.RegisterAsync("subject", v1, CompatibilityMode.Backward).ConfigureAwait(true);
        var version = await _registry.RegisterAsync("subject", v2, CompatibilityMode.Backward).ConfigureAwait(true);

        // Assert
        version.Value.Should().Be(2);
    }

    [Fact]
    public async Task RegisterIncompatibleEvolution_Throws()
    {
        // Arrange
        var v1 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false),
            new SchemaField("Email", SchemaFieldType.String, IsOptional: false));
        // Removing required field is backward-incompatible
        var v2 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false));

        await _registry.RegisterAsync("subject", v1, CompatibilityMode.Backward).ConfigureAwait(true);

        // Act & Assert
        var act = () => _registry.RegisterAsync("subject", v2, CompatibilityMode.Backward).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task GetByVersion_ReturnsCorrectSchema()
    {
        // Arrange
        var v1 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false));
        var v2 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false),
            new SchemaField("Age", SchemaFieldType.Int32, IsOptional: true));

        await _registry.RegisterAsync("subject", v1, CompatibilityMode.None).ConfigureAwait(true);
        await _registry.RegisterAsync("subject", v2, CompatibilityMode.None).ConfigureAwait(true);

        // Act
        var result1 = await _registry.GetAsync("subject", new SchemaVersion(1)).ConfigureAwait(true);
        var result2 = await _registry.GetAsync("subject", new SchemaVersion(2)).ConfigureAwait(true);

        // Assert
        result1.Fields.Should().HaveCount(1);
        result2.Fields.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetMissingVersion_Throws()
    {
        var schema = new SimpleSchema(new SchemaField("X", SchemaFieldType.String, IsOptional: false));
        await _registry.RegisterAsync("subject", schema, CompatibilityMode.None).ConfigureAwait(true);

        var act = () => _registry.GetAsync("subject", new SchemaVersion(99)).AsTask();
        await act.Should().ThrowAsync<KeyNotFoundException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task GetMissingSubject_Throws()
    {
        var act = () => _registry.GetAsync("nonexistent", new SchemaVersion(1)).AsTask();
        await act.Should().ThrowAsync<KeyNotFoundException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task TransitiveCompatibility_V3IncompatibleWithV1_Throws()
    {
        // Arrange — v1 has required Name + required Email
        var v1 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false),
            new SchemaField("Email", SchemaFieldType.String, IsOptional: false));

        // v2: make Email optional (backward-compatible with v1)
        var v2 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false),
            new SchemaField("Email", SchemaFieldType.String, IsOptional: true));

        // v3: remove Email entirely — compatible with v2 (Email was optional there)
        // but INCOMPATIBLE with v1 (Email was required in v1)
        var v3 = new SimpleSchema(
            new SchemaField("Name", SchemaFieldType.String, IsOptional: false));

        await _registry.RegisterAsync("subject", v1, CompatibilityMode.Backward).ConfigureAwait(true);
        await _registry.RegisterAsync("subject", v2, CompatibilityMode.Backward).ConfigureAwait(true);

        // Act & Assert — transitive check catches v3 incompatibility with v1
        var act = () => _registry.RegisterAsync("subject", v3, CompatibilityMode.Backward).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(true);
    }
}
