# Migration Guide — Boutquin.Storage 1.x → 2.0.0

2.0.0 is an explicit major-version break in the wire format emitted by the
source generator for `[Key]` and `[StorageSerializable]` records. **Data
written by a 1.x binary cannot be read by a 2.0.0 binary.** This document
covers why, how to detect the incompatibility at runtime, and the operational
sequence for migrating a deployment.

This is a one-way migration. There is no rollback path once 2.0.0 writes begin.

## What changed

| Surface | 1.x | 2.0.0 |
|---|---|---|
| `[StorageSerializable]` value wire format | Positional, untagged, fixed-width `BinaryWriter` bytes | Tagged wire (DDIA 2e Ch. 5): per-field tag + wire-type varint, ULEB128 integers, zigzag signed, length-prefixed strings, unknown-tag skip |
| `[Key]` wire format | Positional, untagged | Ordered wire: byte-lex-comparable via BE + sign-bit flip for signed integers, UTF-8 terminator escape for strings, 1-byte null discriminator |
| Supported property types | 8 primitives (`int`, `long`, `string`, `float`, `double`, `bool`, `byte`, `char`) | 22 primitives + enums + nullable value types. See [`source-generator-type-reference.md`](source-generator-type-reference.md) |
| `[Tag(N)]` on each property | Not required | **Required** — BSSG010 raises Error if missing |
| `DateTime` / `nint` / `nuint` as property types | Allowed via BSSG001 path | BSSG008 with targeted remediation (use `DateTimeOffset` / `DateOnly` / `int` / `long`) |
| Schema evolution | Adding any field breaks the wire | Adding a tagged field on `[StorageSerializable]` is forward- and backward-compatible — old readers skip unknown tags, absent tags populate as `default`. (`[Key]` ordered wire is NOT evolvable this way — any key-field change alters the byte-lex encoding and is breaking) |
| Row segment format type | `RowSegmentFormat` | `SstSegmentFormat` (block-structured SSTable: 16 KB blocks, per-block CRC32C, prefix compression, footer-embedded bloom filter + block index) |
| Per-append WAL durability | `WriteAheadLogOptions.FsyncOnEveryAppend` | Removed — group commit is the only durability mode; `GroupCommitBatchSize = 1` is the per-append-durability replacement |
| Tombstones | `enableTombstones` constructor opt-in | Removed — tombstones are always on (a DDIA-prescribed correctness primitive, not an option) |
| Engine construction | Multi-parameter constructors | A single `LsmStorageEngineOptions<TKey, TValue>` record carries every knob |
| `LsmTimeSeriesStore<TDatasetKey, TRecord>` constraint | `TRecord : ISerializable<TRecord>, IHasDate, new()` | Unchanged — `TRecord : ISerializable<TRecord>, IHasDate, new()`. A `[StorageSerializable]`-only dated record (no `[ColumnarStorable]`, no `IColumnarRehydratable`) instantiates the store/engine with the default row format and round-trips. The `IColumnarRehydratable<TRecord>` requirement lives only on the columnar construction path (`ColumnarTimeSeriesStoreOptions<TRecord>` / `ColumnarTimeSeriesEngineBuilder`), where `[ColumnarStorable]` auto-emits it for free |
| `ITimeSeriesStore<TDatasetKey, TRecord>` surface | Range/coverage/gap/delete | Adds `GetTailAsync(dataset, count, onOrBefore, projection)` (newest-N, date-descending, optional as-of bound) — **breaking for custom implementors** of the interface |

## Why the break

The 1.x generator emitted the positional, untagged format every DDIA chapter
on encoding and evolution explicitly recommends against:

- Adding a field reorders the byte stream for every deployed reader binary.
- Reordering properties in source silently changes the wire format.
- `int DayNumber` for a 2020s `DateOnly` spent 20 bits of information in 32
  bits; `long Volume` for a 7-digit daily volume spent 24 bits in 64.
- `DateOnly`, `decimal`, enums, most numeric primitives were rejected with
  BSSG001. Consumers hand-rolled `ISerializable<T>` or stringified fields at
  an ingest boundary — exactly the boilerplate the generator was built to
  eliminate.

2.0.0 takes the one-time cost of breaking the wire format and spends it on
the full DDIA prescription: explicit tags, varint integers, wire-type hints
for unknown-tag skip, and a separate byte-lex path for LSM keys.

