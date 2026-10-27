// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System.Collections.Concurrent;

using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.ValueLog;

using Microsoft.Win32.SafeHandles;

namespace Boutquin.Storage.Infrastructure.Tests.ValueLog;

public sealed class ValueLogReaderTests : IAsyncDisposable
{
    private readonly string _dir;
    private ValueLogManifest? _manifest;
    private ValueLogWriter? _writer;
    private ValueLogReader? _reader;

    public ValueLogReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vlr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public async ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        if (_writer is not null)
        {
            await _writer.DisposeAsync();
        }
        if (_manifest is not null)
        {
            await _manifest.DisposeAsync();
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task InitAsync(ValueLogOptions? options = null)
    {
        _manifest = new ValueLogManifest(Path.Combine(_dir, "valuelog.manifest"));
        _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, options ?? ValueLogOptions.Default);
        _reader = new ValueLogReader(_dir, options ?? ValueLogOptions.Default);
    }

    [Fact]
    public async Task Read_RoundTripsAppendedPayload()
    {
        await InitAsync();

        var payload = Encoding.UTF8.GetBytes("the quick brown fox");
        var pointer = await _writer!.AppendAsync(payload, 10ul);

        var readBack = _reader!.Read(pointer);

        readBack.Should().Equal(payload);
    }

    [Fact]
    public async Task Read_MultiplePayloads_CorrectByOffset()
    {
        await InitAsync();

        var p1 = await _writer!.AppendAsync(new byte[] { 1, 2, 3 }, 1);
        var p2 = await _writer!.AppendAsync(new byte[] { 4, 5, 6, 7 }, 2);
        var p3 = await _writer!.AppendAsync(new byte[] { 8 }, 3);

        _reader!.Read(p1).Should().Equal([1, 2, 3]);
        _reader!.Read(p2).Should().Equal([4, 5, 6, 7]);
        _reader!.Read(p3).Should().Equal([8]);
    }

