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
/// <para>- <b>AppendAsync / AppendDeleteAsync:</b> O(S) — serializes the key-value pair and writes S + 18 bytes (length prefix + entry-kind + value-kind + seqno + CRC32C).</para>
/// <para>- <b>RecoverAsync:</b> O(F) — reads and deserializes the entire WriteAheadLog file sequentially.</para>
/// <para>- <b>TruncateAsync:</b> O(1) — truncates the file to zero length and fsyncs.</para>
///
/// <para>
/// <b>Tombstone durability (Phase A1).</b> A delete that is recorded only in the MemTable tombstone
/// set would be lost on a crash before the next flush — the older segment's live value would
/// silently resurrect. <see cref="AppendDeleteAsync"/> writes a durable tombstone marker so that
/// <see cref="RecoverAsync"/> can reconstruct the tombstone set alongside the MemTable.
/// </para>
///
/// <para><b>Reference:</b> Kleppmann &amp; Riccomini, <i>Designing Data-Intensive Applications</i>, 2nd ed.
/// (O'Reilly, 2026), Ch. 4 p. 121 — "the prescribed way to handle deletes is to append a tombstone
/// record to the data file."</para>
/// </remarks>
public interface IWriteAheadLog<TKey, TValue> : IDisposable
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Appends an inline Set entry — the <typeparamref name="TValue"/> payload is carried
    /// inside the WAL frame.
    /// </summary>
    /// <param name="key">The key to append.</param>
    /// <param name="value">The value to append inline.</param>
    /// <param name="sequenceNumber">
    /// Engine-assigned monotonic sequence number for this mutation. Persisted in the WAL
    /// frame so recovery can reseed the engine's counter to <c>max(sst, wal) + 1</c>.
    /// </param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous append operation.</returns>
    /// <remarks>
    /// Appends a Set frame carrying <see cref="Boutquin.Storage.Domain.Enums.ValueKind.Inline"/>;
    /// pointer-valued sets use <see cref="AppendPointerAsync"/>.
    /// </remarks>
    Task AppendAsync(TKey key, TValue value, ulong sequenceNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a Set + <see cref="Boutquin.Storage.Domain.Enums.ValueKind.ValueLogPointer"/>
    /// entry — the WAL frame carries a 12-byte value-log reference rather than the value
    /// bytes. The referenced value-log entry MUST already be durable —
    /// value-log fsync precedes the WAL frame append.
    /// </summary>
    /// <param name="key">The key to append.</param>
    /// <param name="pointer">The value-log reference.</param>
    /// <param name="sequenceNumber">Engine-assigned monotonic sequence number.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task AppendPointerAsync(
        TKey key,
        Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer pointer,
        ulong sequenceNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a tombstone entry (kind <c>Delete</c>) to the WriteAheadLog.
    /// The durable record ensures the delete survives a crash between the in-memory
    /// tombstone mutation and the next MemTable flush.
    /// </summary>
    /// <param name="key">The key to mark as deleted.</param>
    /// <param name="sequenceNumber">
    /// Engine-assigned monotonic sequence number for this tombstone. Persisted in the WAL
    /// frame so recovery can reseed the engine's counter.
    /// </param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous append operation.</returns>
    Task AppendDeleteAsync(TKey key, ulong sequenceNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recovers all valid entries from every WAL segment and returns them in position order.
    /// Returns entries tagged with their <see cref="Boutquin.Storage.Domain.Enums.EntryKind"/>
    /// so the caller can reconstruct both the MemTable (for <c>Set</c>) and the tombstone
    /// set (for <c>Delete</c>) in a single pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Corruption handling is fail-closed by segment position:</b>
    /// </para>
    /// <para>
    /// <b>Mid-file corruption (non-last segment):</b> A checksum failure or truncated frame
    /// inside any segment except the last throws
    /// <see cref="Boutquin.Storage.Domain.Exceptions.WriteAheadLogCorruptException"/>. A
    /// mid-segment write failure is abnormal — it implies the machine lost power between
    /// two successive appends, leaving the segment in an unrecoverable state. Silently
    /// skipping such entries would hide data loss, so the implementation throws.
    /// </para>
    /// <para>
    /// <b>Torn tail on the last segment:</b> A partial write at the very end of the last
    /// segment (incomplete length-prefix or incomplete frame body) is treated as a torn
    /// write — the engine was still live when it crashed. Recovery truncates the segment
    /// file at the last clean frame boundary and returns all entries before that boundary.
    /// No exception is thrown; the truncation repairs the segment for subsequent appends.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A read-only list of recovered WAL entries in position order.</returns>
    Task<IReadOnlyList<WalEntry<TKey, TValue>>> RecoverAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Truncates (clears) the WriteAheadLog file. Called after a successful MemTable flush to disk.
    /// After truncation the next append produces a fresh file (header + per-frame kind byte).
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous truncate operation.</returns>
    Task TruncateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards every WAL segment whose entries all carry sequence numbers at or below
    /// <paramref name="upToSeqnoInclusive"/>. This is the flush-side truncation primitive:
    /// after a memtable flush commits, the engine passes the highest sequence number
    /// <em>contained in the flushed slot</em>, and the log discards only the corresponding
    /// part (DDIA 2e Ch. 4) — segments still holding any frame above the watermark remain
    /// on disk, because they may be the only durable copy of writes acknowledged into the
    /// fresh active memtable after the seal.
    /// </summary>
    /// <param name="upToSeqnoInclusive">
    /// Highest sequence number whose WAL backing is no longer needed: every entry with a
    /// sequence number at or below this value is durably persisted in a flushed segment.
    /// </param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous truncate operation.</returns>
    Task TruncateCompletedSegmentsAsync(ulong upToSeqnoInclusive, CancellationToken cancellationToken = default);
}
