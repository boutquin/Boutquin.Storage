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
namespace Boutquin.Storage.Infrastructure.LsmTree;

/// <summary>
/// Size-tiered compaction strategy: triggers compaction when the segment count reaches a threshold,
/// and selects all segments for merging. In a more advanced implementation, this would group
/// similarly-sized segments into tiers and merge within each tier.
/// </summary>
/// <remarks>
/// <para>
/// <b>Theory:</b> Size-tiered compaction (STCS) groups SSTables by size and merges those in the same
/// size tier. This reduces write amplification compared to full compaction because small, recently-flushed
/// segments are merged with similarly small segments rather than with the entire dataset.
/// </para>
/// <para>
/// <b>Current implementation:</b> This simplified version uses a minimum segment count threshold to
/// trigger compaction and merges all segments. A production implementation would track segment sizes
/// and group them into buckets.
/// </para>
/// <para>
/// <b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 3 —
/// "SSTables and LSM-Trees": size-tiered compaction is used by Apache Cassandra and HBase.
/// </para>
/// </remarks>
public sealed class SizeTieredCompactionStrategy : ICompactionStrategy
{
    private readonly int _minSegments;

    /// <summary>
    /// Initializes a new instance with the specified minimum segment count.
    /// </summary>
    /// <param name="minSegments">The minimum number of segments required to trigger compaction. Must be at least 2.</param>
    public SizeTieredCompactionStrategy(int minSegments = 4)
    {
        if (minSegments < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(minSegments), "Minimum segments must be at least 2.");
        }

        _minSegments = minSegments;
    }

    /// <inheritdoc/>
    public bool ShouldCompact(int segmentCount) => segmentCount >= _minSegments;

    /// <inheritdoc/>
    public IReadOnlyList<int> SelectSegments(int segmentCount)
    {
        // Simplified: select all segments. A real STCS would group by size tier.
        return Enumerable.Range(0, segmentCount).ToList();
    }
}
