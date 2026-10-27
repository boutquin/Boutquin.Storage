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
namespace Boutquin.Storage.Domain.Enums;

/// <summary>
/// Identifies the kind of mutation carried by a <see cref="ValueObjects.WalEntry{TKey, TValue}"/>
/// on the durable WriteAheadLog wire. The underlying byte value is written into the WAL
/// frame between the payload-length and value-kind slots.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire encoding.</b> WAL frame:
/// <c>[4B payload-length][1B entry_kind][1B value_kind][8B seqno][4B header CRC32C][payload][4B payload CRC32C]</c>.
/// The kind byte takes exactly the underlying enum value; readers dispatch on it when
/// reconstituting the MemTable + tombstone set during recovery. Two CRC32C checksums (Castagnoli
/// polynomial — same variant as the SSTable footer and value-log) authenticate the frame: the
/// header checksum covers <c>[payload-length][entry_kind][value_kind][seqno]</c> and the payload
/// checksum covers the payload bytes, so a bit-flip in the kind byte, or in any other header
/// field, surfaces as a header-checksum mismatch on read.
/// </para>
/// <para>
/// <b>Why an enum rather than a bool.</b> Future WAL frame kinds (flush markers, checkpoint
/// boundaries, batch-commit records) can be added without a breaking API change — unknown
/// kind bytes surface as a deserialisation error so that a reader from an older version
/// refuses to silently skip records it cannot interpret.
/// </para>
/// <para>
/// <b>Reference:</b> Kleppmann &amp; Riccomini, <i>Designing Data-Intensive Applications</i>,
/// 2nd ed. (O'Reilly, 2026), Ch. 4 p. 121 — tombstones must be durable, not just in-memory.
/// </para>
/// </remarks>
public enum EntryKind : byte
{
    /// <summary>A live key-value mutation (<c>Set</c>). Value bytes follow the kind byte.</summary>
    Set = 0x00,

    /// <summary>A tombstone (<c>Delete</c>) for the key. Value bytes are present but ignored by the reader.</summary>
    Delete = 0x01,

    /// <summary>
    /// The seal the WriteAheadLog writes as the final frame of a segment when it rotates to the next
    /// one. Its 20-byte payload records the sealed segment's byte length before the seal (LE uint64),
    /// its frame count (LE uint64) and a CRC32C over those bytes (LE uint32), so recovery can detect
    /// damage that leaves no damaged frame — a sealed segment cut short, emptied, extended or
    /// reordered. Written and verified by the log itself; recovery never surfaces it as an entry.
    /// </summary>
    SegmentSeal = 0x02,
}
