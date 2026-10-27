# SSTable Block Format (v2.0.0)

> **Prerequisite reading:** [`SortedStringTable`](../README.md#sorted-string-table-sstable) and the [`LsmStorageEngine`](../README.md#lsm-storage-engine) sections of the README. This guide describes the single SSTable on-disk format `SortedStringTable` reads and writes at v2.0.0. There is no fallback reader: a 2.0.0 binary reads only this format, and the 1.x flat-stream format is not readable by 2.0.0 (see [the migration guide](migration-1x-to-2x.md) for the explicit export/re-ingest path 1.x users follow).

## What changed in 2.0.0

In 1.x, `SortedStringTable` wrote a flat stream of length-prefixed entries with an optional whole-file GZip wrapper. Reads decompressed the entire file. This worked for small segments and broke down at scale — a 200 MB segment held a few thousand keys but cost a 200 MB decompression to look one up, and a bit-flip anywhere in the file invalidated everything.

The Storage 2.0 block format (format version 1) introduces:

- **Block-structured payload** — entries are grouped into ~16 KB blocks; each block compresses and validates independently.
- **Per-block CRC32C** — corruption is detected at block granularity; surrounding blocks remain readable.
- **Restart points + prefix compression inside each block** — `O(log K_per_block)` binary search within a block; shared key prefixes are stored once between restart points.
- **Footer with bloom bits + block index + min/max key** — the per-segment Bloom filter is *durable* (survives a restart, no rebuild needed); the block index gives `O(log B)` block-level binary search before within-block search.
- **Pluggable compression codec** via `ICompressionCodec` — default LZ4, with GZip / Brotli / NoCompression shipped as alternatives.

## File layout

```
┌──────────────────────────────────────────────────────────────────┐
│ Block 0          (compressed payload + CRC32C, ~16 KB)           │
│   uncompressedLength (int32 LE, non-negative)                    │
│   compressedLength   (int32 LE, non-negative)                    │
│   compressed payload (compressedLength bytes)                    │
│   CRC32C             (uint32 LE, Castagnoli 0x1EDC6F41)          │
├──────────────────────────────────────────────────────────────────┤
│ Block 1                                                          │
├──────────────────────────────────────────────────────────────────┤
│ ...                                                              │
├──────────────────────────────────────────────────────────────────┤
│ Block N-1                                                        │
├──────────────────────────────────────────────────────────────────┤
│ Footer (head-to-tail; reader seeks here via the trailing length)  │
│   magic 'B','S','S','1'  (4 bytes)                               │
│   format_version = 1     (1 byte)                                │
│   codec_id               (1 byte) 0x01 LZ4 | 0x02 GZip | 0x03 Brotli | 0x00 None
│   entry_count            (uint32 LE)                             │
│   min_key  (varint length-prefixed)                             │
│   max_key  (varint length-prefixed)                             │
│   max_sequence_number    (varint)                               │
│   block_index_count      (varint)                               │
│     per entry: first_key (varint length-prefixed), offset (varint)
│   bloom: bits_count (varint), hash_count (varint),              │
│          bytes_length (varint), bloom_bytes                      │
│   tombstone_count        (varint)                               │
│     per entry: key (varint length-prefixed), deletion_seqno (varint)
│   optional custom Bloom descriptor (BLM1, kind 1, codec + state) │
├──────────────────────────────────────────────────────────────────┤
│ footer_crc32c    (uint32 LE — Castagnoli over every footer body byte above)
│ footer_length    (uint32 LE — body + crc, excludes this 4-byte word)
└──────────────────────────────────────────────────────────────────┘
```

The reader bounds the trailing `footer_length` against the file length before seeking to the footer, then checks the `'B','S','S','1'` magic and footer CRC before parsing its fields. The footer carries `max_sequence_number` (so recovery reseeds the engine's monotonic counter without rescanning blocks) and the tombstone list (deleted keys paired with their deletion seqnos — the input the compactor uses for safe tombstone GC). `format_version` is a single byte starting at `1` at GA; `'BSS1'` is the four-byte file-family signature. There is no pre-1 footer (greenfield premise), so the native fields and tombstone count are always present and no fallback reader exists. A custom Bloom descriptor may follow the tombstones, inside the footer CRC; native Bloom counts are then zero. See [segment Bloom filters](segment-bloom-filters.md) for the descriptor layout, disabled mode and codec lifecycle.

## Block layout (inside one block)

Each block decompresses to a sequence of entries followed by the restart-point trailer:

```
┌──────────────────────────────────────────────────────────────────┐
│ Entry 0 (restart point — full key, shared_prefix_length = 0)     │
│   shared_prefix_length (uvarint)                                 │
│   suffix_length        (uvarint), suffix_bytes                   │
│   seqno                (uint64 LE — the entry's sequence number) │
│   value_kind           (1 byte: 0x00 Inline | 0x01 ValueLogPointer)
│   value_length         (uvarint), value_bytes                    │
├──────────────────────────────────────────────────────────────────┤
│ Entry 1 (delta from previous key — same layout; only the key     │
│          suffix after the shared prefix is stored)               │
├──────────────────────────────────────────────────────────────────┤
│ ...                                                              │
├──────────────────────────────────────────────────────────────────┤
│ Entry K-1                                                        │
├──────────────────────────────────────────────────────────────────┤
│ Restart points trailer                                           │
│   restart_offset (int32 LE × restart_count — byte offset of each │
│                   restart entry from the start of the payload)   │
│   restart_count  (int32 LE — last 4 bytes of the payload)        │
└──────────────────────────────────────────────────────────────────┘
```

Every entry, restart point or not, has the same shape: `shared_prefix_length`, `suffix_length`, the suffix bytes, the 8-byte little-endian `seqno`, the one-byte `value_kind`, `value_length`, and the value bytes. A restart point stores `shared_prefix_length = 0`, so its suffix is the full key. A `ValueLogPointer` entry carries its 12-byte `(fileIndex: uint32, offset: uint64)` value-log pointer in the value slot. The trailer is written offsets first and `restart_count` last, so a reader takes the count from the final four bytes and walks back to the offsets.

Worked example — one Inline entry, key `AA`, seqno `0x0102030405060708`, value `BB CC`, as the uncompressed payload:

```
hex : 00 01 AA 08 07 06 05 04 03 02 01 00 02 BB CC 00 00 00 00 01 00 00 00
```

`00` shared_prefix_length, `01` suffix_length, `AA` the key, `08 07 06 05 04 03 02 01` the seqno little-endian, `00` value_kind Inline, `02` value_length, `BB CC` the value, then the trailer: restart offset `00 00 00 00` and restart_count `01 00 00 00`.

Every 16th entry is a **restart point** stored with its full key — a binary-searchable anchor inside the block. Intermediate entries store only the suffix that differs from the previous key. For string keys with shared prefixes (a common case in LSM workloads — `provider/dataset/identifier/...` paths), this avoids repeating shared prefixes before compression; the saving depends on the key distribution. Reader binary-searches the restart points first, then linear-scans within a 16-entry segment. Standard LevelDB block layout.

## Compression codec — the LZ4 default

The default codec is **LZ4**. Concretely, `SortedStringTable.Write(entries)` compresses each block payload with LZ4 before writing; reads decompress the block on access. The codec is pluggable via `ICompressionCodec` (see "Pluggability" below) and the per-segment choice is recorded in the footer's `codecId` byte so a reader can decode segments written under any codec the registry knows.

### Choosing a codec

LZ4 is the default for fast block decoding. GZip and Brotli provide alternatives when
compression ratio matters more than decode cost; `NoCompressionCodec` is useful for
already-compressed payloads. Actual throughput, compression ratio, and cache effects depend
on the data and hardware. This guide makes no measured cross-codec performance claim;
run the benchmark harness on the intended workload before choosing.

The LZ4 implementation lives in Infrastructure, which also depends on Google.Protobuf,
System.IO.Hashing and TestableIO.System.IO.Abstractions. Domain depends on Boutquin.Domain
1.0.0. Selecting another codec changes the compression path, not the NuGet dependency graph.

## Pluggability: `ICompressionCodec`

```csharp
namespace Boutquin.Storage.Domain.Interfaces.Compression;

public interface ICompressionCodec
{
    /// <summary>Stable single-byte identifier persisted in the SSTable footer.</summary>
    byte CodecId { get; }

    /// <summary>Human-readable codec name for diagnostics.</summary>
    string Name { get; }

    int Compress(ReadOnlySpan<byte> source, Span<byte> destination);
    int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, int expectedUncompressedLength);
    int MaxCompressedLength(int uncompressedLength);
}
```

Shipped implementations in `Boutquin.Storage.Infrastructure.Compression`:

| Type | `CodecId` | When to choose |
|---|---|---|
| `LZ4Codec` | `0x01` | Default. OLTP workloads. Read latency matters more than disk size. |
| `GZipCodec` | `0x02` | BCL compression path. Compare size and decode cost on your workload; the package still depends on LZ4. |
| `BrotliCodec` | `0x03` | Cold-storage / archival workloads where on-disk size dominates and decode latency is acceptable. Zero new dep. |
| `NoCompressionCodec` | `0x00` | Already-compressed payloads (e.g., a value column that's an embedded JPEG). Diagnostic / benchmark use. |

Codec selection at write time:

```csharp
using var sst = new SortedStringTable<MyKey, MyValue>(
    "data", "segment_001.sst",
    codec: new BrotliCodec());     // override default LZ4
sst.Write(entries);
```

The footer records the chosen `codecId` byte so a reader picks the right codec from the registry — no out-of-band metadata required.

### Read-side codec resolution

```csharp
public interface ICompressionCodecRegistry
{
    void Register(ICompressionCodec codec);
    ICompressionCodec? Resolve(byte codecId);
    IReadOnlyCollection<ICompressionCodec> Registered { get; }
}
```

The default registry covers the four shipped codecs. Custom codecs (Snappy via a third-party wrapper, Zstd, or experimental codecs) register with `Register`, which throws `ArgumentException` when the `CodecId` is already taken. `Resolve` returns `null` for an unregistered byte; reads of a segment carrying an unregistered `codecId` raise `UnsupportedCompressionCodecException` with the offending byte and a hint to register the matching codec.

## Read path

```
TryGetValueAsync(key):
  1. Footer is loaded once at SortedStringTable open and cached in memory.
  2. Bloom filter test — definitive miss returns immediately, no I/O.
  3. Block index binary search — O(log B) — finds the block whose
     first key ≤ key < next block's first key.
  4. Block read — one disk read of (compressedLength + 12) bytes.
  5. CRC32C check on compressed bytes — corruption raises CorruptBlockException.
  6. Codec.Decompress — produces the uncompressed block payload.
  7. Restart-point binary search — O(log K_per_block) — finds the restart
     point ≤ the searched key. Each restart offset must lie inside the
     entries region and strictly increase; any other offset, and any
     truncated or out-of-range entry varint, raises CorruptBlockException.
  8. Linear scan from restart point — O(restartInterval) — returns the entry
     or null.

GetRangeAsync(startKey, endKey):
  Same as Get, but yields every entry in [startKey, endKey], crossing block
  boundaries by re-running the codec for each new block. Bounded by the range,
  not by the dataset.
```

Key properties:

- **One disk read per block touched.** No inflated I/O for keys outside the requested range.
- **Independent corruption recovery.** A bit-flip in block 17 invalidates block 17 only; blocks 0–16 and 18–N stay readable.
- **Bloom across restart.** Bloom bits live in the footer; table recovery restores the internal `SsTableBloomBits` reader directly from the persisted bits and hash parameters. No "the next compaction will rebuild your bloom filters" gap.

## Write path

```
Write(entries):  // entries MUST be sorted (ArgumentException if not)
  1. Open the temporary segment writer.
  2. For each entry:
     a. If currentBlockSize would exceed targetBlockSize (default 16 KB), close
        the current block (compress, CRC, write).
     b. If this entry is the next restart point (count % restartInterval == 0),
        write it with shared_prefix_length = 0 (the full key).
     c. Otherwise, write the shared prefix length and the key suffix. Either way the
        entry then carries its seqno, value_kind and length-prefixed value.
  3. Close the final block.
  4. Write footer (head-to-tail): magic 'B','S','S','1', format_version=1, codecId,
     entryCount, minKey, maxKey, max_sequence_number, block index (firstKey + offset
     per block), native Bloom fields, tombstone list, optional custom Bloom descriptor.
  5. Append footer_crc32c, then the trailing footer_length word.
  6. fsync.
  7. Atomic rename from .tmp to final filename (per `DurableFile.AtomicReplace`).
```

The completed segment is published before the LSM manifest references it. Atomic replacement and OS flush barriers have platform-specific limits; see [durability boundaries](TRADEOFFS.md).

## Format versions

2.0.0 ships a **single** SSTable footer format: family `'BSS1'`, `format_version = 1`. There is no dual-read and no legacy reader. When `SortedStringTable` opens an existing file, the footer reader either recognises the `'BSS1'` magic, verifies the footer CRC32C and `format_version`, and parses the footer, or the open **fails closed** — `TryOpenExistingFile` throws `InvalidDataException` ("not a valid SSTable: …; no fallback reader exists"), whose message names the check that failed — the trailer word, or, with the footer reader's own exception attached as the inner exception, the magic, the footer CRC32C, `format_version`, or a truncated body. A file the magic check rejects — including any 1.x flat-stream SSTable — is never silently degraded to "empty" and never partially read.

The 1.x flat-stream format (whole-file GZip optional) is therefore **not readable** by a 2.0.0 binary. The migration path is explicit: read/export with a 1.x binary, then re-ingest with 2.0.0 (which writes the `'BSS1'` format). See [the migration guide](migration-1x-to-2x.md) for the full operational sequence.

`format_version` (the byte after the magic) starts at `1` at GA and is the iteration number *within* the `'BSS1'` family. A future reworked layout would bump the four-byte family signature (`'BSS2'`, etc.); within `'BSS1'`, a `format_version` bump would carry genuine dual-read — neither exists at v2.0.0. The 2.0.0 reader accepts only `format_version = 1`: a `'BSS1'` footer declaring any other version fails the open with `InvalidDataException` instead of being parsed with the version-1 field layout.

## References

- DDIA 2nd ed (Kleppmann & Riccomini 2026) Ch. 4 "Storage and Retrieval" pp. 119–129 — block structure, sparse indexes, bloom filters, prefix compression in SSTables.
- LevelDB block format: `table/block_builder.cc`, `table/block.cc` (the prior-art for restart points + prefix compression).
- Compression implementation and identifiers: [shipped codecs](../src/Infrastructure/Compression/).
- CRC32C (Castagnoli polynomial 0x1EDC6F41) — same as the columnar footer (`docs/columnar-time-series.md`).
