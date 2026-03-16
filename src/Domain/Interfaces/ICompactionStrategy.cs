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
/// Defines a strategy for selecting which segments to compact in an LSM-tree storage engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Theory:</b> Different compaction strategies offer different trade-offs between write
/// amplification, read amplification, and space amplification. This interface allows the LSM
/// engine to be configured with different strategies without changing its core logic.
/// </para>
/// <para>
/// <b>Common strategies:</b>
/// - <b>Full compaction:</b> Merges all segments into one. Simple, high write amplification.
/// - <b>Size-tiered compaction:</b> Groups similarly-sized segments and merges each group.
///   Better write amplification than full compaction for write-heavy workloads.
/// - <b>Leveled compaction:</b> Organizes segments into levels with size limits. Best read
///   performance but highest write amplification (not implemented in this reference implementation).
/// </para>
/// <para>
/// <b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 3 —
/// "Compaction strategies" section discusses size-tiered vs. leveled compaction trade-offs.
/// </para>
/// </remarks>
public interface ICompactionStrategy
{
    /// <summary>
    /// Determines whether compaction should be triggered based on the current segment count.
    /// </summary>
    /// <param name="segmentCount">The current number of on-disk segments.</param>
    /// <returns>True if compaction should proceed; false otherwise.</returns>
    bool ShouldCompact(int segmentCount);

    /// <summary>
    /// Selects which segment indices should be included in the compaction.
    /// </summary>
    /// <param name="segmentCount">The current number of on-disk segments.</param>
    /// <returns>The indices of segments to compact, in order.</returns>
    IReadOnlyList<int> SelectSegments(int segmentCount);
}