## Fail-fast detection (tag 0 schema-format version)

Every 2.0.0 tagged-wire record carries a tag-0 schema-format version byte as
its first field. A 2.0.0 reader reaching 1.x-written data performs three
checks in order:

1. Read the length prefix and scope to the record payload. 1.x bytes are not a
   length prefix, so this step often fails first: a declared length larger than
   the bytes remaining throws `EndOfStreamException`, and a malformed or
   oversized length varint throws `InvalidDataException`.
2. The first tag MUST be 0 with wire type `Varint`. A tag that decodes to an
   invalid wire type throws `InvalidDataException`; any other first tag throws
   `Boutquin.Storage.Domain.Exceptions.UnsupportedFormatVersionException`
   with a message naming the record type and pointing to this document.
3. The varint value following tag 0 MUST equal 1.

Because 1.x data has no marker, which check rejects it depends on its bytes.
A 1.x payload can therefore surface as `UnsupportedFormatVersionException`,
`InvalidDataException` or `EndOfStreamException`.

**Detection is best-effort, not a guarantee.** The three checks only inspect the
first few bytes, so some 1.x payloads pass all of them and deserialize without
any exception. A 1.x record whose leading bytes happen to read as a length of 2
followed by `00 01` is read as an empty 2.0.0 record: a 1.x
`(int Population, string Name)` written with `Population = 65538` has the bytes
`02 00 01 00 04 4C 69 6D 61` and comes back with every field defaulted. Likewise
a 1.x `SerializableWrapper<long>` whose bytes open `04 00 01 08` is read as a
different value. Such payloads are rare for typical data, but no check in the
reader can rule them out, so the absence of an exception proves nothing.

The reliable 1.x signal is an out-of-band one: a directory name, manifest, or
version file that the consumer writes itself and checks before it opens any data.
The reader's exceptions are a useful second line of defence, not the primary one.

Detection is also **per-record**, not startup-time validation. The engine
itself does not sniff the data directory on open — a `Deserialize` call that
fails throws, and the exception propagates through the read call chain to the
consumer's orchestration layer.

Consumers SHOULD treat `UnsupportedFormatVersionException`, `InvalidDataException`
and `EndOfStreamException` raised by a `Deserialize` of a data directory as a
one-way signal: the directory is 1.x (or corrupt) and needs rebuilding.
`UnsupportedFormatVersionException` alone is not a reliable detector, and a
`Deserialize` that returns normally does not prove the bytes are 2.0.0.

`[Key]` types do not carry the tag-0 marker (ordered wire is positional and
the whole key format changed shape). A 1.x-written key read by a 2.0.0
ordered-wire deserialiser will either decode to garbage or throw
`EndOfStreamException` depending on the byte layout — there is no reliable
content-based version sniff. The operational sequence below avoids the
situation entirely by requiring a full rebuild.

## Consumer migration playbook

1. **Consume the 2.0.0 NuGet.** `Boutquin.Storage` major-version bump; expect
   compile errors on every annotated record until step 2 is complete.
2. **Add `[Tag(N)]` to every property on every `[Key]` / `[StorageSerializable]`
   type.** BSSG010 lists the remaining sites with a concrete `Tag(N)`
   suggestion per property. The Roslyn fix is mechanical for positional-
   constructor records — add `[property: Tag(N)] ` before each parameter's
   type.
3. **Audit enum fields.** If you stringified an enum at a 1.x key-builder
   boundary to dodge BSSG001, you can drop the stringify step — enums now
   encode natively to their underlying integer kind. Revert the string
   workaround and let the enum flow through as-is. This is where the MarketData
   `PriceBarDatasetKey.Frequency` byte savings land.
4. **Replace hand-rolled `ISerializable<T>` implementations.** If a type used
   `BinaryWriter` directly to dodge BSSG001 on `DateOnly` / `decimal` / `Guid`,
   the hand-roll is now pure debt — the generator covers those types natively.
   Delete the hand-roll, add `[StorageSerializable]` + per-property `[Tag]`.
