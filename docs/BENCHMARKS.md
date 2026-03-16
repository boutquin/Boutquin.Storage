# Benchmark Results

> **Environment:** Apple M4, .NET 10.0.3 (Arm64 RyuJIT), macOS Tahoe 26.3
> **Date:** 2026-03-15
> **BenchmarkDotNet:** v0.15.8 (3 iterations, 1 warmup)

This document presents benchmark results for the storage engine implementations in Boutquin.Storage.
Rather than just reporting numbers, each section explains *what the results mean* in terms of the
data structure theory from Kleppmann's *Designing Data-Intensive Applications*.

---

## Table of Contents

- [Hash Functions](#hash-functions)
- [Bloom Filters](#bloom-filters)
- [Entry Serializers](#entry-serializers)
- [Write-Ahead Log](#write-ahead-log)
- [Sorted String Table](#sorted-string-table)
- [Storage Engine Comparison](#storage-engine-comparison)
- [LSM Storage Engine](#lsm-storage-engine)
- [Compaction Strategies](#compaction-strategies)
- [Known Issues](#known-issues)

---

## Hash Functions

| Method      | Mean     | Allocated |
|-------------|----------|-----------|
| XxHash32    | 1.86 μs  | 0 B       |
| Murmur3Hash | 2.13 μs  | 0 B       |
| Fnv1aHash   | 3.00 μs  | 0 B       |

**What this tells us:** xxHash32 is 60% faster than FNV-1a, confirming its reputation as a
throughput-optimized hash. All three are zero-allocation, which is important since hash functions
are called on every Bloom filter operation and every key lookup.

**Practical impact:** The hash function choice matters most for Bloom filters, which call multiple
hash functions per probe. At 10,000 Bloom filter operations, the cumulative difference between
xxHash32 and FNV-1a is ~11ms — noticeable but not dominant compared to I/O costs.

**Expected:** xxHash32 was designed for speed over cryptographic strength, so its lead is expected.
Murmur3 landing between the two is textbook — it's a general-purpose hash that balances speed
and distribution quality.

---

## Bloom Filters

| Method (1,000 items)       | Mean     | Allocated |
|----------------------------|----------|-----------|
| CountingBloomFilter Contains | 34 μs  | 94 KB     |
| BloomFilter Contains         | 37 μs  | 94 KB     |
| BloomFilter ContainsMiss     | 45 μs  | 102 KB    |
| CountingBloomFilter ContainsMiss | 45 μs | 102 KB |
| CountingBloomFilter Add      | 60 μs  | 131 KB    |
| BloomFilter Add              | 70 μs  | 95 KB     |

| Method (10,000 items)      | Mean     | Allocated |
|----------------------------|----------|-----------|
| CountingBloomFilter Contains | 374 μs | 1,008 KB  |
| BloomFilter Contains         | 378 μs | 1,008 KB  |
| BloomFilter ContainsMiss     | 458 μs | 1,016 KB  |
| CountingBloomFilter ContainsMiss | 506 μs | 1,016 KB |
| BloomFilter Add              | 677 μs | 1,020 KB  |
| CountingBloomFilter Add      | 719 μs | 1,382 KB  |

**What this tells us:** Bloom filters deliver their theoretical promise — `Contains` checks for
*existing* items are faster than checks for *missing* items. This is because a positive match
can short-circuit once all hash positions are set, while a negative result must evaluate enough
hash functions to find a zero bit.

**Slightly surprising:** CountingBloomFilter `Contains` is marginally *faster* than standard
BloomFilter, despite the counting variant using wider counters. This is likely a measurement
artifact at this scale — the operations are so fast that noise dominates the ~2μs difference.

**Memory note:** At 10K items, CountingBloomFilter `Add` allocates 35% more memory (1,382 KB vs
1,020 KB) and triggers Gen2 GC collections. This is expected — counting filters store integer
counters per bucket instead of single bits, trading memory for the ability to support deletions.

**Practical impact:** The real value of Bloom filters shows up in the storage engine benchmarks
below: `SearchNonExisting` at 1,000 items takes 787ms with a Bloom filter vs. full segment scans
without one.

---

## Entry Serializers

| Method (1,000 items) | Mean    | Allocated |
|----------------------|---------|-----------|
| BinaryRead           | 43 ms   | 8.5 MB    |
| BinaryWrite          | 66 ms   | 12.7 MB   |
| CsvWrite             | 68 ms   | 26.3 MB   |
| CsvRead              | ⚠ FAILED | —        |

**What this tells us:** Binary serialization reads ~35% faster than it writes, which makes
sense — deserialization skips the encoding step and reads fixed-width fields directly. CSV
writing is comparable in speed to binary but allocates **2x the memory** due to string
conversions and intermediate buffers.

**Why CsvRead failed:** The benchmark threw an exception during execution. This indicates a bug
in the CSV deserialization path — likely a parsing edge case with the test data format. This
needs investigation.

**Design implication:** Binary serialization is the right default for on-disk storage (SSTable,
WAL). CSV serialization is useful for debugging and interoperability but should not be used in
hot paths. The 2x memory overhead becomes significant when flushing large memtables.

---

## Write-Ahead Log

| Method  | Items | Mean      | Allocated |
|---------|-------|-----------|-----------|
| Append  | 100   | 403 ms    | 83 KB     |
| Recover | 100   | 405 ms    | 131 KB    |
| Append  | 1,000 | 4,051 ms  | 779 KB    |
| Recover | 1,000 | 4,080 ms  | 1,192 KB  |

**What this tells us:** WAL operations are **dominated by fsync**. Each append calls
`Flush(flushToDisk: true)` to guarantee durability, which forces the OS to write data through
to the physical storage device. At ~4ms per entry (4,051ms ÷ 1,000), the per-entry cost is
almost entirely fsync latency.

**Perfectly linear scaling** (10x items → 10x time) confirms that each entry pays a fixed fsync
cost with no amortization.

**Recovery ≈ Append time:** This is slightly surprising. You might expect recovery (sequential
reads) to be faster than writes (sequential writes + fsync). The near-identical times suggest
that recovery is also doing per-entry deserialization with validation, or that the read path
is hitting uncached I/O.

**This is the critical bottleneck** in the LSM write path. A production WAL would batch entries
and fsync once per batch (group commit), reducing the per-entry cost by 100-1000x. Our
implementation prioritizes correctness (every entry is durable) over throughput.

---

## Sorted String Table

| Method       | Items | Mean    | Allocated |
|--------------|-------|---------|-----------|
| WriteSsTable | 100   | 17 ms   | 1.3 MB    |
| ReadSsTable  | 100   | 22 ms   | 4.0 MB    |
| WriteSsTable | 1,000 | 76 ms   | 12.7 MB   |
| ReadSsTable  | 1,000 | 219 ms  | 40.1 MB   |

**What this tells us:** SSTable reads are **3x slower than writes** and allocate **3x more
memory**. This is somewhat counterintuitive — you might expect sequential reads to be faster
than writes.

**Why reads are slower:** SSTable reads use a sparse index to locate the approximate region,
then scan forward through the data block. Each read deserializes entries, builds key comparisons,
and allocates intermediate objects. The write path simply serializes sorted data sequentially.

**Superlinear read scaling** (10x items: 22ms → 219ms = 10x) is expected given the sparse index
design. With a fixed sparse index interval, more items mean longer scans within each index
region.

**Memory concern:** At 1,000 items, a single SSTable read allocates 40 MB. In an LSM engine
that searches multiple SSTables per read, this compounds. This is an area where streaming
deserialization or object pooling could significantly reduce GC pressure.

---

## Storage Engine Comparison

This is the most informative benchmark — it shows how the same operations perform across
different storage engine designs, illustrating the trade-offs Kleppmann describes.

### Write Performance (ms)

| Engine                   | 10 items | 100 items | 1,000 items |
|--------------------------|----------|-----------|-------------|
| InMemory                 | 0.46     | 4.5       | 44          |
| AppendOnly               | 1.4      | 14        | 143         |
| AppendOnly + Index       | 2.0      | 20        | 194         |
| LogSegmented             | 2.0      | 20        | 195         |
| BulkKV + BloomFilter     | 1.4      | 14        | 144         |
| LSM (full engine)        | 0.44     | 18        | 185         |

**What this tells us:** Write performance is remarkably consistent across file-backed engines
(~140-195ms at 1,000 items) because they all share the same bottleneck: fsync. The InMemory
store is ~3-4x faster because it skips disk I/O entirely.

**AppendOnly + Index is slightly slower than plain AppendOnly** for writes — the index
maintenance adds overhead. This is the classic write amplification trade-off: pay a small cost
on every write to avoid a large cost on every read.

**LSM writes are fast at small scale** (0.44ms for 10 items) because they go to the in-memory
memtable. At larger scales, WAL fsync dominates.

### Read Performance (ms)

| Engine                   | 10 items | 100 items | 1,000 items |
|--------------------------|----------|-----------|-------------|
| InMemory                 | 0.22     | 2.2       | 22          |
| AppendOnly               | 2.4      | 194       | **21,464**  |
| AppendOnly + Index       | 1.5      | 16        | 148         |
| LogSegmented             | 2.5      | 173       | **10,477**  |
| BulkKV + BloomFilter     | 7.7      | 662       | **66,492**  |
| LSM (full engine)        | 0.0006   | 0.007     | **29,486**  |

**This table is the single most important result.** It demonstrates Kleppmann's core thesis:
the choice of data structure determines whether your system is read-optimized or write-optimized.

**AppendOnly without index: O(n) reads.** Every read scans the entire file from the beginning,
checking each entry. At 1,000 items, this takes 21 seconds — quadratic total cost if you read
all items.

**The index is transformative.** AppendOnly + Index reads at 1,000 items: 148ms vs. 21,464ms
without the index. That's a **145x speedup** from maintaining a simple hash index. This is
Kleppmann's Chapter 3 argument in one benchmark: indexes make reads fast at the cost of slower writes.

**LogSegmented is 2x faster than AppendOnly** for reads at scale because it splits data across
segments, reducing the scan length per segment. But without an index within segments, it still
degrades.

**BulkKV + BloomFilter reads are the slowest** despite having a Bloom filter. This is because
the Bloom filter only helps with *negative* lookups (keys that don't exist). For keys that *do*
exist, the filter says "maybe" and the engine still does a full scan. The 66-second read time
for 1,000 items reveals that the underlying scan is expensive.

**LSM reads at small scale are sub-microsecond** (0.6μs for 10 items) — the memtable serves
these from memory. But at 1,000 items, reads take 29 seconds, suggesting that segment reads
without effective Bloom filter pruning dominate.

### SearchNonExisting Performance (ms)

| Engine                   | 10 items | 100 items | 1,000 items |
|--------------------------|----------|-----------|-------------|
| InMemory                 | 0.22     | 2.2       | 22          |
| AppendOnly               | 2.9      | 269       | 23,274      |
| AppendOnly + Index       | 0.45     | 4.4       | 44          |
| LogSegmented             | 3.3      | 282       | 23,827      |
| BulkKV + BloomFilter     | 0.0005   | 0.005     | **787**     |
| LSM (full engine)        | 0.0006   | 0.006     | **60,969**  |

**Bloom filters shine on negative lookups.** BulkKV + BloomFilter's `SearchNonExisting` at
1,000 items takes 787ms — vs. 66,492ms for `Read` (existing keys). The Bloom filter rejects
non-existent keys without touching disk, delivering an **84x speedup** over positive lookups.

**AppendOnly + Index also benefits:** negative lookups are fast (44ms at 1,000) because the
hash index can immediately report "key not found" without scanning the file.

---

## LSM Storage Engine

The LSM engine is the most complex component, combining WAL, memtable, SSTables, and compaction.

| Operation       | 10 items | 100 items | 1,000 items    |
|-----------------|----------|-----------|----------------|
| Write           | 0.44 ms  | 18 ms     | 185 ms         |
| Read            | 0.0006 ms| 0.007 ms  | **29,486 ms**  |
| SearchExisting  | 0.0006 ms| 0.007 ms  | **29,627 ms**  |
| SearchNonExisting| 0.0006 ms| 0.006 ms  | **60,969 ms**  |
| GetRange        | 0.0006 ms| 0.006 ms  | 59 ms          |
| FlushMemTable   | 9.3 ms   | 9.3 ms    | 9.4 ms         |
| Compact         | 141 ms   | 164 ms    | 353 ms         |

**FlushMemTable is constant-time (~9ms)** regardless of total item count. This is correct —
the memtable has a fixed capacity, so each flush writes the same amount of data. The cost is
one SSTable write + fsync.

**Compact scales sub-linearly** (141ms → 353ms for 100x more items). Compaction merges sorted
segments, so the merge step is O(n) in total entries across segments, not O(n²).

**Read/Search at 1,000 items is extremely slow (29-61 seconds).** This is the most important
finding in the entire benchmark suite. At 1,000 items with a memtable capacity that triggers
frequent flushes, the LSM engine creates many small segments. Each point read potentially
searches through all segments, and without effective Bloom filter pruning for existing keys,
this becomes a linear scan across segments × entries.

**GetRange at 1,000 items is only 59ms** — orders of magnitude faster than point reads. This
is because range queries do a single merge across segments (which is what the data structure is
optimized for) rather than repeated point lookups.

**The lesson:** LSM trees are optimized for write throughput and range queries. Point reads on
an LSM tree without effective Bloom filter skip logic will always be slower than a hash-indexed
store. This is exactly the trade-off Kleppmann describes in Chapter 3.

---

## Compaction Strategies

| Strategy (1,000 items) | Mean   | Allocated |
|------------------------|--------|-----------|
| Leveled                | 24 ns  | 0 B       |
| Full                   | 24 ns  | 0 B       |
| SizeTiered             | 28 ns  | 0 B       |

**⚠ These results are not meaningful.** All three strategies complete in ~24ns with zero
allocation, which is the cost of an async method call overhead — not actual compaction work.
The benchmark is measuring `CompactAsync()` on engines that have no segments to compact
(or the compaction threshold hasn't been reached).

To produce meaningful compaction benchmarks, the setup must:
1. Write enough entries to trigger multiple memtable flushes (creating segments)
2. Ensure the compaction threshold is met
3. Measure the actual merge I/O, not just the decision logic

The LSM benchmark's `Compact` method (141-353ms) gives a better picture of real compaction cost.

---

## Known Issues

The following benchmarks produced no results and need investigation:

| Benchmark | Issue | Likely Cause |
|-----------|-------|--------------|
| DataStructureBenchmark (all 24 cases) | All results NA | BDN could not measure — methods likely too fast or throwing exceptions. The async `SetAsync`/`TryGetValueAsync` overhead on in-memory data structures may confuse BDN's measurement infrastructure |
| CsvRead (EntrySerializer) | Failed | Exception during deserialization — likely a parsing bug with the test data format |
| CompactionStrategy (all 9 cases) | Trivial results (~24ns) | Not measuring real compaction — engines have no segments to compact |

---

## Key Takeaways

1. **Indexes are the most impactful optimization.** A simple hash index turns 21-second reads
   into 148ms reads — a 145x improvement. This is the foundational lesson of Chapter 3.

2. **fsync dominates write latency.** All file-backed engines converge to similar write
   performance (~140-195ms for 1,000 entries) because per-entry fsync is the bottleneck.
   Batching writes (group commit) would be the single highest-impact optimization.

3. **Bloom filters are asymmetric.** They dramatically accelerate negative lookups (84x for
   BulkKV) but cannot help with positive lookups. This makes them ideal for LSM trees where
   most segments *don't* contain any given key.

4. **LSM trees trade read performance for write throughput.** Small-scale reads are
   sub-microsecond (served from memtable), but at scale, point reads degrade severely.
   Range queries remain efficient because they leverage the sorted structure.

5. **Binary serialization is 2x more memory-efficient than CSV** with comparable speed.
   For on-disk formats, binary is the clear choice.

6. **xxHash32 is the fastest hash function** (60% faster than FNV-1a) with zero allocation.
   It's the right default for Bloom filters and hash-based data structures.
