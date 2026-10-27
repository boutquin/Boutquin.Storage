# WiscKey Value-Log Subsystem

> Store large values outside the SSTable so compaction only rewrites small pointers.
> DDIA 2e p. 130, "Key-value separation… values stored in a 'value log'… SSTables then
> store only pointers to the values."

`Boutquin.Storage.Infrastructure.ValueLog` is an append-only, CRC-protected value-log
subsystem. It ships in v2.0.0 fully integrated end-to-end: the writer, reader, manifest,
and on-disk format are production-grade, **and** `LsmStorageEngine.SetAsync` dispatches
through the size threshold into the value log, carries the resulting pointer through flush
and compaction, dereferences it transparently on read, and reclaims dead segments via
whole-file garbage collection (see § *Engine integration (shipped)* below).

## Overview

For workloads where `sizeof(value) >> sizeof(key)` — e.g., serialised `Bar[]` slices that
cluster around 200 B – 1 KiB against 16–32 B keys — LSM write amplification is dominated
by value rewrites. DDIA describes the fix:

1. Large payloads are appended to a dedicated, append-only **value log**.
2. The SSTable block entry keeps only a fixed-width pointer into that log.
3. Compaction rewrites tiny pointers; the original bytes stay in place.
4. A periodic **garbage collector** reclaims value-log files whose pointers are no
   longer reachable.

The on-disk surface is this subsystem's responsibility; `LsmStorageEngine` drives it
through the MemTable's pointer-carrying value slot. The full engine path is documented in
§ *Engine integration (shipped)* below.

## File format

Each value-log segment is named `valuelog_NNNNNN.log` (monotonic 6-digit counter).

### Header (8 bytes, written once on segment creation)

```
offset  size  field            encoding
------  ----  ---------------  --------
     0     4  magic            ASCII 'B','S','V','L'
     4     4  format_version   LE u32 (= 1 in v2.0.0)
```

### Entry frame (repeats from offset 8 to EOF)

```
offset    size  field         encoding
--------  ----  ------------  --------
+0           4  length        LE u32 — byte length of `payload`
+4           4  header_crc    LE u32 — CRC32C over [length‖entry_kind‖seqno]
+8           1  entry_kind    0x00 Payload, 0x01 Reserved, 0x02-0xFF Reject
+9           8  seqno         LE u64 — engine sequence number
+17          N  payload       `length` raw bytes
+17+N        4  payload_crc   LE u32 — CRC32C over `payload`
```

CRC32C uses the Castagnoli polynomial (same as the SSTable block CRC). The header checksum
covers every other header field (bytes +0..+3 and +8..+16), so a damaged length prefix, kind
byte or sequence number is detected on its own; the payload checksum covers the payload only.

**Recovery.** On open, the writer verifies every sealed segment against its seal (see
§ *Manifest* below) and walks the active segment — the highest one, the only segment that can hold
an interrupted append — frame by frame, applying one deterministic tear rule that rests only on
bytes the writer authenticated. A torn tail — the interrupted final append — is a partial frame
header at end of file; a frame header whose landed bytes (a prefix a writer could produce, so a
non-zero kind byte must be a defined kind) are followed only by zeros to end of file, the all-zero
run from a frame start being the empty prefix (the file length reached the disk, the frame bytes
did not; no frame encodes as zeros, because an all-zero header fails its checksum); a valid header
whose payload is short at end of file; or a payload checksum failure on a frame whose checksum
trailer did not fully land — its last byte is zero and the checksum bytes that landed equal the
checksum of the payload — followed by nothing but zeros to end of file. A torn tail is truncated.
Any other damage — a header checksum failure with a non-zero byte after the header, a payload
checksum failure on a frame whose last byte landed (each append is fsync'd before its pointer is
returned, so that frame was written completely and acknowledged) or whose landed checksum bytes
disagree with its payload, or a payload checksum failure on a frame with a non-zero byte after it —
is corruption of acknowledged frames: open fails closed with
`CorruptValueLogException` and leaves the file untouched. The writer refuses an empty payload
(`ArgumentException`), so every complete frame holds a non-zero byte after its header — even an
all-zero payload, because CRC32C of a run of zero bytes is zero only for runs whose length is a
multiple of 2^31 − 1, longer than any frame — and a complete header that fails its checksum is
never mistaken for an unwritten tail. That includes a damaged header on the
final acknowledged frame, and a crash that reorders page writeback inside the final, unacknowledged
append so its header is lost while later bytes landed; neither loses an acknowledged value, but the
second needs an operator. Recovery does not interpret the kind byte; a frame both checksums accept
is kept, and the reader refuses an unknown kind when a pointer dereferences it. `MaxEntryBytes`
governs appends only, so lowering it never truncates frames written under a larger cap.

