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
/// Leveled compaction strategy: organizes segments into levels with size limits.
/// Level 0 contains freshly flushed segments; higher levels are compacted and non-overlapping.
/// </summary>
/// <remarks>
/// <para>
/// <b>Theory:</b> Leveled compaction (used by LevelDB and RocksDB) organizes SSTables into levels.
/// Level 0 receives freshly flushed memtable segments, which may have overlapping key ranges.
/// When Level 0 exceeds a threshold, its segments are merged with overlapping segments from Level 1.
/// Each subsequent level has a size limit that is a multiple of the previous level.
/// </para>
///
/// <para>
/// <b>Trade-offs vs size-tiered compaction:</b>
/// - <b>Read amplification:</b> Lower — each level (except L0) has non-overlapping key ranges,
///   so at most one segment per level needs to be checked.
/// - <b>Write amplification:</b> Higher — data may be rewritten multiple times as it moves through levels.
/// - <b>Space amplification:</b> Lower — less temporary space needed during compaction.
/// </para>
///
/// <para>
/// <b>Current implementation:</b> This simplified version uses Level 0 segment count as the
/// compaction trigger and selects Level 0 segments for compaction. A production implementation
/// would track segment level assignments, key ranges, and merge with overlapping L1 segments.
/// </para>
///
/// <para>
/// <b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 3 —
/// "SSTables and LSM-Trees": leveled compaction is used by LevelDB and RocksDB, offering better
/// read performance than size-tiered compaction at the cost of higher write amplification.
/// </para>
/// </remarks>
public sealed class LeveledCompactionStrategy : ICompactionStrategy
{
    private readonly int _level0Threshold;

    /// <summary>
    /// Gets the size multiplier between levels.
    /// </summary>
    public int LevelSizeMultiplier { get; }

    /// <summary>
    /// Gets the base level size in megabytes.
    /// </summary>
    public int BaseLevelSizeMB { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LeveledCompactionStrategy"/> class.
    /// </summary>
    /// <param name="level0Threshold">
    /// The number of Level 0 segments that triggers compaction. Must be at least 2.
    /// Recommended: 4 (RocksDB default). Lower values reduce read amplification but increase
    /// write amplification. Higher values batch more L0 segments per compaction.
    /// </param>
    /// <param name="levelSizeMultiplier">
    /// The size multiplier between levels. Must be at least 2.
    /// Recommended: 10 (RocksDB default). A multiplier of 10 means Level 2 is 10× Level 1,
    /// Level 3 is 100× Level 1, etc. Lower values (e.g., 4) reduce space amplification
    /// but increase write amplification due to more frequent compactions.
    /// </param>
    /// <param name="baseLevelSizeMB">
    /// The size limit for Level 1 in megabytes.
    /// Recommended: 10–256 MB depending on total dataset size. For small datasets (&lt;1 GB),
    /// 10 MB is appropriate. For larger datasets, 64–256 MB reduces the number of levels.
    /// </param>
    public LeveledCompactionStrategy(int level0Threshold = 4, int levelSizeMultiplier = 10, int baseLevelSizeMB = 10)
    {
        if (level0Threshold < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(level0Threshold), "Level 0 threshold must be at least 2.");
        }

        if (levelSizeMultiplier < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(levelSizeMultiplier), "Level size multiplier must be at least 2.");
        }

        _level0Threshold = level0Threshold;
        LevelSizeMultiplier = levelSizeMultiplier;
        BaseLevelSizeMB = baseLevelSizeMB;
    }

    /// <inheritdoc/>
    public bool ShouldCompact(int segmentCount) => segmentCount >= _level0Threshold;

    /// <inheritdoc/>
    public IReadOnlyList<int> SelectSegments(int segmentCount)
    {
        // Simplified: select all Level 0 segments (the most recent ones up to the threshold count).
        // In a production implementation, this would select overlapping L0 segments plus
        // their corresponding L1 segments for merge.
        var count = Math.Min(segmentCount, _level0Threshold);
        return Enumerable.Range(0, count).ToList();
    }
}
