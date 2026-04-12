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
/// Checks field-level schema compatibility.
///
/// <para><b>Backward:</b> new schema can read old data — new fields must be optional; no removed required fields.</para>
/// <para><b>Forward:</b> old schema can read new data — removed fields must have been optional; no new required fields.</para>
/// <para><b>Full:</b> both backward and forward.</para>
/// <para><b>None:</b> always compatible.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 4 — "Encoding and Evolution": compatibility rules.</para>
/// </summary>
public sealed class FieldLevelCompatibilityChecker : ISchemaCompatibilityChecker
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
    /// Backward: new schema (candidate) can read data written by old schema (existing).
    /// - New fields added in candidate must be optional.
    /// - Required fields in existing must still be present in candidate.
    /// </summary>
    private static bool IsBackwardCompatible(ISchema existing, ISchema candidate)
    {
        var existingFields = existing.Fields.ToDictionary(f => f.Name);
        var candidateFields = candidate.Fields.ToDictionary(f => f.Name);

        // Every required field in the old schema must still exist in the new schema
        foreach (var field in existing.Fields)
        {
            if (!field.IsOptional && !candidateFields.ContainsKey(field.Name))
            {
                return false;
            }
        }

        // Every new field in the candidate that wasn't in existing must be optional
        foreach (var field in candidate.Fields)
        {
            if (!existingFields.ContainsKey(field.Name) && !field.IsOptional)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Forward: old schema (existing) can read data written by new schema (candidate).
    /// - Fields removed in candidate (present in existing but not candidate) must have been optional in existing.
    /// - New required fields in candidate cannot be read by old schema.
    /// </summary>
    private static bool IsForwardCompatible(ISchema existing, ISchema candidate)
    {
        var existingFields = existing.Fields.ToDictionary(f => f.Name);
        var candidateFields = candidate.Fields.ToDictionary(f => f.Name);

        // Fields removed in candidate must have been optional in existing
        foreach (var field in existing.Fields)
        {
            if (!candidateFields.ContainsKey(field.Name) && !field.IsOptional)
            {
                return false;
            }
        }

        // New fields in candidate must be optional (old reader would skip them)
        foreach (var field in candidate.Fields)
        {
            if (!existingFields.ContainsKey(field.Name) && !field.IsOptional)
            {
                return false;
            }
        }

        return true;
    }
}
