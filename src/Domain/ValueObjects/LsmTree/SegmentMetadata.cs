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
namespace Boutquin.Storage.Domain.ValueObjects.LsmTree;

/// <summary>
/// Per-segment metadata surfaced to a compaction strategy. Combines the Phase A4 durability
/// envelope (<see cref="Crc32C"/>, <see cref="SizeBytes"/>) with the Phase B3 leveled-compaction
/// locating information (<see cref="Level"/>, <see cref="MinKey"/>, <see cref="MaxKey"/>).
/// </summary>
/// <typeparam name="TKey">The segment's key type.</typeparam>
/// <remarks>
/// <para>
/// <b>Theory:</b> DDIA 2e p. 124 (leveled compaction) needs per-segment key-range information
/// to decide which L_(i+1) segments overlap a chosen L_i segment. DDIA 2e p. 125 (size-tiered
/// compaction) needs only <see cref="SizeBytes"/> to group segments into tiers. This single
/// value type covers both strategies, so the engine threads one list of metadata through
/// <see cref="ICompactionStrategy{TKey}"/> regardless of the chosen algorithm.
/// </para>
/// <para>
/// <b>Why a record struct:</b> fits the repo's <c>ValueObjects_ShouldBeRecordTypes</c>
/// architecture test and avoids heap allocation when compaction strategies iterate large
/// segment lists.
/// </para>
/// <para>
/// <b>Reference:</b> Kleppmann &amp; Riccomini, <i>Designing Data-Intensive Applications</i>,
/// 2nd ed. (O'Reilly, 2026), Ch. 4 — "Filling up the memtable and flushing to SSTables" and
/// "Compaction strategies."
/// </para>
/// </remarks>
public readonly record struct SegmentMetadata<TKey>(
    int Level,
    TKey MinKey,
    TKey MaxKey,
    long SizeBytes,
    uint Crc32C)
{
    /// <summary>Whether the bounds describe real keys. Unknown bounds must never be compared or hashed.</summary>
    public bool HasKeyRange { get; init; } = true;

    /// <summary>Compares metadata without evaluating absent key bounds.</summary>
    /// <param name="other">The metadata to compare.</param>
    /// <returns>Whether both values describe the same metadata.</returns>
    public bool Equals(SegmentMetadata<TKey> other) =>
        Level == other.Level && SizeBytes == other.SizeBytes && Crc32C == other.Crc32C
        && HasKeyRange == other.HasKeyRange
        && (!HasKeyRange || (EqualityComparer<TKey>.Default.Equals(MinKey, other.MinKey)
            && EqualityComparer<TKey>.Default.Equals(MaxKey, other.MaxKey)));

    /// <summary>Hashes only meaningful metadata; an absent bound need not be a valid serializable key.</summary>
    /// <returns>A hash consistent with metadata equality.</returns>
    public override int GetHashCode() => HasKeyRange
        ? HashCode.Combine(Level, SizeBytes, Crc32C, HasKeyRange, MinKey, MaxKey)
        : HashCode.Combine(Level, SizeBytes, Crc32C, HasKeyRange);
}
