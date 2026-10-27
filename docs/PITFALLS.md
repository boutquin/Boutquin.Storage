# Storage Engine Implementation Pitfalls

Lessons learned from bugs discovered during code review of this codebase.
Each pitfall describes a trap, why it's wrong, and where the fix lives.

---

## 1. Hash Algorithm Specification Fidelity

### Trap: Substituting similar-looking operations in hash algorithms

**Bug**: xxHash32's single-byte remainder processing used XOR (`^=`) instead of ADD (`+=`)
to combine each trailing byte into the hash accumulator.

**Why it's wrong**: The xxHash specification explicitly uses addition for single-byte remainders.
XOR and ADD have different mixing properties — XOR is its own inverse (applying it twice cancels
out), while ADD propagates carry bits upward, providing better avalanche. Using XOR produced
incorrect hash values that silently diverged from the spec.

**Why it's hard to catch**: Both operations produce plausible-looking 32-bit hash values. Without
a reference implementation to compare against, the bug is invisible. The code "works" — it just
produces wrong hashes.

**Fix pattern**: Always validate custom hash implementations against the platform's reference
implementation. In .NET, `System.IO.Hashing.XxHash32.HashToUInt32()` provides the canonical
answer.

**Code**: `src/Infrastructure/Algorithms/XxHash32.cs:127` — changed `hash ^= b * Prime5` to
`hash = unchecked(hash + b * Prime5)`.

**Test**: `tests/Infrastructure/HashAlgorithmTests.cs` — `ComputeHash_ShouldMatchDotNetReferenceImplementation`
validates against `System.IO.Hashing.XxHash32` with multiple inputs including edge cases
(empty, 1-byte, 5-byte, 17-byte) that exercise different code paths.

---

## 2. File I/O Durability

### Trap: Assuming `Stream.Flush()` persists data to disk

**Bug**: The WriteAheadLog called `Flush()` after writing entries, but `Flush()` only flushes to the OS
page cache — it does NOT issue an fsync. A power failure after `Flush()` but before the OS
writes the page cache to disk loses the data silently.

**Why it's wrong**: The entire point of a Write-Ahead Log is crash durability. Without fsync,
the WriteAheadLog provides no durability guarantee — it's just a regular file write.

**Fix pattern**: Use `FileStream.Flush(flushToDisk: true)` which issues `fsync`/`FlushFileBuffers`.
This is the .NET equivalent of POSIX `fsync()`. The current WAL flusher applies it once per
group-commit batch; each `AppendAsync` completes only after the batch containing that frame is
durable. Set `WriteAheadLogOptions.GroupCommitBatchSize` to `1` when every append must form its
own durable batch.

**Code**: `src/Infrastructure/WriteAheadLog/WriteAheadLog.cs` — the flusher writes a bounded batch,
calls `Flush(flushToDisk: true)`, and only then completes the queued append requests.

### Trap: Opening a new FileStream per write operation

**Bug**: Each `AppendAsync` call opened a new `FileStream`, wrote, and closed it. This caused:
1. Repeated file open/close overhead (kernel transitions + handle allocation)
2. No guarantee of sequential writes (OS may reorder between opens)
3. File locking issues when tests tried to read the file concurrently

**Fix pattern**: Open a persistent `FileStream` on first write, keep it open for the lifetime
of the WriteAheadLog, and close it on dispose. Use lazy initialization (`EnsureStreamOpen()`) to avoid
opening the file before it's needed.

**Code**: `src/Infrastructure/WriteAheadLog/WriteAheadLog.cs` — `_appendStream` field with
`EnsureStreamOpen()` lazy opener and `CloseAppendStream()` for recovery.

### Trap: File locking conflicts between write and read streams

**Bug**: The WriteAheadLog's persistent append stream was opened with `FileShare.None`, preventing
`RecoverAsync` from opening a second `FileStream` for reading. Tests that wrote entries
and then called `RecoverAsync` on the same WriteAheadLog instance hit `IOException`.

**Fix pattern**: Close the append stream before opening the read stream in `RecoverAsync`,
or use `FileShare.ReadWrite`. The WriteAheadLog implementation closes the append stream before recovery
and reopens it afterward.

**Code**: `src/Infrastructure/WriteAheadLog/WriteAheadLog.cs` — `CloseAppendStream()` called at the
start of `RecoverAsync`.

