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

namespace Boutquin.Storage.Infrastructure.LsmTree;

/// <summary>
/// Merges every segment into a single compacted segment once the segment count reaches a
/// configurable threshold.
/// </summary>
/// <typeparam name="TKey">Key type — carried only so the strategy satisfies
/// <see cref="ICompactionStrategy{TKey}"/>; full compaction ignores key ranges.</typeparam>
/// <remarks>
/// <para>
/// <b>Theory:</b> DDIA 2e p. 122–123 notes full compaction as the simplest option: one pass,
/// one output segment. Post-compaction read amplification is O(1), write amplification is the
/// highest of any strategy (every entry is rewritten on every cycle).
/// </para>
/// <para>
/// <b>When to use:</b> read-heavy workloads where read latency dominates, or as a reference
/// baseline for comparing against <see cref="SizeTieredCompactionStrategy{TKey}"/> and
/// <see cref="LeveledCompactionStrategy{TKey}"/>.
/// </para>
/// </remarks>
public sealed class FullCompactionStrategy<TKey> : ICompactionStrategy<TKey>
{
    private readonly int _threshold;

    /// <summary>
    /// Creates a strategy that triggers compaction at <paramref name="threshold"/> segments.
    /// </summary>
    /// <param name="threshold">Must be at least 2 — a single segment has nothing to merge.</param>
    public FullCompactionStrategy(int threshold)
    {
        if (threshold < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Compaction threshold must be at least 2.");
        }

        _threshold = threshold;
    }

    /// <inheritdoc/>
    public bool ShouldCompact(IReadOnlyList<SegmentMetadata<TKey>> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return segments.Count >= _threshold;
    }

    /// <inheritdoc/>
    public IReadOnlyList<SegmentMetadata<TKey>> SelectSegments(
        IReadOnlyList<SegmentMetadata<TKey>> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        // Full compaction: every segment is selected, oldest → newest preserved.
        return segments.ToList();
    }
}