    [Fact]
    public async Task Read_AddressReusedByLaterWrite_ThrowsInsteadOfServingOtherValue()
    {
        await InitAsync();
        var lost = await _writer!.AppendAsync(new byte[] { 0xB1, 0xB2, 0xB3 }, 7);
        await _writer.DisposeAsync();
        _writer = null;
        await _manifest!.DisposeAsync();

        // Lose the frame (any truncation of an acknowledged frame), then let a later write
        // land at the very same (file, offset) address.
        var path = ValueLogWriter.ResolveSegmentPath(_dir, lost.FileIndex);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength((long)lost.Offset);
        }
        _manifest = new ValueLogManifest(Path.Combine(_dir, "valuelog.manifest"));
        _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default);
        var reused = await _writer.AppendAsync(new byte[] { 0xCC, 0xCD }, 8);
        reused.Offset.Should().Be(lost.Offset, "the scenario needs the address to be reused");

        Action act = () => _reader!.Read(lost);

        act.Should().Throw<CorruptValueLogPointerException>(
            "a stored value whose identity differs from the pointer's is treated as lost, never served");
        _reader!.Read(reused).Should().Equal([0xCC, 0xCD]);
    }

    [Fact]
    public async Task Read_ThrowsOnCrcMismatch()
    {
        await InitAsync();

        var pointer = await _writer!.AppendAsync(new byte[] { 1, 2, 3 }, 1);

        // Flip a byte inside the payload so the frame CRC mismatches on read.
        var path = ValueLogWriter.ResolveSegmentPath(_dir, pointer.FileIndex);
        await _writer!.DisposeAsync();
        _writer = null;

        using (var rw = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            rw.Seek((long)pointer.Offset + ValueLogFrame.EntryHeaderSize, SeekOrigin.Begin);
            rw.WriteByte(0xAA);
        }

        Action act = () => _reader!.Read(pointer);

        act.Should().Throw<CorruptValueLogPointerException>()
            .Which.FileIndex.Should().Be(pointer.FileIndex);
    }

    [Fact]
    public async Task Read_ThrowsWhenFileMissing()
    {
        await InitAsync();

        var pointer = new ValueLogPointer(FileIndex: 999, Offset: 0);

        Action act = () => _reader!.Read(pointer);

        act.Should().Throw<MissingValueLogFileException>();
    }

    [Fact]
    public async Task Read_CorruptPointerOffsetNearEof_ThrowsTypedCorruption()
    {
        // F-V3-01 — a corrupt/forged pointer whose Offset lands within the final 17 bytes of the
        // segment (so the 17-byte frame-header read short-reads) must surface a TYPED
        // CorruptValueLogPointerException, not a raw EndOfStreamException leaked from the unbounded
        // header read at the top of Read.
        await InitAsync();

        var pointer = await _writer!.AppendAsync(new byte[] { 1, 2, 3 }, 1);
        var path = ValueLogWriter.ResolveSegmentPath(_dir, pointer.FileIndex);
        await _writer!.DisposeAsync();
        _writer = null;

        var fileLength = new FileInfo(path).Length;

        // Offset = fileLength - 5: the header ReadFully requests 17 bytes but only 5 remain.
        var corrupt = new ValueLogPointer(pointer.FileIndex, (ulong)(fileLength - 5));

        Action act = () => _reader!.Read(corrupt);

        act.Should().Throw<CorruptValueLogPointerException>(
            "a pointer offset that runs the frame-header read past EOF must surface a typed corruption exception")
            .Which.FileIndex.Should().Be(pointer.FileIndex);
    }

    [Fact]
    public async Task Read_CorruptPointerOffsetAtOrPastEof_ThrowsTypedCorruption()
    {
        // F-V3-01 — Offset exactly at (and past) EOF: the header read gets zero bytes.
        await InitAsync();

        var pointer = await _writer!.AppendAsync(new byte[] { 1, 2, 3 }, 1);
        var path = ValueLogWriter.ResolveSegmentPath(_dir, pointer.FileIndex);
        await _writer!.DisposeAsync();
        _writer = null;

        var fileLength = new FileInfo(path).Length;

        Action atEof = () => _reader!.Read(new ValueLogPointer(pointer.FileIndex, (ulong)fileLength));
        atEof.Should().Throw<CorruptValueLogPointerException>();

        Action pastEof = () => _reader!.Read(new ValueLogPointer(pointer.FileIndex, (ulong)(fileLength + 1000)));
        pastEof.Should().Throw<CorruptValueLogPointerException>();
    }

    [Fact]
    public async Task Read_AcrossRotatedSegments()
    {
        await InitAsync(new ValueLogOptions(SegmentMaxBytes: 300));

        var p1 = await _writer!.AppendAsync(new byte[200], 1);
        var p2 = await _writer!.AppendAsync(new byte[200], 2);

        p1.FileIndex.Should().Be(0u);
        p2.FileIndex.Should().Be(1u);

        _reader!.Read(p1).Should().HaveCount(200);
        _reader!.Read(p2).Should().HaveCount(200);
    }

    // ───── P3.7.1 CrcValidationMode ──────────────────────────────────────

    [Fact]
    public async Task Read_CrcValidationMode_Never_DoesNotThrowOnCorruptPayload()
    {
        await InitAsync(new ValueLogOptions(ValidateCrcOnRead: CrcValidationMode.Never));

        var pointer = await _writer!.AppendAsync(new byte[] { 1, 2, 3 }, 1);
        var path = ValueLogWriter.ResolveSegmentPath(_dir, pointer.FileIndex);
        await _writer!.DisposeAsync();
        _writer = null;

        using (var rw = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            rw.Seek((long)pointer.Offset + ValueLogFrame.EntryHeaderSize, SeekOrigin.Begin);
            rw.WriteByte(0xAA);
        }

        // With CrcValidationMode.Never the reader skips CRC checks — no exception.
        var result = _reader!.Read(pointer);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Read_CrcValidationMode_Always_ThrowsOnCorruptPayload()
    {
        await InitAsync(new ValueLogOptions(ValidateCrcOnRead: CrcValidationMode.Always));

        var pointer = await _writer!.AppendAsync(new byte[] { 1, 2, 3 }, 1);
        var path = ValueLogWriter.ResolveSegmentPath(_dir, pointer.FileIndex);
        await _writer!.DisposeAsync();
        _writer = null;

        using (var rw = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            rw.Seek((long)pointer.Offset + ValueLogFrame.EntryHeaderSize, SeekOrigin.Begin);
            rw.WriteByte(0xAA);
        }

        Action act = () => _reader!.Read(pointer);
        act.Should().Throw<CorruptValueLogPointerException>();
    }

    // ───── P3.7.2 LRU handle cache split (lock-free hot path) ────────────────

    [Fact]
    public async Task Read_LruEviction_WorksWhenCacheExceedsCapacity()
    {
        // Cache size = 2, but we create 4 segments — eviction must fire without errors.
        await InitAsync(new ValueLogOptions(SegmentMaxBytes: 300, ReadHandleCacheSize: 2));

        var p1 = await _writer!.AppendAsync(new byte[200], 1);
        var p2 = await _writer!.AppendAsync(new byte[200], 2);
        var p3 = await _writer!.AppendAsync(new byte[200], 3);
        var p4 = await _writer!.AppendAsync(new byte[200], 4);

        p1.FileIndex.Should().Be(0u);
        p2.FileIndex.Should().Be(1u);
        p3.FileIndex.Should().Be(2u);
        p4.FileIndex.Should().Be(3u);

        // Read from all 4 segments — the reader must evict LRU entries and still return
        // the correct payload for each pointer.
        _reader!.Read(p1).Should().HaveCount(200);
        _reader!.Read(p2).Should().HaveCount(200);
        _reader!.Read(p3).Should().HaveCount(200);
        _reader!.Read(p4).Should().HaveCount(200);
        _reader!.Read(p1).Should().HaveCount(200); // re-read evicted segment
    }

    [Fact]
    public async Task Read_ConcurrentReads_DoNotThrowOrDeadlock()
    {
        await InitAsync();

        const int entryCount = 20;
        var pointers = new ValueLogPointer[entryCount];
        for (var i = 0; i < entryCount; i++)
        {
            pointers[i] = await _writer!.AppendAsync(new byte[16], (ulong)i + 1);
        }

        // Fan out reads from multiple threads; any exception or deadlock fails the test.
        var tasks = Enumerable.Range(0, 40).Select(i => Task.Run(() =>
        {
            var ptr = pointers[i % entryCount];
            _reader!.Read(ptr).Should().HaveCount(16);
        }));

        Func<Task> act = () => Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Use-after-dispose race: the hot read path resolves a <see cref="SafeFileHandle"/> from
    /// the lock-free cache and then issues <see cref="System.IO.RandomAccess"/> reads against
    /// it OUTSIDE any lock, while a concurrent cold-path miss evicts (disposes) that very handle
    /// under <c>_openLock</c>. With a cache size below the number of distinct segments touched
    /// concurrently, eviction and read race continuously; nothing pins the in-flight handle, so a
    /// read can land on a disposed handle and throw <see cref="ObjectDisposedException"/>.
    /// The fix pins the handle (SafeHandle ref-count) across the whole read window, deferring the
    /// actual close until the read drains. This stress loop reliably reproduces the throw before
    /// the fix and must run clean after it.
    /// </summary>
    [Fact]
    public async Task Read_ConcurrentReadsAcrossMoreSegmentsThanCache_DoNotHitDisposedHandle()
    {
        // Cache size 1 with many segments → every read on a different segment than the last
        // forces an eviction. Two threads racing across N>cacheSize files keep the evict/read
        // windows overlapping.
        await InitAsync(new ValueLogOptions(SegmentMaxBytes: 300, ReadHandleCacheSize: 1));

        const int segments = 8;
        var pointers = new ValueLogPointer[segments];
        for (var i = 0; i < segments; i++)
        {
            // Each ~200-byte payload rolls a fresh 300-byte-capped segment, so every pointer
            // lives in a distinct value-log file.
            pointers[i] = await _writer!.AppendAsync(new byte[200], (ulong)i + 1);
        }
        for (var i = 0; i < segments; i++)
        {
            pointers[i].FileIndex.Should().Be((uint)i);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var exceptions = new ConcurrentBag<Exception>();

        void Hammer()
        {
            var rng = new Random(Environment.CurrentManagedThreadId);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var ptr = pointers[rng.Next(segments)];
                    _reader!.Read(ptr).Should().HaveCount(200);
                }
                catch (OperationCanceledException)
                {
                    // disposal/cancel race at shutdown — not the bug under test.
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                    return;
                }
            }
        }

        var threads = Enumerable.Range(0, 4).Select(_ =>
        {
            var t = new Thread(Hammer) { IsBackground = true };
            t.Start();
            return t;
        }).ToArray();

        await Task.Run(() =>
        {
            foreach (var t in threads)
            {
                t.Join();
            }
        });

        exceptions.Should().BeEmpty(
            "an in-flight read must pin its handle so a concurrent eviction cannot dispose it mid-read");
    }

    // ───── F-WA2-01 short-read contract ──────────────────────────────────────

    /// <summary>
    /// <see cref="System.IO.RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/> is
    /// contractually allowed to return FEWER bytes than requested even when every byte is
    /// durably present (a "short read" — the count is the load-bearing return value, which is
    /// why the BCL signature returns it). A read site that issues a single
    /// <c>RandomAccess.Read</c> and discards the count leaves the tail of its destination buffer
    /// unfilled. For the value-log payload read this is a correctness defect, not a recoverable
    /// one: the unfilled zero tail makes the recomputed CRC mismatch the recorded CRC, so a
    /// perfectly intact, durably-acknowledged value throws
    /// <see cref="CorruptValueLogPointerException"/> on read.
    /// <para>
    /// A genuine short read cannot be forced deterministically on a regular file on every
    /// platform (macOS does not cap a single in-bounds read; some Linux filesystems and pipes
    /// do). To pin the contract deterministically the read-fully helper accepts the per-call
    /// reader as a delegate; this test injects a stub that serves at most a few bytes per call —
    /// exactly the worst case the contract permits — and asserts the helper loops until the
    /// whole buffer is filled. Against a single-shot read the buffer keeps a zero tail and the
    /// assertion fails.
    /// </para>
    /// </summary>
    [Fact]
    public void ReadFully_ShortReadingSource_FillsEntireBuffer()
    {
        // A source that always serves at most 3 bytes per call — the short-read worst case.
        const int chunkCap = 3;
        var backing = new byte[40];
        for (var i = 0; i < backing.Length; i++)
        {
            backing[i] = (byte)(i + 1);
        }

        ValueLogReader.RandomReadFunc shortReader = (SafeFileHandle _, Span<byte> dest, long fileOffset) =>
        {
            var available = backing.Length - (int)fileOffset;
            var n = Math.Min(chunkCap, Math.Min(dest.Length, available));
            backing.AsSpan((int)fileOffset, n).CopyTo(dest);
            return n;
        };

        var buffer = new byte[backing.Length];

        ValueLogReader.ReadFully(handle: null!, buffer, fileOffset: 0, read: shortReader);

        // Single-shot read would leave bytes [3..39] zero; the loop must fill all 40.
        buffer.Should().Equal(backing,
            "ReadFully must loop RandomAccess.Read until the requested span is fully filled, "
            + "because RandomAccess.Read may return fewer bytes than requested");
    }

    /// <summary>
    /// A source that reports 0 bytes before the buffer is full is a genuine truncation (EOF on a
    /// file the caller already bounds-checked as long enough) — the read-fully helper must throw
    /// rather than spin forever or silently return a partially-filled buffer.
    /// </summary>
    [Fact]
    public void ReadFully_PrematureEof_Throws()
    {
        ValueLogReader.RandomReadFunc eofReader = (SafeFileHandle _, Span<byte> _, long _) => 0;

        var buffer = new byte[8];

        Action act = () => ValueLogReader.ReadFully(handle: null!, buffer, fileOffset: 0, read: eofReader);

        act.Should().Throw<EndOfStreamException>();
    }
}