---

## 3. Endianness and Binary Format Portability

### Trap: Using `BinaryWriter`/`BinaryReader` for on-disk formats

**Constraint**: Persistent formats need an explicit byte order. .NET `BinaryWriter` writes
integer primitives little-endian; it does not use native host order. This repository uses
`BinaryPrimitives` so each field's endianness and width are visible at the encode/decode site.

**Fix pattern**: Use `System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian()` and
`ReadInt32LittleEndian()` to explicitly specify byte order. Write into `Span<byte>` buffers
rather than using `BinaryWriter`.

**Code**: `src/Infrastructure/WriteAheadLog/WriteAheadLog.cs` — all length and checksum fields use
`BinaryPrimitives` with explicit little-endian encoding.

---

## 4. Corruption Recovery Semantics

### Trap: Skipping a corrupt entry and continuing recovery

**Bug**: When the log encountered a checksum mismatch during recovery, it `continue`d to the next
entry — consuming the corrupt frame's *declared* payload length and resuming parsing after it —
instead of stopping. An earlier version of this document actively prescribed this
`continue`-don't-`break` pattern; it is wrong and is now forbidden.

**Why it's wrong**: Recovery must **fail closed** — stop reading at the first frame that fails
validation. A corrupt frame's length prefix is itself untrustworthy: if the bytes that encode the
payload length are flipped, "skip the declared payload and resume" lands the reader at an arbitrary
offset *inside* a later frame. From there the parser reads corruption-controlled bytes as if they
were a valid entry and silently fabricates entries from misaligned data. Skip-and-continue trades a
known, bounded loss (one torn frame) for an unbounded one (every subsequent read desynchronised),
and it does so invisibly — the recovered entries look structurally fine.

This is the defect class behind the prior `FileRaftLog.Replay` mid-file-truncation bug: a corrupt
frame in the middle of the log was skipped, and replay resumed against misaligned bytes.

**Why it's hard to catch**: A single-corruption test that only checks "the valid entries before the
corruption came back" passes under *both* the wrong and the right behaviour. The divergence only
shows when a corrupt frame sits in the *middle* of the log with more bytes after it — skip-and-continue
fabricates phantom entries from the misaligned tail; fail-closed stops and (for mid-file corruption)
throws.

**Fix pattern**: Two steps, in order.

1. **STOP at the first corrupt frame.** A frame is corrupt if it fails *any* of CRC validation,
   payload-length sanity, entry-kind / value-kind dispatch, or deserialization. Authenticate the
   framing fields, not just the payload — the CRC scope must cover the entry-kind, value-kind,
   sequence number, and payload bytes together, so a bit-flip in any framing field is caught
   *before* the length prefix is trusted. Surface every entry up to (but not including) the corrupt
   frame.
