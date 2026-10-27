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
/// One persisted tombstone entry in an SSTable footer: the deleted key (as on-disk bytes)
/// paired with the deletion seqno that produced it.
/// </summary>
/// <param name="Key">The deleted key, encoded by the same key serializer the SSTable uses
/// for live entries. Stored as <see cref="ReadOnlyMemory{Byte}"/> so the footer reader can
/// surface the raw bytes for engine-level decoding without a per-tombstone allocation.</param>
/// <param name="SequenceNumber">The engine-assigned sequence number at which the deletion
/// happened. The compactor's tombstone-GC step uses this to prove that no live entry older
/// than <see cref="SequenceNumber"/> survives in any other segment before stripping the
/// tombstone. Without this seqno, tombstones accumulate forever — the space-amplification
/// hazard DDIA Ch. 4 p. 124 names as a load-bearing LSM property.</param>
/// <remarks>
/// Persisting the tombstone seqno alongside the deleted key is what makes compaction's
/// tombstone-GC step safe. Reference: DDIA 2e Ch. 4 p. 124.
/// </remarks>
public readonly record struct SsTableTombstoneEntry(ReadOnlyMemory<byte> Key, ulong SequenceNumber);
