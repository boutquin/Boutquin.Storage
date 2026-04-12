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
/// Serializes and deserializes values with an embedded schema version header,
/// enabling safe evolution of serialized data over time.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 4 — "Encoding and Evolution": version-tagged serialization for long-lived data.</para>
/// </summary>
/// <typeparam name="T">The type to serialize.</typeparam>
public interface IVersionedSerializer<T>
{
    /// <summary>
    /// Serializes <paramref name="value"/> to the <paramref name="stream"/> with a schema version header.
    /// </summary>
    /// <param name="stream">The output stream.</param>
    /// <param name="value">The value to serialize.</param>
    /// <param name="schemaVersion">The schema version to embed in the header.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask SerializeAsync(Stream stream, T value, SchemaVersion schemaVersion, CancellationToken ct = default);

    /// <summary>
    /// Deserializes a value from the <paramref name="stream"/>, reading the embedded schema version header.
    /// </summary>
    /// <param name="stream">The input stream.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The schema version and the deserialized value.</returns>
    ValueTask<(SchemaVersion Version, T Value)> DeserializeAsync(Stream stream, CancellationToken ct = default);
}