2. **THEN classify what stopped you** — not every stop is an error:
   - **Torn tail** (no length prefix left, or a partial final frame at physical EOF): the legitimate
     end-of-segment case from a crash mid-append. Truncate cleanly at the last good frame boundary
     and recover the prior entries **without** a corruption warning. (The WAL goes further and
     *repairs* the torn tail so the next append lands at a clean boundary — see the WAL design
     doc-comment's "Standalone-append safety" paragraph.)
   - **Mid-file corruption** (a complete-looking frame fails validation with more bytes — possibly
     valid frames — after it): real data loss in the middle of the log. **Throw loud** (a typed
     corruption exception); do not silently truncate, and never skip past it.

**Code (reference-correct siblings)**:
- `src/Infrastructure/WriteAheadLog/WriteAheadLog.cs:285` — `RecoverAsync` stops at the first frame
  that fails validation, with CRC-authenticated framing and the single torn-tail-vs-corruption rule
  of the threat model below. See the `<remarks>` "Recovery semantics" paragraph
  (doc-comment lines 73–83).
- `src/Infrastructure/ValueLog/ValueLogReader.cs:229-235` — `ReadFully` honours each short read's
  returned count and throws `EndOfStreamException` only on a genuine 0-byte return before the
  bounds-checked buffer is filled (real truncation), never returning a partially-filled buffer that
  a caller could mistake for valid data.
- `src/Distributed/Consensus/FileRaftLog.cs:214` — `Replay` stops at the first unreadable frame
  rather than skipping past it.

**Test**: `tests/Infrastructure/WriteAheadLog/Phase12HardeningTests.cs` —
`RecoverAsync_CrcMismatchOnFirstFrame_StopsAtZeroEntries`, `RecoverAsync_UnknownEntryKind_StopsAtCorruption`,
and `RecoverAsync_UnknownValueKind_StopsAtCorruption` verify the fail-closed stop at the first corrupt
frame; `ReadFrameAsync_TruncatedAfterCrcBytes_StopsCleanly` and `tests/Infrastructure/WriteAheadLogTests.cs` —
`RecoverAsync_ShouldSkipCorruptedEntries` verify the torn-tail case stops cleanly and recovers the
prior valid entries.


### Recovery threat model of the durable logs

What the six durable logs (WAL, value log, value-log manifest, append-only engine, event log, Raft
log) detect, and what they do not. The rules are the two steps above made deterministic: a frame
header carries its own CRC32C, and the tear rule below decides what counts as an interrupted write.

| Damage | Outcome |
|---|---|
| Interrupted final append: a partial header, a valid header with a short payload, or a last-frame payload checksum failure followed only by zeros — in the WAL and the value log only a frame whose last byte is zero and whose checksum bytes that landed match the landed payload; in the Raft log only a frame whose end marker is zero; in the append-only engine only a frame whose last payload byte is zero | **Torn tail.** Truncated in the active tail only (last WAL segment, highest value-log segment, the single file of the other logs); every earlier frame is recovered without a warning |
| Event log or Raft log: a last-frame payload checksum failure that is followed by anything, zeros included, after the frame's end, or whose last byte (the event log's last payload byte, the Raft log's end marker) is non-zero | **Corruption.** These two logs write and fsync one frame per append, so bytes after a frame's end cannot come from that frame's interrupted write, and an interrupted frame ends in zeros. A payload checksum failure is repaired as a torn tail only when the damaged frame ends exactly at end-of-file and its last byte is zero; otherwise `InvalidDataException`, file untouched |
| WAL, value log or append-only engine: a final frame whose last byte landed yet fails its payload checksum | **Corruption.** Each append is fsync'd before it is acknowledged, so a frame whose last byte landed was written completely; `WriteAheadLogCorruptException`, `CorruptValueLogException` or `InvalidDataException`, file untouched |
| Checksum failure with a non-zero byte after it; a complete header that fails its checksum; a torn tail on any segment but the last | **Corruption.** Typed exception (`WriteAheadLogCorruptException`, `CorruptValueLogException`, `InvalidDataException`); file untouched |
| Sealed segment emptied, cut (even at a frame boundary), extended, reordered, renamed or swapped | **Corruption.** WAL: the `SegmentSeal` frame no longer matches. Value log: the manifest seal record's length, frame count and checksum no longer match |
| Missing middle WAL segment; deleted last WAL segment; emptied, header-cut or zero-filled last WAL segment numbered above 1 (only `wal_000001.log` may be cut to nothing); manifest-listed value-log segment missing on disk; lost or emptied value-log manifest beside segments that hold frames | **Fail loud on open.** Never read as an empty log; the value-log orphan sweep never deletes acknowledged segments |
| Active tail or single-file log truncated exactly at a frame boundary; every WAL segment deleted; the oldest WAL segment or segments deleted | **Outside the threat model.** Only valid frames remain and nothing records how many followed, so it is indistinguishable from writes that never happened; recovery returns what remains. A deleted head is also indistinguishable from reclamation: recovery requires only contiguous surviving segment indices, and reclaiming flushed segments legitimately removes an oldest-first prefix. The writes in the deleted segments are gone, and an LSM engine over that WAL reopens without them |
| Append-only engine: damage to a final payload whose genuine last byte is zero (an encoding that ends in a zero byte, such as a small little-endian integer) | **Outside the threat model.** The frame ends with its payload, so the damaged bytes are ones a payload prefix followed by zeros could produce: recovered as a torn append, and that acknowledged record is lost. The event log's JSON payloads never end in a zero byte, and the Raft log ends every frame with a fixed non-zero end marker, a byte the writer always makes non-zero |

The last row is a declared limit, not an oversight: closing the tail cases needs a durable
high-water mark written on every append, which costs a second fsync per append, and closing the
lost-head case needs a durable reclamation low-water mark that recovery checks, which the WAL does
not keep. Truncation or deletion of fsync'd data is deliberate tampering, not a crash state.

**Tests that execute this table**:
`tests/Infrastructure/WritePathAndRecoveryDocsAccuracyTests.cs` runs the WAL rows
(`Wal_TornActiveTail_…`, `Wal_MidFileDamage_…`, `Wal_SealedSegmentCutAtAFrameBoundary_…`,
`Wal_MissingMiddleSegment_…`, `Wal_ActiveTailCutExactlyAtAFrameBoundary_…`) and the Raft-log rows
(`RaftLog_…`) against the real logs;
`tests/Infrastructure/ValueLog/ValueLogSegmentSealIntegrityTests.cs` and
`ValueLogManifestLossDetectionTests.cs` cover the value-log seal and manifest rows;
`tests/Infrastructure/AppendOnlyFileStorageEngineTornTailTests.cs` covers the append-only engine.
`tests/Infrastructure/RecoveryThreatModelDocsAccuracyTests.cs` executes the rows that name a
specific log: the deleted oldest WAL segment, the emptied last WAL segment, and which logs repair a
last-frame payload checksum failure followed by zeros.

---

## 5. Sorted Data Validation

### Trap: Silently sorting unsorted input

**Bug**: `SortedStringTable.Write()` accepted unsorted input and silently sorted it using
`items.OrderBy(...)`. This masked bugs in callers — if the MemTable produced unsorted output,
the SSTable would "fix" it silently, hiding the real problem.

**Why it's wrong**: In an LSM-tree, the MemTable guarantees sorted output. If the SSTable
receives unsorted input, something is broken upstream. Silent correction hides the bug.
Fail-fast validation surfaces the problem immediately.

**Fix pattern**: Validate that input is strictly sorted (each key > previous key) and throw
`ArgumentException` if not. Do not sort defensively.

**Code**: `src/Infrastructure/SortedStringTable/SortedStringTable.cs:103-119` — strict
ascending-order validation with `ArgumentException` on violation.

**Test**: `tests/Infrastructure/SortedStringTableTests.cs` —
`Write_WithUnsortedInput_ShouldThrowArgumentException` and
`Write_WithDuplicateKeys_ShouldThrowArgumentException`.

---

## 6. Sync-Over-Async Anti-Pattern

### Trap: Calling `.GetAwaiter().GetResult()` on async methods in synchronous code

**Bug**: `SortedStringTable.Write()` is synchronous but called `_serializer.WriteEntryAsync()`
using `.GetAwaiter().GetResult()`. This blocks the calling thread and risks deadlocks in
UI or ASP.NET contexts with a single-threaded `SynchronizationContext`.

**Fix pattern**: Add a synchronous `WriteEntry()` method to the serializer interface alongside
the async version. Call the synchronous method from synchronous code paths.

**Code**: `src/Domain/Interfaces/IEntrySerializer.cs` — added `void WriteEntry(Stream, TKey, TValue)`.
`src/Infrastructure/Serialization/BinaryEntrySerializer.cs` and `CsvEntrySerializer.cs` — implemented.
`src/Infrastructure/SortedStringTable/SortedStringTable.cs:135` — calls `_serializer.WriteEntry()`.

---

## 7. Thread Safety and Dispose Patterns

### Trap: No use-after-dispose protection

**Bug**: The LSM storage engine and WriteAheadLog had no guards against calling methods after `Dispose()`.
This could corrupt state or throw confusing exceptions (e.g., `NullReferenceException` instead
of `ObjectDisposedException`).

**Fix pattern**: Use `ObjectDisposedException.ThrowIf(_disposed, this)` at the top of every
public method. For the WriteAheadLog, use `Interlocked.Exchange` for thread-safe dispose detection.

**Code**:
- `src/Infrastructure/LsmTree/LsmStorageEngine.cs` — `ObjectDisposedException.ThrowIf` on public operations.
- `src/Infrastructure/WriteAheadLog/WriteAheadLog.cs` — `Interlocked.Exchange(ref _disposed, 1)` in
  `Dispose()`, checked in all public methods.

### Trap: Lock re-entrance in async code

**Bug**: `LsmStorageEngine.ContainsKeyAsync()` called `TryGetValueAsync()` which also acquired
the `SemaphoreSlim` lock. Since `SemaphoreSlim` is not reentrant, this deadlocked.

**Fix pattern**: Extract the lock-free core logic into a private method (`TryGetValueInternalAsync`)
and have both `TryGetValueAsync` and `ContainsKeyAsync` acquire the lock independently, then
call the internal method.

**Code**: `src/Infrastructure/LsmTree/LsmStorageEngine.cs` — `TryGetValueInternalAsync` extracted,
both public methods acquire lock and delegate to it.

### Trap: Non-atomic bulk operations

**Bug**: `LsmStorageEngine.SetBulkAsync()` acquired and released the lock per-item by calling
`SetAsync()` in a loop. Another thread could observe a half-written batch.

**Fix pattern**: Acquire the lock once for the entire batch, loop internally, and check for
flush thresholds inside the loop.

**Code**: `src/Infrastructure/LsmTree/LsmStorageEngine.cs` — `SetBulkAsync` acquires the
semaphore once, loops with internal flush checks.

---

## 8. Defensive Copy for Mutable Return Values

### Trap: Returning internal mutable arrays directly

**Bug**: `MerkleTree.RootHash` returned a direct reference to the internal `_tree[0]` byte array.
Callers could mutate the returned array, silently corrupting the tree's root hash.

**Fix pattern**: Return `(byte[])_tree[0].Clone()` to provide a defensive copy. Callers can
mutate their copy without affecting the tree.

**Code**: `src/Distributed/Merkle/MerkleTree.cs:65` — `RootHash` returns
`(byte[])_tree[0].Clone()`.

**Test**: `tests/Infrastructure/MerkleTreeTests.cs` — `RootHash_ShouldReturnDefensiveCopy`
verifies that modifying the returned array does not affect subsequent `RootHash` calls.

---

## 9. Input Validation at Public API Boundaries

### Trap: Missing null checks on public methods

**Bug**: Multiple public methods across BTree, RedBlackTree, and LSM engine accepted null keys
without validation, leading to `NullReferenceException` deep in internal code instead of a
clear `ArgumentNullException` at the API boundary.

**Fix pattern**: Use `Guard.AgainstNullOrDefault` or `ArgumentNullException.ThrowIfNull` at the
top of every public method that accepts reference-type parameters.

**Code**:
- `src/Infrastructure/DataStructures/BTree.cs` — `Guard.AgainstNullOrDefault` on all public methods
- `src/Infrastructure/DataStructures/RedBlackTree.cs:533` — `ArgumentNullException.ThrowIfNull(key)`
- `src/Infrastructure/LsmTree/LsmStorageEngine.cs` — `Guard.AgainstNullOrDefault` for key and value

### Trap: Missing negative index validation

**Bug**: `MerkleTree.VerifyProof` accepted negative `leafIndex` values, which produced incorrect
array indexing instead of a clear error.

**Fix pattern**: Use `ArgumentOutOfRangeException.ThrowIfNegative(leafIndex)` before any
computation.

**Code**: `src/Distributed/Merkle/MerkleTree.cs` — validates `leafIndex` in `VerifyProof`.

---

## 10. CancellationToken Neglect

### Trap: Accepting CancellationToken but never checking it

**Bug**: All 7 async methods in `BTree` accepted a `CancellationToken` parameter but never
called `ThrowIfCancellationRequested()`. Long-running operations (bulk insert, traversal)
could not be cancelled.

**Fix pattern**: Call `cancellationToken.ThrowIfCancellationRequested()` at the start of every
async method that accepts a token.

**Code**: `src/Infrastructure/DataStructures/BTree.cs` — all 7 async methods now check the token.

**Test**: `tests/Infrastructure/BTreeTests.cs` — 7 tests verify that a pre-cancelled token
throws `OperationCanceledException`.

---

## 11. Compaction Ordering and File Lifecycle

### Trap: Deleting old segments before the compacted segment is fully written

**Bug (prevented by design)**: A naive compaction implementation might delete old segment files
eagerly — e.g., deleting each old segment as its data is read. If a crash occurs after some
old segments are deleted but before the compacted segment is fully written, data is lost.

**Fix pattern**: Write the compacted segment completely first, then delete old segments in a
separate phase. This ensures that at any point during compaction, all data exists in at least
one valid segment:
1. Read all entries from old segments (old segments remain intact)
2. Write merged entries to a new compacted segment
3. Commit the replacement segment set in the manifest
4. Retire old segments; physically delete only after their last snapshot pin is released

**Code**: `src/Infrastructure/LsmTree/LsmStorageEngine.cs` — `CompactInternalAsync` implements
the read → write → manifest commit → retire pattern.

### Trap: Wrong merge order loses newer values

**Bug (prevented by design)**: During compaction, if segments are merged in the wrong order
(newest-to-oldest into a dictionary), the older segment's value overwrites the newer one for
duplicate keys, violating last-writer-wins semantics.

**Fix pattern**: Process segments oldest-to-newest when merging into a `SortedDictionary`. Since
later `dictionary[key] = value` assignments overwrite earlier ones, the newest segment's value
wins for any given key — matching the LSM read-path semantics (newest segment searched first).

**Code**: `src/Infrastructure/LsmTree/LsmStorageEngine.cs` — the current compactor uses a
k-way merge of selected oldest-to-newest streams, preserving sequence numbers, value kinds
and tombstones. It does not materialize a `SortedDictionary` as the illustrative pattern does.

**Test**: `tests/Infrastructure/LsmStorageEngineTests.cs` —
`CompactAsync_ShouldPreserveLastWriterWins` writes the same key to two different segments
and verifies the newer value survives compaction.

---

## 12. Atomic File Writes

### Trap: Writing directly to the final file path

**Bug (prevented by design)**: Writing directly to the target file means a crash mid-write leaves a
partially-written, corrupt file. The next read sees a truncated SSTable with valid header but
missing entries.

**Fix pattern**: Write and flush a temporary file, then publish it through
`DurableFile.AtomicReplace`. The final path contains the old complete file or the new complete
file. On Windows the helper uses extended rename semantics so an existing delete-sharing
reader retains the old file while new opens see the replacement. Unsupported filesystem
semantics fail explicitly; there is no delete-then-copy fallback. Atomic visibility alone
does not establish persistence across physical power loss.

**Code**: `src/Infrastructure/SortedStringTable/SortedStringTable.cs` publishes its completed
temporary file through `DurableFile.AtomicReplace`.

---

## 13. Tombstone Flush Edge Case

### Trap: Creating an empty segment when only tombstones exist

**Bug**: When the MemTable is empty but tombstones exist, flushing would either skip the tombstones
(losing delete information) or create an empty segment file (wasting disk and confusing recovery).

**Fix pattern**: Write a data-less segment carrying the tombstone keys and sequence numbers
in its footer, then commit it in the manifest. Updating only an older segment's in-memory
map would lose deletes on reopen.

**Code**: `src/Infrastructure/LsmTree/LsmStorageEngine.cs` — the background flusher writes
an empty entry list with a nonempty tombstone map through either segment format.

---

## 14. Async Startup Recovery

### Trap: Replaying the WAL synchronously during construction

**Bug**: Replaying the WAL from the LSM engine constructor required
`.GetAwaiter().GetResult()`. Blocking an arbitrary async operation during construction can
deadlock under a `SynchronizationContext` and makes cancellation impossible to propagate cleanly.

**Fix pattern**: Keep synchronous setup in the constructor and expose an async factory for the
remaining work. `LsmStorageEngine.OpenAsync(options, cancellationToken)` constructs the engine,
opens an existing value log, then awaits WAL replay. The constructor still validates options,
creates the directory, validates the `CURRENT` manifest and opens its segment set synchronously.

**Code**: `src/Infrastructure/LsmTree/LsmStorageEngine.cs` — `OpenAsync` delegates async recovery
to `InitialiseAsync`; `ReplayWalAsync` is never called through sync-over-async.

---

## 15. StreamReader Buffering and BOM Pollution in CSV Serialization

### Trap: Creating a new StreamReader per ReadEntry call on a shared stream

**Bug**: `CsvEntrySerializer.ReadEntry` created a new `StreamReader(stream, Encoding.UTF8, ..., 1024, leaveOpen: true)` per call. StreamReader's internal buffer (default 1024 bytes) read ahead from the underlying stream beyond the current entry. On `StreamReader.Dispose()`, those buffered-but-unconsumed bytes were lost — the stream position had already advanced past them, corrupting all subsequent reads.

**Why it's wrong**: The calling code expects to read entries sequentially from a single stream — write 10 entries, rewind, read them back one at a time. The first `ReadEntry` appeared to work, but every subsequent call received garbled data because the stream position had been advanced past their bytes by the first reader's buffer.

**Why it's hard to catch**: Single-entry tests pass because there's no subsequent read to corrupt. The bug only manifests when reading multiple entries sequentially from the same stream — the exact pattern used by the benchmark and by the SSTable reader.

**Fix pattern**: Read bytes directly from the stream instead of using StreamReader. CSV's special characters (comma `0x2C`, quote `0x22`, LF `0x0A`, CR `0x0D`) are all single-byte ASCII values. In UTF-8, multi-byte sequences never contain bytes in the `0x00–0x7F` range, so byte-level detection of record boundaries is safe. Accumulate raw bytes and decode the complete line to string once at the end.

**Code**: `src/Infrastructure/Serialization/CsvEntrySerializer.cs` — `ReadCsvLineFromStream(Stream stream)` replaced the old `ReadCsvLine(StreamReader reader)` method.

### Trap: UTF-8 BOM prepended to every entry by StreamWriter

**Bug**: `CsvEntrySerializer.WriteEntry` created a new `StreamWriter(stream, Encoding.UTF8, ...)` per call. `Encoding.UTF8` emits a 3-byte BOM (0xEF 0xBB 0xBF) preamble on the first write of each new StreamWriter. Since each `WriteEntry` creates a fresh StreamWriter, every entry got a BOM prepended — not just the first one.

**Why it's wrong**: When reading back, the 3-byte BOM is interpreted as data, producing corrupted key values. For example, an integer key of `1` (4 bytes: `01 00 00 00`) preceded by a BOM becomes 7 bytes (`EF BB BF 01 00 00 00`), deserializing to `29342703` instead of `1`.

**Why it's hard to catch**: The BOM is invisible in most text editors and debuggers. Hex inspection of the stream is required to see the extra bytes. The corruption appears as wrong numeric values, which is easily mistaken for a serialization logic error rather than an encoding issue.

**Fix pattern**: Use `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)` instead of `Encoding.UTF8`. This produces identical UTF-8 output without the BOM preamble. The ReadEntry path also includes backward-compatible BOM skipping for data written by the old encoder.

**Code**: `src/Infrastructure/Serialization/CsvEntrySerializer.cs:52` — `private static readonly Encoding s_utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)` used in both `WriteEntry` and `WriteEntryAsync`.

**Test**: `tests/Infrastructure/CsvEntrySerializerTests.cs` — `ReadEntry_ShouldReadMultipleEntriesSequentiallyFromSingleStream` writes 3 entries to a single stream, reads them all back, and verifies correctness. This is the exact scenario that was broken before the fix.

---

## Summary: Prevention Checklist

Before submitting storage engine code, verify:

- [ ] Hash algorithms validated against a reference implementation
- [ ] All file writes use `Flush(flushToDisk: true)` for durability-critical paths
- [ ] On-disk formats use explicit endianness (`BinaryPrimitives`)
- [ ] Recovery fails closed — stops at the first corrupt frame, then classifies torn-tail (truncate cleanly) vs mid-file corruption (throw loud)
- [ ] Sorted input is validated, not silently corrected
- [ ] Synchronous code paths call synchronous methods (no sync-over-async)
- [ ] Every public method checks `ObjectDisposedException` after dispose
- [ ] Async locks (`SemaphoreSlim`) are not re-entered — extract internal methods
- [ ] Bulk operations acquire the lock once, not per-item
- [ ] Mutable arrays returned from properties are defensive copies
- [ ] All public methods validate null/invalid inputs at the boundary
- [ ] `CancellationToken` parameters are actually checked
- [ ] Compaction writes the new segment before deleting old segments (crash-safe ordering)
- [ ] Compaction merges segments oldest-to-newest (last-writer-wins preserved)
- [ ] File writes use write-to-temp-then-rename for atomicity
- [ ] Tombstone-only flushes persist a tombstone-carrying segment and commit its manifest
- [ ] Async startup work uses an async factory; constructors do not block on Tasks
- [ ] StreamReader/StreamWriter are not created per-entry on a shared stream (use byte-level I/O)
- [ ] StreamWriter uses BOM-less UTF-8 (`new UTF8Encoding(false)`) — never `Encoding.UTF8` for per-call writers
