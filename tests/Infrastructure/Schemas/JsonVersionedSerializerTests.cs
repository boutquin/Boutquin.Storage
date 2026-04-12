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
/// Tests for <see cref="JsonVersionedSerializer{T}"/>.
/// </summary>
public sealed class JsonVersionedSerializerTests
{
    private sealed record TestPayload(string Name, int Age);

    // For testing old → new evolution: new version has optional Email field
    private sealed record TestPayloadV2(string Name, int Age, string? Email = null);

    [Fact]
    public async Task RoundTrip_WithVersionHeader()
    {
        // Arrange
        var serializer = new JsonVersionedSerializer<TestPayload>();
        var version = new SchemaVersion(1);
        var payload = new TestPayload("Alice", 30);

        // Act — serialize
        using var ms = new MemoryStream();
        await serializer.SerializeAsync(ms, payload, version).ConfigureAwait(true);

        // Act — deserialize
        ms.Position = 0;
        var (deserializedVersion, deserializedValue) = await serializer.DeserializeAsync(ms).ConfigureAwait(true);

        // Assert
        deserializedVersion.Should().Be(version);
        deserializedValue.Should().Be(payload);
    }

    [Fact]
    public async Task Deserialize_OldVersionPayload_WithNewerReader()
    {
        // Arrange — serialize with v1 schema (no Email field)
        var v1Serializer = new JsonVersionedSerializer<TestPayload>();
        var v1Version = new SchemaVersion(1);
        var v1Payload = new TestPayload("Bob", 25);

        using var ms = new MemoryStream();
        await v1Serializer.SerializeAsync(ms, v1Payload, v1Version).ConfigureAwait(true);

        // Act — deserialize with v2 reader (has optional Email)
        ms.Position = 0;
        var v2Serializer = new JsonVersionedSerializer<TestPayloadV2>();
        var (version, value) = await v2Serializer.DeserializeAsync(ms).ConfigureAwait(true);

        // Assert — Email defaults to null
        version.Value.Should().Be(1);
        value.Name.Should().Be("Bob");
        value.Age.Should().Be(25);
        value.Email.Should().BeNull();
    }

    [Fact]
    public async Task VersionHeader_Is4BytesLittleEndian()
    {
        // Arrange
        var serializer = new JsonVersionedSerializer<TestPayload>();
        var version = new SchemaVersion(42);
        var payload = new TestPayload("Test", 1);

        // Act
        using var ms = new MemoryStream();
        await serializer.SerializeAsync(ms, payload, version).ConfigureAwait(true);

        // Assert — first 4 bytes are 42 in LE
        var bytes = ms.ToArray();
        bytes.Length.Should().BeGreaterThan(4);
        var versionFromBytes = BitConverter.ToInt32(bytes, 0);
        if (!BitConverter.IsLittleEndian)
        {
            // BinaryPrimitives writes LE regardless of platform
            var span = bytes.AsSpan(0, 4);
            span.Reverse();
            versionFromBytes = BitConverter.ToInt32(bytes, 0);
        }

        versionFromBytes.Should().Be(42);
    }
}
