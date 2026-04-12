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
/// Concurrent-safe in-memory schema registry with auto-incrementing versions per subject.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 4 — "Encoding and Evolution": schema registries for safe evolution.</para>
/// </summary>
public sealed class InMemorySchemaRegistry : ISchemaRegistry
{
    private readonly ConcurrentDictionary<string, List<ISchema>> _subjects = new();
    private readonly ISchemaCompatibilityChecker _checker;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of <see cref="InMemorySchemaRegistry"/>.
    /// </summary>
    /// <param name="checker">The compatibility checker to use for validation.</param>
    public InMemorySchemaRegistry(ISchemaCompatibilityChecker checker)
    {
        ArgumentNullException.ThrowIfNull(checker);
        _checker = checker;
    }

    /// <inheritdoc />
    public ValueTask<SchemaVersion> RegisterAsync(string subject, ISchema schema, CompatibilityMode mode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(schema);

        lock (_lock)
        {
            if (!_subjects.TryGetValue(subject, out var versions))
            {
                versions = [];
                _subjects[subject] = versions;
            }

            // Check compatibility transitively against ALL prior versions (not just latest).
            // Per DDIA Ch.5: a v3 schema must be compatible with v1 AND v2,
            // otherwise data written with v1 could break when read with v3.
            if (versions.Count > 0 && mode != CompatibilityMode.None)
            {
                for (var i = 0; i < versions.Count; i++)
                {
                    if (!_checker.IsCompatible(versions[i], schema, mode))
                    {
                        throw new InvalidOperationException(
                            $"Schema is not {mode}-compatible with version {i + 1} of subject '{subject}'.");
                    }
                }
            }

            versions.Add(schema);
            return new ValueTask<SchemaVersion>(new SchemaVersion(versions.Count));
        }
    }

    /// <inheritdoc />
    public ValueTask<ISchema> GetAsync(string subject, SchemaVersion version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        if (!_subjects.TryGetValue(subject, out var versions))
        {
            throw new KeyNotFoundException($"Subject '{subject}' not found.");
        }

        var index = version.Value - 1;
        if (index < 0 || index >= versions.Count)
        {
            throw new KeyNotFoundException($"Version {version.Value} not found for subject '{subject}'.");
        }

        return new ValueTask<ISchema>(versions[index]);
    }
}
