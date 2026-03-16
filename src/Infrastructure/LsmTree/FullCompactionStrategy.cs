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
/// Full compaction strategy: merges all segments into a single segment when the segment count
/// reaches a configurable threshold.
/// </summary>
/// <remarks>
/// <para>
/// This is the simplest compaction strategy. When triggered, it selects all segments for merging.
/// The result is always a single compacted segment. This minimizes read amplification (O(1) after
/// compaction) but maximizes write amplification (every entry is rewritten during each compaction).
/// </para>
/// <para>
/// <b>When to use:</b> Suitable for workloads with moderate write rates where read latency
/// is the primary concern, or as a reference implementation for testing.
/// </para>
/// </remarks>
public sealed class FullCompactionStrategy : ICompactionStrategy
{
    private readonly int _threshold;

    /// <summary>
    /// Initializes a new instance with the specified threshold.
    /// </summary>
    /// <param name="threshold">The segment count that triggers compaction. Must be at least 2.</param>
    public FullCompactionStrategy(int threshold)
    {
        if (threshold < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Compaction threshold must be at least 2.");
        }

        _threshold = threshold;
    }

    /// <inheritdoc/>
    public bool ShouldCompact(int segmentCount) => segmentCount >= _threshold;

    /// <inheritdoc/>
    public IReadOnlyList<int> SelectSegments(int segmentCount)
    {
        // Select all segments.
        return Enumerable.Range(0, segmentCount).ToList();
    }
}
