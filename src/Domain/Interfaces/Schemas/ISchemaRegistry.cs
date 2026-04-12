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
/// Registers and retrieves versioned schemas for named subjects.
///
/// <para>Each subject (e.g., a topic or entity name) maintains an independent version sequence.
/// New schemas are validated against the latest version using the specified compatibility mode
/// before registration succeeds.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 4 — "Encoding and Evolution": schema registries for safe evolution.</para>
/// </summary>
public interface ISchemaRegistry
{
    /// <summary>
    /// Registers a new schema version for the specified subject.
    /// </summary>
    /// <param name="subject">The subject name (e.g., topic or entity). Must not be null, empty, or whitespace.</param>
    /// <param name="schema">The schema to register.</param>
    /// <param name="mode">The compatibility mode to enforce against existing versions.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assigned version number.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the schema is incompatible with the existing version.</exception>
    ValueTask<SchemaVersion> RegisterAsync(string subject, ISchema schema, CompatibilityMode mode, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a schema by subject and version.
    /// </summary>
    /// <param name="subject">The subject name.</param>
    /// <param name="version">The version to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The schema at the specified version.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the subject or version does not exist.</exception>
    ValueTask<ISchema> GetAsync(string subject, SchemaVersion version, CancellationToken ct = default);
}
