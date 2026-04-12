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
using System.Text.Json;

using Boutquin.Storage.Domain.Interfaces.Schemas;

namespace Boutquin.Storage.Infrastructure.Schemas;

/// <summary>
/// JSON-based versioned serializer that prepends a 4-byte little-endian schema version header
/// before the JSON payload.
///
/// <para><b>Wire format:</b> [4 bytes: version (LE int32)] [N bytes: UTF-8 JSON payload]</para>
///
/// <para><b>Unknown field preservation:</b> deserializing to <typeparamref name="T"/> silently drops
/// JSON properties not mapped to <typeparamref name="T"/>'s members. A round-trip through
/// serialize → deserialize → serialize will lose unknown fields. Per DDIA Ch. 5 (W-8),
/// this can cause data loss when older code reads a record with new fields, modifies it,
/// and writes it back. To preserve unknown fields, annotate <typeparamref name="T"/> with
/// <c>[JsonExtensionData]</c> on a <c>Dictionary&lt;string, JsonElement&gt;</c> property.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 4 — "Encoding and Evolution": version-tagged serialization.</para>
/// </summary>
/// <typeparam name="T">The type to serialize.</typeparam>
public sealed class JsonVersionedSerializer<T> : IVersionedSerializer<T>
{
    private readonly JsonSerializerOptions? _options;

    /// <summary>
    /// Initializes a new instance of <see cref="JsonVersionedSerializer{T}"/>.
    /// </summary>
    /// <param name="options">Optional JSON serializer options.</param>
    public JsonVersionedSerializer(JsonSerializerOptions? options = null)
    {
        _options = options;
    }

    /// <inheritdoc />
    public async ValueTask SerializeAsync(Stream stream, T value, SchemaVersion schemaVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Write 4-byte LE version header
        var versionBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(versionBytes, schemaVersion.Value);
        await stream.WriteAsync(versionBytes, ct).ConfigureAwait(false);

        // Write JSON payload
        await JsonSerializer.SerializeAsync(stream, value, _options, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<(SchemaVersion Version, T Value)> DeserializeAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Read 4-byte LE version header
        var versionBytes = new byte[4];
        await stream.ReadExactlyAsync(versionBytes, ct).ConfigureAwait(false);
        var version = new SchemaVersion(BinaryPrimitives.ReadInt32LittleEndian(versionBytes));

        // Read JSON payload
        var value = await JsonSerializer.DeserializeAsync<T>(stream, _options, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Deserialization returned null.");

        return (version, value);
    }
}
