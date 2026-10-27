// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using Boutquin.Storage.Domain.Interfaces.Batch;

namespace Boutquin.Storage.Infrastructure.Batch;

/// <summary>A named single-process batch action with immutable dependency names.</summary>
/// <remarks>Actions own their output atomicity and idempotency; the runner does not roll back
/// effects or retry a failed task. Reference: DDIA batch processing, task dependency graphs.</remarks>
public sealed class BatchTask : IBatchTask
{
    private readonly Func<CancellationToken, ValueTask> _action;
    /// <summary>Creates a task with a stable name and prerequisite names.</summary>
    /// <param name="name">Nonblank graph-unique name.</param>
    /// <param name="dependencies">Names of required predecessor tasks.</param>
    /// <param name="action">One attempt at the task's work.</param>
    public BatchTask(string name, IEnumerable<string> dependencies, Func<CancellationToken, ValueTask> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(action);
        var copy = dependencies.ToArray();
        foreach (var dependency in copy) { ArgumentException.ThrowIfNullOrWhiteSpace(dependency); }
        Name = name;
        Dependencies = Array.AsReadOnly(copy.Distinct(StringComparer.Ordinal).ToArray());
        _action = action;
    }
    /// <inheritdoc />
    public string Name { get; }
    /// <inheritdoc />
    public IReadOnlyList<string> Dependencies { get; }
    /// <inheritdoc />
    public ValueTask ExecuteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _action(ct);
    }
}