### In-SSTable pointer

A `ValueLogPointer` in an SSTable block entry (the body after `ValueKind = 0x01`) is
**12 bytes**: `fileIndex:u32 LE` followed by a `u64 LE` whose low 40 bits are the `offset`
and whose high 24 bits are the `sequenceCheck`. `fileIndex` is the integer suffix of
`valuelog_NNNNNN.log`; `offset` is the absolute byte offset of the entry frame's first
byte (the `length` field) within the file; segments rotate before an offset could exceed
40 bits (1 TiB). `sequenceCheck` is `seqno mod (2^24 − 1) + 1` for the frame the pointer was
issued for. The reader compares it with the frame it finds and throws
`CorruptValueLogPointerException` on a mismatch, so an address that was truncated and then
reused by a later write is reported as lost rather than resolving to another key's value.

> **Design note.** The pointer body was widened from an earlier 8-byte single-file
> offset to the 12-byte `(fileIndex, offset)` tuple so multiple rotated value-log
> segment files can coexist and the garbage collector can reclaim whole files once
> every live SSTable reference has rolled past them.

### Manifest (`valuelog.manifest`)

An 8-byte header (`'B','S','V','M'` + version 1) followed by fixed 29-byte records:

```
offset  size  field          encoding
------  ----  -------------  --------
     0     1  kind           0x00 = AddFile, 0x01 = DeleteFile, 0x02 = SealFile
     1     4  fileIndex      LE u32
     5     8  sealedLength   LE u64 — final byte length of the segment (seal records; zero otherwise)
    13     8  frameCount     LE u64 — number of frames in the segment (seal records; zero otherwise)
    21     4  segmentCrc32c  LE u32 — CRC32C over the segment's bytes [0, sealedLength) (seal records; zero otherwise)
    25     4  crc32c         LE u32 over the first 25 bytes
```

**Seal.** Rotation seals the outgoing segment before its successor exists: the seal record names
the segment's final byte length, frame count and checksum, and it is fsync'd before the successor's
file is created and its add recorded. Nothing is appended to a sealed segment again, so a crash can
interrupt an append only in the highest, unsealed segment; a crash between the seal and the
successor's add leaves a sealed highest segment, and the writer then starts a fresh one. Recovery
verifies every sealed segment against its seal — exact length, every frame intact, the recorded
frame count and checksum — and fails closed with `CorruptValueLogException` on any difference,
including every shape the tear rule would repair in the active segment.

