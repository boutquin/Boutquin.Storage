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
namespace Boutquin.Storage.Domain.Interfaces.Schemas;

/// <summary>
/// Avro-style serializer that embeds a schema version header and supports writer-schema-directed
/// deserialization via an <see cref="ISchemaRegistry"/>. The wire layout follows DDIA 2e Ch. 5:
/// each record carries the writer-schema version tag, and the reader can resolve the writer schema
/// from the registry to drive field-level evolution (renaming, re-ordering, default filling).
///
/// <para><b>Avro vs Protobuf framing:</b> this interface represents the Avro-family evolution
/// model. The source generator's tagged-wire emitter handles the Protobuf-family model (tag-numbered
/// fields, rename-is-free). The two models are distinct design choices with different trade-offs.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026),
/// Ch. 5 — "Encoding and Evolution": Avro writer-schema resolution, version-tagged serialization.</para>
/// </summary>
/// <typeparam name="T">The type to serialize.</typeparam>
public interface IAvroStyleVersionedSerializer<T>
{
    /// <summary>
    /// Serializes <paramref name="value"/> to the <paramref name="stream"/> with a schema version header.
    /// </summary>
    /// <param name="stream">The output stream.</param>
    /// <param name="value">The value to serialize.</param>
    /// <param name="schemaVersion">The registered positive writer-schema version to embed in the header.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is not positive (for example <c>default(SchemaVersion)</c>).
    /// Every implementation's reader rejects such a header as corruption, so every implementation
    /// refuses it before writing any byte.
    /// </exception>
    ValueTask SerializeAsync(Stream stream, T value, SchemaVersion schemaVersion, CancellationToken ct = default);

    /// <summary>
    /// Deserializes a value from the <paramref name="stream"/>, reading the embedded schema version
    /// header and optionally resolving the writer schema from <paramref name="registry"/> to drive
    /// Avro-style field evolution.
    /// </summary>
    /// <param name="stream">The input stream.</param>
    /// <param name="registry">Schema registry for resolving the writer schema by version. Implementations
    /// may ignore this when the format is self-describing (e.g., JSON).</param>
    /// <param name="subject">Registry subject name under which schemas for <typeparamref name="T"/> are registered.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The writer-schema version and the deserialized value.</returns>
    ValueTask<(SchemaVersion Version, T Value)> DeserializeAsync(
        Stream stream,
        ISchemaRegistry registry,
        string subject,
        CancellationToken ct = default);
}
