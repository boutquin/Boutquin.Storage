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

namespace Boutquin.Storage.Domain.Interfaces.Batch;

/// <summary>One named, retry-policy-free step in a single-process dependency graph.</summary>
public interface IBatchTask
{
    /// <summary>Gets the stable task name, unique in the graph.</summary>
    string Name { get; }
    /// <summary>Gets names that must complete successfully before this task runs.</summary>
    IReadOnlyList<string> Dependencies { get; }
    /// <summary>Executes one attempt; failures stop downstream steps.</summary>
    /// <param name="ct">Cancellation of this attempt.</param>
    /// <returns>Completion after all task effects finish.</returns>
    ValueTask ExecuteAsync(CancellationToken ct = default);
}