5. **Reserve retired tags after adopting tagged schemas.** The 1.x positional
   format had no tags to reserve. Once tags have been assigned, mark a removed
   field's number with `[ReservedTag(N)]` so it cannot be reused. This protects
   tagged row schemas; any ordered-key layout change still requires rebuilding
   the keys, regardless of tag hygiene.

6. **Declare `[DecimalScale(N)]` on every `decimal` `[Key]` property** —
   BSSG017 raises Error otherwise. The ordered wire canonicalises every value
   to scale `N` value-preservingly so byte-lex order matches
   `decimal.Compare`; values not exactly representable at scale `N` throw at
   encode time.
7. **Replace `string?` key properties with non-nullable `string`** — BSSG019
   raises Error (the ordered wire has no null discriminator for strings; an
   empty string is a valid key component).
8. **Audit `[ColumnarStorable]` property shapes** — BSSG020 raises Error on
   any settable property whose type has no columnar encoding. The supported
   column set is `DateOnly`, `int`, `long`, `decimal`, `double`, `float`,
   `bool`, `string`, and enums (stored as the underlying value widened to
   int64). Get-only properties are excluded automatically as derived state.
   For an unencodable property: make it get-only if derived, reshape it to a
   supported type, or take the record to the row segment format (tagged wire
   handles wrappers, `Nullable<T>`, and collections natively).
9. **Use a record type for every `[ColumnarStorable]` target** — BSSG022 raises
   Error when the attribute is applied to a non-record class or struct. Generated
   rehydration relies on record construction and copy semantics.
10. **Re-ingest data.** There is no one-shot 1.x → 2.0.0 migration tool. The
   operational sequence below lays out the full process.

## Operational sequence

```
┌──────────────────────────────────────────────────────────────────────────┐
│ 1. Stop all 1.x-binary processes reading or writing the data directory.  │
│ 2. Inventory sources that can replay into the engine: raw upstream       │
│    adapters, event logs, snapshot tarballs, replay queues, etc.          │
│ 3. Capture a one-shot backup of the 1.x data directory (tarball, restic  │
│    snapshot, …). Belt-and-braces — no rollback once step 5 writes begin. │
│ 4. Delete the 1.x data directory, or rename it to `*_v0_backup`.         │
│ 5. Deploy the 2.0.0 binary. Start with an empty (or newly created) data  │
│    directory.                                                            │
│ 6. Trigger re-ingest from the sources identified in step 2.              │
│ 7. Verify cold-start correctness against a known fixture before resuming │
│    production traffic.                                                   │
└──────────────────────────────────────────────────────────────────────────┘
```

### Single-runner discipline

Re-ingest must run in a single process against a given data directory.
Multiple concurrent processes on the same directory during re-ingest is
undefined behaviour. Use whatever lock mechanism the consumer already relies
on (file lock, database row lock, Kubernetes `Job concurrency=1`, etc.).

### Crash-mid-stream recovery

If re-ingest crashes mid-stream, the data directory is in a mixed state.
Recovery is:

1. Delete the partial 2.0.0 data directory.
2. Restart re-ingest from scratch.

This is the simplest contract that requires no migration-state tracking.
Consumers wanting atomic re-ingest should re-ingest into a staging directory
and swap atomically at the end.

### Re-running against partially-populated data is undefined

The tagged wire does not deduplicate or merge: a second write of the same
logical record produces a second copy of the wire payload. Re-ingest
pipelines are responsible for their own idempotency. Most rely on the LSM
engine's last-writer-wins compaction, which converges correctly if the same
logical keys are written. If a consumer has no idempotency layer, delete the
2.0.0 data directory first.

## Decision tree for the "permanent data" case

Not every 1.x data directory has a replay source. For data derived from
upstream APIs that rotate or expire, or for data that was the authoritative
source, the choices are:

1. **Re-derive from on-disk backups.** If the 1.x data was itself derived
   from text inputs, CSVs, or a replay log, re-derive into 2.0.0.
2. **Stand up a shim.** If a future 1.x-reader library ships (not currently
   planned), use it to convert 1.x → JSON → 2.0.0. This is a one-time
   consumer-owned project, not a Boutquin.Storage-shipped tool.
3. **Accept data loss for non-load-bearing records.** For logs, metrics,
   ephemeral cached data — often cheaper to start empty than to migrate.
4. **Pin to 1.x.** Stay on the 1.x line until one of the above options
   becomes available. 1.x is in maintenance-only mode; no new features
   will ship there.

