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
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Defines an interface for a WriteAheadLog that provides durability guarantees
/// for key-value operations by persisting entries to disk before they are applied to the
/// in-memory data structure (MemTable).
/// </summary>
/// <typeparam name="TKey">The type of the keys in the WriteAheadLog.</typeparam>
/// <typeparam name="TValue">The type of the values in the WriteAheadLog.</typeparam>
/// <remarks>
/// <para>
/// <b>Theory:</b> A WriteAheadLog is a fundamental component of LSM-tree storage engines.
/// Before any write operation modifies the in-memory MemTable, the operation is first recorded
/// in the WriteAheadLog. This ensures that in the event of a crash, the MemTable can be reconstructed
/// by replaying the WriteAheadLog entries.
/// </para>
/// <para>
/// <b>Lifecycle:</b> The WriteAheadLog accumulates entries as writes arrive. When the MemTable is flushed
/// to an SSTable on disk, the WriteAheadLog is truncated (cleared) because the data is now safely persisted
/// in the SSTable. A new WriteAheadLog then begins accumulating entries for the next MemTable.
/// </para>
/// <para>
/// <b>Complexity (where S = serialized entry size, F = total WriteAheadLog file size):</b>
/// </para>
/// <para>- <b>AppendAsync:</b> O(S) — serializes the key-value pair and writes S + 8 bytes (length prefix + CRC32).</para>
/// <para>- <b>RecoverAsync:</b> O(F) — reads and deserializes the entire WriteAheadLog file sequentially.</para>
/// <para>- <b>TruncateAsync:</b> O(1) — truncates the file to zero length and fsyncs.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 3 — "Storage and Retrieval",
/// section on making B-trees reliable and LSM-tree crash recovery. The WriteAheadLog ensures durability by recording
/// every mutation before it is applied to the in-memory structure.</para>
/// </remarks>
public interface IWriteAheadLog<TKey, TValue> : IDisposable
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Appends a key-value entry to the WriteAheadLog.
    /// </summary>
    /// <param name="key">The key to append.</param>
    /// <param name="value">The value to append.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous append operation.</returns>
    Task AppendAsync(TKey key, TValue value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recovers all valid entries from the WriteAheadLog, skipping any corrupted entries.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A read-only list of recovered key-value pairs.</returns>
    Task<IReadOnlyList<(TKey Key, TValue Value)>> RecoverAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Truncates (clears) the WriteAheadLog file. Called after a successful MemTable flush to disk.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous truncate operation.</returns>
    Task TruncateAsync(CancellationToken cancellationToken = default);
}
