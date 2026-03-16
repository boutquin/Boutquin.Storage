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
This is the .NET equivalent of POSIX `fsync()`.

**Code**: `src/Infrastructure/WriteAheadLog/WriteAheadLog.cs:138` — `_appendStream.Flush(flushToDisk: true)`
after every append. Also at line 266 for truncate.

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

**Bug**: The WriteAheadLog used `BinaryWriter.Write(int)` for length prefixes and checksums. `BinaryWriter`
uses the host's native byte order, which means files written on a little-endian machine
are unreadable on a big-endian machine (and vice versa).

**Why it's wrong**: On-disk formats must have a defined byte order to be portable. Most modern
formats use little-endian (x86/ARM default), but this must be explicit, not implicit.

**Fix pattern**: Use `System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian()` and
`ReadInt32LittleEndian()` to explicitly specify byte order. Write into `Span<byte>` buffers
rather than using `BinaryWriter`.

**Code**: `src/Infrastructure/WriteAheadLog/WriteAheadLog.cs` — all length and checksum fields use
`BinaryPrimitives` with explicit little-endian encoding.

---

## 4. Corruption Recovery Semantics

### Trap: Stopping recovery at the first corrupt entry

**Bug**: When the WriteAheadLog encountered a checksum mismatch during recovery, it broke out of the
recovery loop, discarding all subsequent entries — even if they were perfectly valid.

**Why it's wrong**: The length-prefix framing format (`[4-byte length][payload][4-byte CRC32]`)
allows the reader to skip a corrupt entry by consuming its declared payload bytes and advancing
to the next entry. Stopping at the first corruption loses valid data unnecessarily.

**Fix pattern**: On checksum mismatch, `continue` to the next entry instead of `break`.
Only `break` on structural corruption (truncated length prefix, impossibly large payload)
where the framing itself is lost.

**Code**: `src/Infrastructure/WriteAheadLog/WriteAheadLog.cs` — `continue` for checksum mismatch,
`break` for structural truncation.

**Test**: `tests/Infrastructure/WriteAheadLogTests.cs` —
`RecoverAsync_ShouldSkipCorruptEntryAndRecoverSubsequentValidEntries` writes 3 entries,
corrupts entry 2's payload, and verifies entries 1 and 3 are recovered.

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
- `src/Infrastructure/LsmTree/LsmStorageEngine.cs` — `ObjectDisposedException.ThrowIf` on all
  8 public methods.
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

**Code**: `src/Infrastructure/DataStructures/MerkleTree.cs` — `RootHash` returns
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

**Code**: `src/Infrastructure/DataStructures/MerkleTree.cs` — validates `leafIndex` in `VerifyProof`.

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
3. Only after the new segment is fully written, delete old segments

**Code**: `src/Infrastructure/LsmTree/LsmStorageEngine.cs` — `CompactInternalAsync` implements
the three-phase pattern: read → write → delete.

### Trap: Wrong merge order loses newer values

**Bug (prevented by design)**: During compaction, if segments are merged in the wrong order
(newest-to-oldest into a dictionary), the older segment's value overwrites the newer one for
duplicate keys, violating last-writer-wins semantics.

**Fix pattern**: Process segments oldest-to-newest when merging into a `SortedDictionary`. Since
later `dictionary[key] = value` assignments overwrite earlier ones, the newest segment's value
wins for any given key — matching the LSM read-path semantics (newest segment searched first).

**Code**: `src/Infrastructure/LsmTree/LsmStorageEngine.cs` — `CompactInternalAsync` iterates
`_segments` in list order (oldest first, since segments are added chronologically).

**Test**: `tests/Infrastructure/LsmStorageEngineTests.cs` —
`CompactAsync_ShouldPreserveLastWriterWins` writes the same key to two different segments
and verifies the newer value survives compaction.

---

## 12. Atomic File Writes

### Trap: Writing directly to the final file path

**Bug (prevented by design)**: Writing directly to the target file means a crash mid-write leaves a
partially-written, corrupt file. The next read sees a truncated SSTable with valid header but
missing entries.

**Fix pattern**: Write to a temporary file (`.tmp`), then atomically rename via
`File.Move(tempPath, finalPath, overwrite: true)`. This leverages the filesystem's atomic rename
guarantee — the final path either contains the old complete file or the new complete file, never
a partial write.

**Code**: `src/Infrastructure/SortedStringTable/SortedStringTable.cs` — `Write()` serializes to
`MemoryStream`, writes to `.tmp` file, then `File.Move` with overwrite.

---

## 13. Tombstone Flush Edge Case

### Trap: Creating an empty segment when only tombstones exist

**Bug**: When the MemTable is empty but tombstones exist, flushing would either skip the tombstones
(losing delete information) or create an empty segment file (wasting disk and confusing recovery).

**Fix pattern**: When only tombstones exist (no data), attach the tombstones to the newest existing
segment's tombstone set rather than creating a new empty segment.

**Code**: `src/Infrastructure/LsmTree/LsmStorageEngine.cs` — `FlushInternalAsync` handles the
`itemList.Count == 0 && _memTableTombstones.Count > 0` case by appending to `_segmentTombstones[^1]`.

---

## 14. WriteAheadLog Replay in Constructor (Sync-Over-Async)

### Trap: Calling async methods synchronously during construction

**Bug**: The LSM engine constructor needs to replay the WriteAheadLog, but constructors cannot be async.
Using `.GetAwaiter().GetResult()` on an arbitrary async method risks deadlock if a
`SynchronizationContext` is present.

**Why it's acceptable here**: At construction time, the engine's `SemaphoreSlim` is not yet
contended (no other callers exist), and the WriteAheadLog's internal semaphore is similarly uncontested.
This is documented as a one-time startup operation.

**Code**: `src/Infrastructure/LsmTree/LsmStorageEngine.cs` — `ReplayWal()` uses
`.GetAwaiter().GetResult()` with a comment explaining why it's safe.

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
- [ ] Recovery skips corrupt entries without losing subsequent valid entries
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
- [ ] Tombstone-only flushes attach to existing segments (don't create empty segments)
- [ ] Sync-over-async in constructors is documented and justified (uncontested locks only)
- [ ] StreamReader/StreamWriter are not created per-entry on a shared stream (use byte-level I/O)
- [ ] StreamWriter uses BOM-less UTF-8 (`new UTF8Encoding(false)`) — never `Encoding.UTF8` for per-call writers