## What `UnsupportedFormatVersionException` looks like in practice

```csharp
try
{
    var bar = Bar.Deserialize(stream);
    ...
}
catch (Exception ex) when (ex is UnsupportedFormatVersionException
                              or InvalidDataException
                              or EndOfStreamException)
{
    // Incompatible encoding or corruption: preserve the source and check its
    // out-of-band version marker before deciding to migrate or restore a backup.
    logger.LogError(ex, "Cannot decode data. Verify format and integrity before recovery.");
    throw;
}
```

The failure is per-record and the detection is best-effort: confirm the
directory's version through an out-of-band marker before relying on this catch,
because some 1.x bytes deserialize without an exception. A mixed 1.x/2.0.0
directory (which should not happen if the operational sequence was followed)
will usually throw on the first 1.x-written record; the 2.0.0-written
records ahead of it will have read successfully and the engine state will
contain partial data. This is why
step 4 of the operational sequence deletes the old directory rather than
writing alongside.

## Enum stringify workaround — before and after

One concrete example from the MarketData DatasetKey adoption. 1.x restricted
keys to the 8-primitive set, so enum fields were stringified at the key-
builder boundary:

```csharp
// 1.x — BarGranularity.Daily → "Daily" (6 bytes with length prefix)
[Key]
public partial record struct PriceBarDatasetKey(
    string Provider,
    string Frequency,        // was: BarGranularity, stringified to dodge BSSG001
    string Ticker,
    string Exchange);

// 2.0.0 — BarGranularity natively typed (ordered wire: underlying int, 4 bytes BE sign-flipped)
[Key]
public partial record struct PriceBarDatasetKey(
    [property: Tag(1)] string Provider,
    [property: Tag(2)] BarGranularity Frequency,
    [property: Tag(3)] string Ticker,
    [property: Tag(4)] string Exchange);
```

