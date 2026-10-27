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

namespace Boutquin.Storage.Infrastructure.Batch;

/// <summary>Executes a validated task graph sequentially and stops on its first failed attempt.</summary>
/// <remarks>There is no implicit retry, checkpoint, transaction or distributed scheduler.
/// Durable sinks should use atomic replacement or position/request deduplication.</remarks>
public sealed class BatchJobRunner
{
    /// <summary>Runs each graph task exactly once in this invocation, returning completed names.</summary>
    /// <param name="graph">Already validated graph.</param>
    /// <param name="ct">Cancellation before or during each task.</param>
    /// <returns>Completion order when all tasks succeed.</returns>
    public async ValueTask<IReadOnlyList<string>> RunAsync(BatchJobGraph graph, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var completed = new List<string>();
        foreach (var task in graph.Tasks)
        {
            ct.ThrowIfCancellationRequested();
            await task.ExecuteAsync(ct).ConfigureAwait(false);
            completed.Add(task.Name);
        }
        return completed.AsReadOnly();
    }
}
