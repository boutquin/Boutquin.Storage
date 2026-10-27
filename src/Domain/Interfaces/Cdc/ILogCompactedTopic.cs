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
namespace Boutquin.Storage.Domain.Interfaces.Cdc;

/// <summary>A keyed change topic retaining the latest record, including deletion, for each key.</summary>
/// <typeparam name="TKey">Stable key identity.</typeparam>
/// <typeparam name="TValue">Immutable values.</typeparam>
/// <remarks>Compaction creates offset gaps. Reads reconstruct current state, not every historical transition.</remarks>
public interface ILogCompactedTopic<TKey, TValue> : ISnapshotCdcSource<TKey, TValue>
{
    /// <summary>Appends a change and replaces the retained record for its key.</summary>
    /// <param name="key">The affected key.</param>
    /// <param name="value">The new value, or default for deletion.</param>
    /// <param name="operation">Insert, Update or Delete; Snapshot is not appendable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assigned nonnegative offset, never reused after successful persistence.</returns>
    ValueTask<long> AppendAsync(TKey key, TValue? value, ChangeOperation operation, CancellationToken ct = default);
}
