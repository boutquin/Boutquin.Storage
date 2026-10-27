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
namespace Boutquin.Storage.Domain.ValueObjects;

/// <summary>
/// A monotonically increasing version number for schemas in a registry.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026),
/// Ch. 5 — "Encoding and Evolution": schema versions in Avro/Protobuf registries.</para>
/// </summary>
public readonly record struct SchemaVersion
{
    /// <summary>
    /// Gets the version number. Positive on every assignment path; the one exception is
    /// <c>default(SchemaVersion)</c>, whose zero value no accessor runs for. That value is not a
    /// version: readers reject it, so a writer that persists a version must refuse it.
    /// </summary>
    /// <remarks>
    /// A required, validating <c>init</c> accessor rather than a get-only property: a struct
    /// deserializer (System.Text.Json among them) materializes the value through the implicit
    /// parameterless constructor and then assigns members, so a get-only property silently decodes
    /// every persisted version — for example inside an <see cref="EventEnvelope{TKey, TValue}"/>
    /// written to a JSON-backed log — to 0. The accessor re-applies the positivity check on that
    /// path and on <c>with</c> expressions, and <c>required</c> makes a missing value a decode
    /// error instead of a silent 0. Domain stays free of serializer attributes.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than 1.</exception>
    public required int Value
    {
        get;
        init => field = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "SchemaVersion must be positive.");
    }

    /// <summary>
    /// Initializes a new <see cref="SchemaVersion"/> with the specified positive value.
    /// </summary>
    /// <param name="value">The version number. Must be positive (&gt;= 1).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="value"/> is less than 1.
    /// </exception>
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public SchemaVersion(int value)
    {
        Value = value;
    }

    /// <summary>Returns the string representation of the version.</summary>
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
