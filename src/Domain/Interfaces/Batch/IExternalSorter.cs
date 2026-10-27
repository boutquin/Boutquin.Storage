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

/// <summary>Orders an asynchronous input by spilling bounded runs to external storage.</summary>
/// <typeparam name="T">The item type, with a deterministic codec and total comparer.</typeparam>
public interface IExternalSorter<T>
{
    /// <summary>Sorts input; disposing the enumerator releases its private scratch files.</summary>
    /// <param name="input">Finite input stream, consumed once.</param>
    /// <param name="ct">Cancellation while consuming, merging or yielding.</param>
    /// <returns>Items in ascending comparer order, retaining duplicates.</returns>
    IAsyncEnumerable<T> SortAsync(IAsyncEnumerable<T> input, CancellationToken ct = default);
}
