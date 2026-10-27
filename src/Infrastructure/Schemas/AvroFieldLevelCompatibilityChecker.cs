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

namespace Boutquin.Storage.Infrastructure.Schemas;

/// <summary>
/// Avro-style field-level schema compatibility checker. Matches fields by name, making this
/// the Avro evolution model from DDIA 2e Ch. 5: renaming a field breaks compatibility even
/// if its tag number is preserved. The tagged-wire emitter in the source generator handles
/// the Protobuf-family (tag-based) evolution model independently.
///
/// <para><b>Backward:</b> new schema can read old data — new fields must be optional; no removed required fields.</para>
/// <para><b>Forward:</b> old schema can read new data — removed fields must have been optional; no new required fields.</para>
/// <para><b>Full:</b> both backward and forward.</para>
/// <para><b>None:</b> always compatible.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026),
/// Ch. 5 — "Encoding and Evolution": Avro compatibility and writer-schema resolution.</para>
/// </summary>
public sealed class AvroFieldLevelCompatibilityChecker : ISchemaCompatibilityChecker
{
    /// <inheritdoc />
    public bool IsCompatible(ISchema existing, ISchema candidate, CompatibilityMode mode)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);

        return mode switch
        {
            CompatibilityMode.None => true,
            CompatibilityMode.Backward => IsBackwardCompatible(existing, candidate),
            CompatibilityMode.Forward => IsForwardCompatible(existing, candidate),
            CompatibilityMode.Full => IsBackwardCompatible(existing, candidate) && IsForwardCompatible(existing, candidate),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    /// <summary>
    /// Backward: a new reader (candidate) reads data written by an old writer (existing). The
    /// only breaking change is <b>adding a required field</b> — old data carries no value for it
    /// and the new reader has no default. <b>Removing a field is always backward-safe</b> (the new
    /// reader simply ignores the extra field present in old data), regardless of whether the
    /// removed field was required. This asymmetry vs. <see cref="IsForwardCompatible"/> is the
    /// whole point of distinguishing the two modes (DDIA 2e Ch. 5; finding #4). <c>IsOptional</c>
    /// is the proxy for "has a default value".
    /// </summary>
    private static bool IsBackwardCompatible(ISchema existing, ISchema candidate)
    {
        var existingFields = existing.Fields.ToDictionary(f => f.Name);

        // Every new field in the candidate that wasn't in existing must be optional (have a
        // default); a new required field cannot be filled when reading old data. Removing a field
        // is intentionally NOT checked here — it is always backward-compatible. For a RETAINED
        // field, the new reader (candidate) must be able to read the old writer's (existing) bytes,
        // so the candidate type must be the existing type or a valid Avro promotion of it.
        foreach (var field in candidate.Fields)
        {
            if (!existingFields.TryGetValue(field.Name, out var existingField))
            {
                if (!field.IsOptional)
                {
                    return false;
                }
            }
            else
            {
                if (!IsReadableAs(writerType: existingField.FieldType, readerType: field.FieldType))
                {
                    return false;
                }

                // Optionality flip on a RETAINED field: optional→required is backward-breaking.
                // IsOptional means "may be absent in serialized data", so old data may omit the
                // field — and the new required reader has no default to fall back on. Same hazard
                // as ADDING a required field, reached through retention instead of addition.
                if (existingField.IsOptional && !field.IsOptional)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Forward: an old reader (existing) reads data written by a new writer (candidate). The only
    /// breaking change is <b>removing a required field</b> — the old reader expects it and the new
    /// data omits it with no default to fall back on. <b>Adding a field is always forward-safe</b>
    /// (the old reader skips the unknown field), regardless of whether the added field is required.
    /// This is the mirror image of <see cref="IsBackwardCompatible"/> (DDIA 2e Ch. 5; finding #4).
    /// </summary>
    private static bool IsForwardCompatible(ISchema existing, ISchema candidate)
    {
        var candidateFields = candidate.Fields.ToDictionary(f => f.Name);

        // Fields removed in candidate must have been optional (have a default) in existing; the
        // old reader falls back to that default. Adding a field is intentionally NOT checked here
        // — it is always forward-compatible because the old reader skips unknown fields. For a
        // RETAINED field, the old reader (existing) must be able to read the new writer's
        // (candidate) bytes, so the existing type must be the candidate type or a valid Avro
        // promotion of it.
        foreach (var field in existing.Fields)
        {
            if (!candidateFields.TryGetValue(field.Name, out var candidateField))
            {
                if (!field.IsOptional)
                {
                    return false;
                }
            }
            else
            {
                if (!IsReadableAs(writerType: candidateField.FieldType, readerType: field.FieldType))
                {
                    return false;
                }

                // Optionality flip on a RETAINED field: required→optional is forward-breaking.
                // The new writer may omit the now-optional field, and the old reader — which
                // declares it required — has no default to fall back on. Same hazard as REMOVING
                // a required field, reached through retention instead of removal.
                if (!field.IsOptional && candidateField.IsOptional)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a reader declaring <paramref name="readerType"/> can decode bytes a writer wrote as
    /// <paramref name="writerType"/>, following Avro's writer/reader schema resolution: identical
    /// types always resolve, plus the numeric promotions int → long → double and the string ↔ bytes
    /// equivalence (DDIA 2e Ch. 5 — "changing the datatype of a field is possible, provided that
    /// Avro can convert the type"). The coarse <see cref="SchemaFieldType"/> model has no distinct
    /// 32-bit float, so int/long promote straight to <see cref="SchemaFieldType.Float64"/>. Every
    /// other change (e.g. Int32 → String, narrowing Int64 → Int32) is unresolvable.
    /// </summary>
    private static bool IsReadableAs(SchemaFieldType writerType, SchemaFieldType readerType)
        => writerType == readerType
           || (writerType, readerType) switch
           {
               (SchemaFieldType.Int32, SchemaFieldType.Int64) => true,
               (SchemaFieldType.Int32, SchemaFieldType.Float64) => true,
               (SchemaFieldType.Int64, SchemaFieldType.Float64) => true,
               (SchemaFieldType.String, SchemaFieldType.Binary) => true,
               (SchemaFieldType.Binary, SchemaFieldType.String) => true,
               _ => false,
           };
}
