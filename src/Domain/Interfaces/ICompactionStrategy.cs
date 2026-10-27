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
using Boutquin.Storage.Domain.ValueObjects.LsmTree;

namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Selects which segments an LSM storage engine should merge on each compaction cycle.
/// </summary>
/// <typeparam name="TKey">Key type carried in <see cref="SegmentMetadata{TKey}"/> to let the
/// strategy reason about key-range overlap for leveled compaction.</typeparam>
/// <remarks>
/// <para>
/// <b>Phase B3/B4 change:</b> prior versions exposed only the segment count via non-generic
/// methods; the strategy therefore could not tell Level from Level or segment size from segment
/// size, and "leveled" vs "size-tiered" degenerated into renamed full compactions. The Phase B
/// spec promotes per-segment <see cref="SegmentMetadata{TKey}"/> into the contract so each
/// strategy can honour the algorithm its XML doc claims (DDIA 2e pp. 120–125).
/// </para>
/// <para>
/// <b>Contract:</b>
/// </para>
/// <list type="bullet">
/// <item><see cref="ShouldCompact"/> must be a pure function of the supplied list — no hidden
/// state, no side effects. The engine may call it many times per write loop.</item>
/// <item><see cref="SelectSegments"/> MUST return a subset of the input list (same
/// <see cref="SegmentMetadata{TKey}"/> values, not remapped clones). Returning an unrelated
/// metadata value would point the engine at non-existent segments.</item>
/// <item>The returned subset MUST be ordered oldest → newest so the engine's k-way merge keeps
/// last-writer-wins precedence stable.</item>
/// </list>
/// <para>
/// <b>Common strategies:</b>
/// </para>
/// <list type="bullet">
/// <item><b>Full compaction</b> — merges every segment into one. Simple, high write
/// amplification (<c>FullCompactionStrategy&lt;TKey&gt;</c>).</item>
/// <item><b>Size-tiered</b> — groups segments by size tier, merges a tier once it fills up.
/// Lower write amplification for write-heavy workloads
/// (<c>SizeTieredCompactionStrategy&lt;TKey&gt;</c>).</item>
/// <item><b>Leveled</b> — organises segments into level-size-capped strata with non-overlapping
/// key ranges per level ≥ 1. Best read performance; higher write amplification
/// (<c>LeveledCompactionStrategy&lt;TKey&gt;</c>).</item>
/// </list>
/// <para>
/// <b>Reference:</b> Kleppmann &amp; Riccomini, <i>Designing Data-Intensive Applications</i>,
/// 2nd ed. (O'Reilly, 2026), Ch. 4 — "Compaction strategies" pp. 122–125.
/// </para>
/// </remarks>
public interface ICompactionStrategy<TKey>
{
    /// <summary>
    /// Returns <see langword="true"/> if the supplied segment set has crossed the strategy's
    /// compaction threshold.
    /// </summary>
    /// <param name="segments">Oldest → newest. Must not be null.</param>
    bool ShouldCompact(IReadOnlyList<SegmentMetadata<TKey>> segments);

    /// <summary>
    /// Picks the subset of <paramref name="segments"/> to merge. Returns the segments in
    /// oldest → newest order, using the same <see cref="SegmentMetadata{TKey}"/> instances the
    /// caller supplied so the engine can locate them in its own index.
    /// </summary>
    IReadOnlyList<SegmentMetadata<TKey>> SelectSegments(
        IReadOnlyList<SegmentMetadata<TKey>> segments);
}
