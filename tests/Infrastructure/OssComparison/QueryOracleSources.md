# Algorithm and query oracle provenance

These selected comparisons execute Storage entry points. They do not port complete
upstream suites or establish equivalent performance, formats or transaction semantics.

| Upstream | Revision and file | Selected comparison | Adaptation boundary |
|---|---|---|---|
| [SMHasher](https://github.com/aappleby/smhasher/blob/07bb4de10a63e8cc2e1724865454eba635742383/src/MurmurHash3.cpp) | `07bb4de10a63e8cc2e1724865454eba635742383`, `src/MurmurHash3.cpp` | Executed `MurmurHash3_x86_32` for lengths 0–64, bytes `(i*37+length) mod 256`, seed `0xc58f1a7b` | 65 actual C++ reference outputs embedded in `AlgorithmOracleTests`; public-domain declaration in the source. No x64/128 equivalence claimed. |
| [Faiss](https://github.com/facebookresearch/faiss/blob/c919a604e621876c43f33c54dc87a6c2e6eccc97/tests/test_index.py) | `c919a604e621876c43f33c54dc87a6c2e6eccc97`, `TestIndexFlat.do_test` | Compare k-nearest IDs/distances to independently computed all-pairs distances | C# scalar arithmetic replaces NumPy; 4 dimensions, 70 vectors, replacement/deletion and 15 queries per index/metric. Storage L2 returns the square root, inner-product distance is negative dot product, cosine is additional. IVF probes every partition; HNSW uses efSearch above corpus size. This tests exhaustive settings, not approximate recall at small efSearch. |
| [Apache Flink](https://github.com/apache/flink/blob/dab40d31eba000411f919fe9b77afc6d77bfa1a7/flink-runtime/src/test/java/org/apache/flink/streaming/runtime/operators/windowing/TumblingEventTimeWindowsTest.java) | `dab40d31eba000411f919fe9b77afc6d77bfa1a7`, tumbling/sliding `testWindowAssignment` | 5-second windows at 0, 4999 and 5000 milliseconds, sliding by 1 second | UTC UnixEpoch translation; compare exact half-open boundaries. Offset/stagger APIs are not present in Storage. Additional shuffled-event aggregation uses an independent offline membership sum. |
| [Apache Lucene](https://github.com/apache/lucene/blob/a9779ef3c1939fcd8aaab1b9485f036311797a1d/lucene/core/src/test/org/apache/lucene/search/TestPhraseQuery.java) | `a9779ef3c1939fcd8aaab1b9485f036311797a1d`, `testExact`, `testSlop1` | Ordered phrase adjacency and skipped-token cases | Random histories add replacement/removal and repeated terms. Storage never reverses phrase terms; Lucene permits transpositions at larger slop. Storage uses smoothed TF-IDF, not BM25. An independent token-position scan defines expected membership. |

Faiss's MIT license is retained in [Faiss-LICENSE.txt](Faiss-LICENSE.txt).
Apache-derived cases are adapted by Pierre G. Boutquin (2026); Apache-2.0 is retained
in the repository [LICENSE.txt](../../../LICENSE.txt). Upstream copyright notices and full source
license files accompany the pinned source archive used to reproduce this work.

Other oracles are independently authored: bit-at-a-time Castagnoli CRC; FNV arithmetic
in a wider integer type; `System.IO.Hashing.XxHash32` from the centrally pinned package;
SortedDictionary for B/B+/red-black/skip-list histories; list-based weighted LRU;
set relation for secondary indexes; nested-loop relational join and cube membership;
scalar rows for vectorized filter/projection/aggregation. Bloom tests require no false
negatives and check a fixed-corpus false-positive sanity ceiling, not exact membership.
Counting Bloom removal is exercised only for previously inserted occurrences.

All seeds and source input construction are in the tests. Existing focused suites
add deletion, collision, approximate-search recall, cancellation, invalid-input,
stream-join, delivery, spill-file cleanup and ownership checks; these new tests do
not replace those contracts.