The 2.0.0 key bytes are shorter (4 fixed bytes vs a string body plus the
ordered wire's terminator escape) and the enum value is first-class at the
deserialize boundary (no string-to-enum parse). Note the `[Tag(N)]`
annotations on key properties are declaration bookkeeping for evolution
policy — the ordered wire itself is a flat byte-lex concatenation with no
per-field tag framing. The savings compound across the shipped DatasetKey
types that used this workaround.

## Wrapper bytes (`SerializableWrapper<T>`)

`Boutquin.Storage.Domain.Helpers.SerializableWrapper<T>` shipped in 1.x with
a raw binary-primitive wire: an `int` was 4 little-endian bytes, a `string`
was a 7-bit-length-prefix UTF-8, and so on — straight `BinaryWriter` output.
2.0.0 rewrites `Serialize` and `Deserialize` to emit the same tagged-wire
frame the source generator emits for `[StorageSerializable]` records: a
ULEB128 length prefix, then tag 0 schema-format version 1, then tag 1
carrying the wrapped value under the wire type that matches `T`.

The practical consequence:

- **Wire break.** 1.x-persisted wrapper bytes cannot be read by the 2.0.0
  wrapper. The tag-0 schema-format check normally fails (the first bytes of 1.x
  wrapper output are the value itself, not the combined `(tag << 3) | wire`
  varint for tag 0), so `Deserialize` usually throws — most often
  `EndOfStreamException` or `InvalidDataException` from the length-prefix check
  that runs first, and `UnsupportedFormatVersionException` only when the
  leading bytes happen to scope and then miss tag 0. Detection is best-effort:
  a 1.x `long` whose bytes open `04 00 01 08` passes every check and is read as
  a different value without any exception, so a 1.x wrapper file must be
  identified through an out-of-band marker, not by the first `Deserialize`.
- **Who is affected.** A grep of the 2.0.0 source tree
  (`rg -l 'SerializableWrapper<' src/`) returns only the declaration site —
  no production code persists raw wrapper bytes to disk. Test fixtures that
  write and read in the same process are unaffected (the wrapper is
  self-consistent across its 2.0.0 write and read paths). If a downstream
  consumer had persisted wrapper bytes to a custom data store, the same
  re-ingest pattern applies as for `[StorageSerializable]` records above:
  treat the exception as a signal to rebuild the store from source.

The migration consolidates the wrapper and generator onto a single wire
convention for Domain, which is the correctness benefit; the wire-break
cost is paid once.

## SSTable on-disk format — single format, fail-closed (corrected)

> An earlier revision of this guide described a "v1 → v2 dual-read" where the
> 2.0.0 reader fell through to a 1.x flat-stream reader on a footer-magic
> mismatch, and documented the footer magic as `BSS2`. **Neither is true of
> the shipped 2.0.0.** The new block format has no earlier released version.
> The separate 1.x flat-stream format requires explicit export/re-ingest;
> this section describes the only reader included in 2.0.0.

The 2.0.0 block-structured SSTable is the **only** row-segment format. Every
SSTable carries a trailing footer whose four-byte ASCII magic is **`BSS1`** —
the family identifier for the block-structured layout — followed by a
`format_version` byte that iterates *within* that family (currently `1`).
`BSS2` is reserved for a hypothetical future reworked family and has never
been written to disk.

The reader is **fail-closed**: a footer-magic mismatch throws (the message
names the expected `BSS1` and the observed bytes) rather than falling through
to any alternate parser. A 1.x flat-stream file — or its companion `.idx`
sparse-index file — placed in a 2.0.0 segment directory is unreadable;
export through the 1.x application and re-ingest into a separate 2.0.0 directory.
Keep the 1.x directory and its `.idx` files until the migrated data is verified.

### Codec story

The Storage 2.0 footer (format version 1) carries a one-byte `CodecId` that a reader resolves via
`ICompressionCodecRegistry` to decompress each block. The default registry
pre-registers LZ4 (`0x01`, default), GZip (`0x02`), Brotli (`0x03`), and
a passthrough codec (`0x00`). Reopening a Storage 2.0 SSTable whose footer declares
a `CodecId` no registered codec handles raises
`UnsupportedCompressionCodecException` at open time — the SSTable is not
partially usable, it is unreadable until the matching codec is registered.

See [`sstable-block-format.md`](sstable-block-format.md) for the full
format-version-1 reference.

## 2.0.0 behavioral contracts worth knowing before you migrate

These are not wire-format changes, but a consumer arriving from 1.x should
plan around them:

- **Recovery is fail-closed on segment corruption — with a cache-tier
  opt-out.** A manifest-listed segment that fails its CRC32C/size check at
  engine open (or is missing from disk) throws an `InvalidDataException`
  naming the segment; recovery never silently excludes acknowledged data,
  because exclusion resurrects older values and deletes from elder segments.
  Stores whose contents are rebuildable from an upstream source (cache
  tiers) may opt into
  `LsmStorageEngineOptions.CorruptSegmentRecovery = ExcludeAndSurface`: the
  damaged segment is skipped and reported through the
  `OnCorruptSegmentExcluded` callback so the consumer can shrink its
  coverage accounting and re-ingest the lost span deliberately.
- **Columnar decimals are value-preserving, not representation-preserving.**
  Dictionary encoding collapses value-equal decimals of different scales
  (`1m` vs `1.0m`) to one stored representation, and the scaled-integer
  codec re-scales to the column's common scale on decode. Values compare
  equal after a flush + read, but `decimal.GetBits` scale may differ — do
  not rely on stored scale for display or tick-size semantics through the
  columnar path. The tagged row wire preserves scale exactly.
- **Sort direction vs scan direction.** `ColumnSortSpec` accepts per-column
  `SortDirection.Ascending` / `Descending` and governs the physical row
  order written into columnar segment blocks. `ScanDirection` (read-path
  iteration) is an independent surface and ships ascending-only; the
  recency-biased read demand is served by `ITimeSeriesStore.GetTailAsync`
  (newest-N, date-descending, optional as-of bound) instead.

## Runtime dependencies

`Boutquin.Storage.Domain` depends on the public `Boutquin.Domain` 1.0.0 package.
Infrastructure depends on Domain plus `K4os.Compression.LZ4`, `Google.Protobuf`,
`System.IO.Hashing` and `TestableIO.System.IO.Abstractions`; versions are pinned in
`Directory.Packages.props`. Choosing `GZipCodec`, `BrotliCodec`, or
`NoCompressionCodec` avoids executing LZ4 compression but does not remove the
Infrastructure package's transitive LZ4 dependency.
