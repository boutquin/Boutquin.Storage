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
namespace Boutquin.Storage.Domain.ValueObjects.SortedStringTable;

/// <summary>
/// A single decoded entry from a v2 SSTable block: key, monotonic sequence number, value
/// discriminator, and value bytes. Canonical public type for block-level readers; carries
/// the sequence-number slot (snapshot isolation) and the value-kind slot (WiscKey pointer
/// vs inline value).
/// </summary>
/// <param name="Key">The entry's raw key bytes as stored in the block (already
/// reconstructed from prefix-compressed form). Never empty for a decoded entry.</param>
/// <param name="SequenceNumber">The monotonic ulong assigned by the MemTable at write
/// time. v2.0.0 writes this on every entry; consumers of the snapshot-isolation API
/// read it via the LSM engine's <c>ISnapshot</c> read path (see
/// <c>docs/snapshot-isolation.md</c>).</param>
/// <param name="Kind">Discriminator byte indicating whether <paramref name="Value"/> is
/// inline bytes (<see cref="ValueKind.Inline"/>) or a value-log pointer
/// (<see cref="ValueKind.ValueLogPointer"/>).</param>
/// <param name="Value">The raw value bytes when <paramref name="Kind"/> is
/// <see cref="ValueKind.Inline"/>; the 12-byte <c>(fileIndex:u32, offset:u64)</c> pointer
/// produced by <c>ValueLogPointer.WriteTo</c> when <paramref name="Kind"/> is
/// <see cref="ValueKind.ValueLogPointer"/>.</param>
/// <remarks>
/// <para><b>Theory:</b>
/// The block-entry shape <c>(Key, Seqno, Kind, Value)</c> — 8-byte seqno plus 1-byte kind
/// on top of the classic SSTable key-value pair — is the format precommitment that lets
/// two post-GA features land without a v3 revision: snapshot isolation (reads seqno) and
/// key-value separation (reads kind). DDIA 2e pp. 119 + 130 + 131.</para>
/// <para><b>Reference:</b>
/// Kleppmann, <i>Designing Data-Intensive Applications</i> 2e, pp. 119–131.</para>
/// </remarks>
public readonly record struct BlockEntry(
    ReadOnlyMemory<byte> Key,
    ulong SequenceNumber,
    ValueKind Kind,
    ReadOnlyMemory<byte> Value);
