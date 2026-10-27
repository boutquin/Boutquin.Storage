# Text and vector search

Boutquin.Storage provides in-memory retrieval primitives alongside its primary-key storage engines. Text search uses an inverted index. Vector search offers an exact baseline and two approximate nearest-neighbor indexes.

## Text analysis and retrieval

`TextAnalyzer` composes one `ITokenizer` with any number of `INormalizer` instances. `WhitespaceTokenizer` handles ordinary word-oriented text, while `NGramTokenizer` supports substring-oriented indexing. `LowercaseNormalizer` uses invariant case folding, and `StopWordNormalizer` can discard configured terms.

`InvertedIndex<TDocumentId>` supports:

- add or replace by document identifier;
- deletion;
- exact term lookup;
- Boolean AND across terms;
- exact phrases and ordered proximity with a total skipped-token budget; and
- TF-IDF ranked retrieval with deterministic insertion-order tie-breaking.

The index stores sorted term positions per document (whose lengths provide term frequencies) and a duplicate-free postings list per term. Replacing a document removes its old terms before adding the new terms.

```csharp
var analyzer = new TextAnalyzer(
    new WhitespaceTokenizer(),
    new LowercaseNormalizer(),
    new StopWordNormalizer(["the", "a"]));
var index = new InvertedIndex<int>(analyzer);

index.Add(1, "Designing data-intensive applications".AsMemory());
index.Add(2, "Data systems and vector search".AsMemory());

var matches = index.SearchAll(["data", "search"]); // document 2
var ranked = index.Search("data systems".AsMemory(), maxResults: 10);
var phrase = index.SearchPhrase("data systems".AsMemory()); // adjacent analyzed tokens
var proximity = index.SearchPhrase("data vector".AsMemory(), maxSkippedTokens: 2);
```

Phrase matching measures positions in the **analyzed token stream**. A stop word removed by the
analyzer takes no position. Query tokens must occur in order; each repeated token needs its own
occurrence. `maxSkippedTokens` bounds the total unmatched tokens between the first and last
match, not the gap at each pair. Empty analyzed queries return no documents. Replacing or deleting
a document also replaces or deletes its stored positions. This costs one integer per analyzed
occurrence; candidate documents come from the first term's postings, and later positions are
located by binary search.

## Vector distance contract

All vector indexes have a fixed dimension and reject dimension mismatches, `NaN`, and infinity. Distances are ordered from smaller to larger:

- `L2Distance` returns Euclidean distance.
- `CosineDistance` returns `1 - cosine similarity`. Two zero vectors have distance zero; one zero and one non-zero vector have distance one.
- `DotProductDistance` returns the negative dot product so the common smaller-is-better ordering still applies.

Equal distances are resolved by insertion order. An upsert keeps the existing identifier's position in that order. Stored vectors are copied at the API boundary.

## Exact and approximate indexes

`FlatVectorIndex<TId>` scans every vector. It is exact and is the reference oracle for recall measurements.

`IvfVectorIndex<TId>` trains a deterministic coarse k-means quantizer, assigns vectors to the closest centroid, and searches the nearest partitions. Increasing the probe count examines a superset of partitions and therefore provides a direct latency/recall control. Probing every partition is exact.

`HnswVectorIndex<TId>` builds the layered proximity graph described by Malkov and Yashunin. Insertion uses greedy descent at upper layers and bounded candidate expansion at shared layers. `efConstruction` controls graph-build breadth; `efSearch` controls query breadth. Higher values generally increase recall and work. A fixed random seed makes graph construction reproducible.

Each upper-layer node keeps at most `maxConnections` links and each layer-0 node at most `2 × maxConnections`, plus one link to its parent in a layer-0 spanning tree that keeps every stored vector reachable; a node takes at most `maxConnections` children in that tree. A full candidate list admits only a vector that ranks before its furthest entry (nearer, or at equal distance and inserted earlier). Both bounds hold when many vectors tie, such as positive multiples under cosine distance or mutually equidistant points, so no vector becomes a hub whose links grow with the index and the work per insert or query does not grow with the number of tied vectors.

The implementation follows the algorithmic structures in [Product Quantization for Nearest Neighbor Search](https://doi.org/10.1109/TPAMI.2010.57) for coarse inverted-file retrieval and [Efficient and Robust Approximate Nearest Neighbor Search Using Hierarchical Navigable Small World Graphs](https://doi.org/10.1109/TPAMI.2018.2889473) for HNSW.

```csharp
var exact = new FlatVectorIndex<string>(3, new CosineDistance());
var ivf = new IvfVectorIndex<string>(3, new CosineDistance(), partitionCount: 16, probeCount: 2);
var hnsw = new HnswVectorIndex<string>(3, new CosineDistance(), efSearch: 48);

exact.Upsert("document-1", [0.8F, 0.1F, 0.2F]);
var nearest = exact.Search([0.7F, 0.1F, 0.3F], count: 10);
```

## Measuring recall and latency

`VectorSearchBenchmark` builds all three indexes over the same deterministic clustered data and measures identical top-10 query batches:

```bash
dotnet run -c Release --project benchmarks/StorageEngine -- --search-only
```

The vector tests compute recall against `FlatVectorIndex`, verify that IVF recall is monotonic as probe count increases, and require useful HNSW recall on held-out clustered queries. Run them with:

```bash
dotnet test tests/Infrastructure/Boutquin.Storage.Infrastructure.Tests.csproj \
  --filter FullyQualifiedName~Search
```

`SearchDemo` in the samples project applies keyword and semantic retrieval to the same three documents so result semantics can be compared directly.

## Recorded measurements

A short run on 2026-09-17 indexed 1,000 vectors of dimension 32 and performed **eight** top-10
queries per benchmark operation (indexes 0, 125, …, 875). Mean operation times were IVF with
four probes 75.49 microseconds, HNSW efSearch=48 192.71 microseconds, and flat search
321.27 microseconds. These are illustrative corpus-specific batch times, not per-query service
latencies. The separate clustered correctness corpus measured recall@5 of 100% for IVF with
1/4/8 probes and 92% for HNSW efSearch=64. Those recall settings and inputs differ from the
latency benchmark; the two results do not establish a joint latency/recall guarantee.