Recovery replays the manifest to compute the live file set. Each record's CRC covers every
field of the record, and records have no length field, so the tear rule is deterministic: a
trailing partial record, or a final record whose landed bytes (a prefix with a defined kind byte,
whose landed CRC bytes, if any, equal the leading bytes of the CRC over the record's fields) are
followed only by zeros to end of file (an all-zero record is the empty prefix), is a torn append
and is truncated. A complete record whose CRC fails with no zero suffix, a final record whose
landed CRC bytes disagree with its fields (the record CRC's high byte is genuinely zero in about one
record in 256, so a zero last byte alone does not make a tear), or a CRC failure anywhere before the
final record, is fatal — including the final record, whatever segment its damaged bytes now name.
Dropping a torn final record is refused too when an on-disk segment holds frames that no manifest
record lists as live or as deleted: appends to a segment start only after its add record is
durable, so that segment's add was acknowledged, and dropping it would let the writer's orphan
sweep delete the segment. The same directory evidence is checked on every open, not only when a
record is dropped: a manifest that ends cleanly on a record boundary, or holds only its header,
while a segment holds frames it neither lists nor deletes fails closed with
`CorruptValueLogException`, and the writer's orphan sweep removes an unlisted segment only when the
manifest records its delete or the segment holds no frame. A manifest no longer than its header that
holds only a prefix of the header (or zeros) is a creation a crash interrupted; its header is
rewritten, and a missing manifest is created, only when no segment beside it holds frames — no
segment exists before the manifest header is durable. Otherwise open fails closed and rewrites
nothing. A failed append is truncated back before the error propagates, and if that truncation
fails the manifest refuses further records until it is reopened.

The active segment carries no seal, so truncating it exactly at a frame boundary is
indistinguishable from those frames never having been written. The format detects crash tears and
damage to acknowledged bytes; deliberate frame-boundary truncation of fsync'd data in the active
segment is outside its threat model.

## Configuration

All knobs live on `Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogOptions`:

| Field                      | Default         | Effect                                                                |
| -------------------------- | --------------- | --------------------------------------------------------------------- |
| `ValueLogThresholdBytes`   | `1 024`         | Values strictly larger go to the log; at/below stay inline. Between 64 bytes and 128 MiB. |
| `SegmentMaxBytes`          | `256 MiB`       | The writer seals the current segment and rotates to a fresh one when the next frame would not fit within this size. |
| `ReadHandleCacheSize`      | `64`            | LRU-bounded pool of `SafeFileHandle`s across live value-log files.    |
| `MaxEntryBytes`            | `64 MiB`        | Largest single value the value-log writer accepts, else `ValueTooLargeException`. Applies only to values the engine routes to the log (strictly larger than `ValueLogThresholdBytes`); a value at or below the threshold stays inline and is bounded by the threshold alone. Recovery ignores it. |
| `ValidateCrcOnRead`        | `Always`        | `Always` verifies a frame's header and payload CRC32C on every read; `Never` skips both checks (trusted media only) and a damaged frame is served as stored. The sequence-number and kind checks still run. |

`MaxEntryBytes` is a cap on the value-log writer, not on `SetAsync` as a whole. To bound every
value, keep `ValueLogThresholdBytes` at or below `MaxEntryBytes`; with the threshold above the cap,
values between the two are stored inline and accepted. For a segment format without value-log pointer support (including columnar), the engine
bypasses value-log routing regardless of these options and admits inline values up to 128 MiB.
Larger values fail before acknowledgment.

## Tuning guide

- **Threshold (`ValueLogThresholdBytes`).** Default `1 024` balances keeping small values
  cheap on the read path against letting large ones escape compaction write-amp. For
  MarketData's `Bar[]` slices (200 B – 1 KiB range), `512` or `256` may materialise more
  of the win — measure before flipping. Raising the threshold past the largest value
  effectively disables the value log.
- **`SegmentMaxBytes`.** Larger segments amortise rotation cost (fewer fsync'd headers)
  at the price of coarser GC granularity — a single segment that ages out half-reachable
  holds twice the wasted bytes before reclamation is eligible. Measure the space/rotation trade-off for the intended workload.
- **`ReadHandleCacheSize`.** Open-file budget, not memory budget. The LRU evicts least-
  recently-used handles; a hot working set larger than the cap pays reopens. `64` suits
  most workloads — raise only after profiling shows handle churn.
- **`ValidateCrcOnRead`.** Leave at `Always` unless the media is trusted: `Never` removes the
  only read-time defence against bitrot in a frame, and the value log has no compression or other
  per-entry transform whose cost it could offset.

## Operator runbook

### `UnrecognisedValueLogFormatException` on startup

One of the live segment files (or the manifest itself) opens with bytes that do not
match the expected magic or declares a `format_version` this build does not accept.
Root causes in descending order of likelihood:

1. **A non-value-log file was dropped into the `valuelog/` directory.** Move it out and
   restart.
2. **A forward-incompatible writer touched the directory.** The only format version
   shipped is `1`; anything else means a future build wrote these files. Upgrade the
   engine or restore from backup.
3. **On-disk corruption at the head of the file.** Restore from backup.

### `CorruptValueLogException` on startup (mid-file CRC mismatch)

A frame in a value-log segment failed its header or payload CRC check with bytes after it, or a
sealed segment no longer matches its seal in the manifest (length, frame count or checksum).
This is **not** a torn final append — the subsystem truncates those silently, and only in the
active segment. Mid-
file corruption indicates bitrot in a region that MUST remain intact, so startup fails
fast rather than silently dropping reachable entries.

Recovery options:

1. **Restore the affected segment from backup.** The fileIndex + offset in the exception
   pinpoint the corrupt frame.
2. **Manually truncate at the corrupt offset and accept lost entries.** Requires tooling
   that knows which SSTable pointers become dangling; in v2.0.0 this tooling is not
   shipped — open a follow-up spec before attempting.

When the message names a segment that "holds frames" which the manifest neither lists nor
deletes, the manifest itself lost records: it was truncated, emptied, zero-filled, replaced or
deleted while segments holding acknowledged values remained. No segment was touched. Restore
`valuelog.manifest` from backup. Removing the named segment also lets the engine start, but it
discards every acknowledged value the segment holds.

### `CorruptValueLogPointerException` on a hot read

A live `TryGetValueAsync` dereferenced a `ValueLogPointer` whose entry-frame header or
payload CRC failed at read time, or whose frame carries a different sequence number than the pointer records. The SSTable entry itself is intact; the corruption is localised to the
value-log frame. The engine propagates the exception to the caller and does NOT self-
quarantine the SSTable entry (that would violate snapshot read-only semantics).

Operator response: the field `FileIndex` + `Offset` on the exception identify the
corrupt frame. Restore from backup or replace the surrounding segment. This entry presumes
`ValidateCrcOnRead = Always`; with `Never` neither checksum is verified on read, so a damaged
frame is served as stored and the exception cannot fire for it.

### `MissingValueLogFileException`

A live manifest entry names a segment file that is absent on disk. Typically indicates
external deletion between the last clean shutdown and the current startup. If the
manifest had already tombstoned the file and the tombstone was fsync'd, the missing
file would be expected and ignored — so the exception is a real missing-file event.

Restore the named file from backup and retry.

### `DanglingValueLogPointerException`

WAL replay encountered a `ValueLogPointer` whose referenced value-log entry was either
never written or was truncated. Violates the value-log durability ordering invariant (the
WAL frame is fsync'd AFTER the value-log entry) and so indicates external tampering —
file deletion, disk replacement, snapshot restore from an inconsistent pair — rather
than a crash this design tolerates.

Investigate the surrounding file state before attempting repair.

### `ValueTooLargeException` on `Set`

A value routed to the value log — one strictly larger than `ValueLogThresholdBytes` — exceeded
`ValueLogOptions.MaxEntryBytes` (64 MiB default). Either raise the cap (alongside
`SegmentMaxBytes`, so one value cannot dominate a segment) or split the payload upstream. A value
at or below the threshold never reaches the value log, so this exception cannot be raised for it.

## Engine integration (shipped)

`LsmStorageEngine<TKey, TValue>` routes every `SetAsync` call through the
value-log threshold: payloads above `ValueLogOptions.ValueLogThresholdBytes`
append to the value log first, then a `ValueLogPointer` lands in the MemTable
slot, in the WAL frame, and eventually in the SSTable block entry's
`ValueKind.ValueLogPointer` slot. Flush preserves the `(Inline | ValueLogPointer)`
discriminator per entry; compaction carries the pointer bytes verbatim through
the k-way merge; reads dispatch on `BlockEntry.Kind` and dereference pointers
only at the engine layer.

## Value-log garbage collection

`LsmStorageEngine.CompactValueLogAsync()` triggers one whole-file GC round. The
collector scans every live SSTable segment and every snapshot-pinned segment for
`ValueKind.ValueLogPointer` block entries, collecting the live
`(fileIndex, offset)` set `L`. For every closed value-log segment file (the
current append-target is exempt) the collector deletes the file iff `L ∩ file = ∅`.
Snapshot pins keep files alive until the last pinning snapshot is disposed — the
next `CompactValueLogAsync` call after the snapshot disposal reclaims the file.

**When GC runs.** On an explicit `CompactValueLogAsync()` call. v2.0.0 ships
whole-file reclamation only; size- or fraction-triggered auto-GC is a future
increment.

**How to observe progress.** `CompactValueLogAsync()` returns a `GcResult`
carrying `FilesReclaimed`, `BytesReclaimed`, and `FilesSkipped`. Skipped files
are those still referenced by live SSTable or snapshot-pinned pointers.

**Partial-file rewrites are out of scope**. A closed file with even one live
pointer stays on disk until every reference clears.

## Reference

- Kleppmann, *Designing Data-Intensive Applications* 2e, p. 130 ("Key-value separation").
- SSTable block-entry format carrying `ValueKind = ValueLogPointer`:
  [SortedStringTable source](../src/Infrastructure/SortedStringTable/SortedStringTable.cs),
  [BlockFormat source](../src/Infrastructure/SortedStringTable/BlockFormat.cs), and the
  [ValueKind enum](../src/Domain/Enums/ValueKind.cs).
- End-to-end WiscKey path through the LSM engine:
  [LsmStorageEngine source](../src/Infrastructure/LsmTree/LsmStorageEngine.cs) (see
  `SetAsync`, `TryGetValueAsync`, `CompactValueLogAsync`).
