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
using Boutquin.Storage.Domain.ValueObjects.SortedStringTable;

namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Per-block cache for SSTable read paths. The engine consults the cache before reading a
/// block from disk; on a hit, the cached payload (already decompressed) is returned without
/// touching the file system.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a seam.</b> Every SSTable point lookup re-reads — and on the columnar path
/// re-decompresses — the same block on cache miss. DDIA 2e Ch. 4 p. 79 treats per-block
/// caching as the canonical read-side optimisation; LevelDB and RocksDB ship one. Without it,
/// the LSM read path is serially slow on warm-but-evicted workloads — exactly the workload
/// most consumers actually have.
/// </para>
/// <para>
/// <b>Contract.</b> Implementations must be thread-safe — the engine reads concurrently
/// across point lookups, range scans, and snapshot iterators. <see cref="TryGet"/> returns
/// <see langword="false"/> when the key is absent or the entry was evicted between
/// <see cref="Put"/> and the call; the engine then reads from disk, decodes, and calls
/// <see cref="Put"/> with the decoded payload. <see cref="Put"/> may evict any other entry
/// to make room. <see cref="Clear"/> empties the cache and is safe to call concurrently with
/// reads — concurrent <see cref="TryGet"/> may succeed or fail; either is acceptable.
/// </para>
/// <para>
/// <b>What the cached payload represents.</b> The cached value is the **decompressed** block
/// payload, in the byte layout consumers see after <c>BlockFormat.DecompressPayload</c>.
/// Caching pre-decompression bytes would force every reader to decompress on each lookup —
/// the saved CPU is the load-bearing benefit of block caching, not just the avoided disk
/// read.
/// </para>
/// <para>
/// <b>Cost accounting.</b> The <c>costBytes</c> parameter on <see cref="Put"/>
/// reports the byte size of the payload. Implementations sum reported costs to enforce a
/// budget; consumers must report the actual bytes inserted.
/// </para>
/// <para>
/// <b>Default null = no caching.</b> Engine and SST consumers accept a nullable cache
/// reference and short-circuit the cache path when null. This preserves the no-cache
/// configuration as valid behaviour and makes every caching test deterministic against the
/// presence/absence of the seam.
/// </para>
/// <para>
/// <b>Reference:</b> Kleppmann &amp; Riccomini, <i>Designing Data-Intensive Applications</i>,
/// 2nd ed. (O'Reilly, 2026), Ch. 4 p. 79 — block-cache shape and motivation.
/// </para>
/// </remarks>
public interface IBlockCache
{
    /// <summary>
    /// Attempts to retrieve the cached block payload for <paramref name="key"/>. Returns
    /// <see langword="true"/> on hit; sets <paramref name="payload"/> to the cached bytes.
    /// Returns <see langword="false"/> on miss; <paramref name="payload"/> is set to an empty
    /// array. Implementations must be thread-safe.
    /// </summary>
    bool TryGet(BlockCacheKey key, out byte[] payload);

    /// <summary>
    /// Inserts the decompressed block payload for <paramref name="key"/> into the cache. The
    /// implementation may evict any other entry to make room. <paramref name="costBytes"/>
    /// must reflect the actual byte size of <paramref name="payload"/>.
    /// </summary>
    void Put(BlockCacheKey key, byte[] payload, long costBytes);

    /// <summary>
    /// Removes every entry from the cache. Safe to call concurrently with reads — concurrent
    /// <see cref="TryGet"/> calls may observe the stale or fresh state.
    /// </summary>
    void Clear();
}
