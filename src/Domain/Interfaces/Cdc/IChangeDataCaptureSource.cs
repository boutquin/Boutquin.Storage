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

/// <summary>
/// A source of change data capture records that can be read from a given position.
///
/// <para>CDC sources bridge existing storage engines to downstream consumers (materialized views,
/// search indexes, analytics). Consumers read from their last checkpoint position forward.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 11 — "Stream Processing" and Ch. 12 — "The Future of Data Systems":
/// CDC as the foundation for derived data systems.</para>
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface IChangeDataCaptureSource<TKey, TValue>
{
    /// <summary>
    /// Reads change records from the specified position forward.
    /// </summary>
    /// <param name="position">The position to start reading from (inclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of change records in order.</returns>
    IAsyncEnumerable<ChangeRecord<TKey, TValue>> ReadChangesAsync(long position, CancellationToken ct = default);
}
