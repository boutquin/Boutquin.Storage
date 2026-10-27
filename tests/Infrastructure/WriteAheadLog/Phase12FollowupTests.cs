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
#nullable disable

using System.Buffers.Binary;
using WalType = Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>>;

namespace Boutquin.Storage.Infrastructure.Tests.WriteAheadLog;

/// <summary>
/// Phase-12 follow-up — targets the WAL coverage gap left after the Batch A push:
/// sync <c>ValidateHeader</c> arms exercised on reopen (bad-magic, bad-version, too-short)
/// surfaced via group-commit's wrapper exception, <c>ReadAndValidateHeaderAsync</c>
/// too-short branch on a tiny segment, pre-cancelled <see cref="CancellationToken"/> on
/// <see cref="WalType.AppendAsync"/>, and the directory-missing arm of
/// <c>DiscoverSegmentFiles</c>. Each test pins a single failure mode the original Batch A
/// coverage push did not reach.
/// </summary>
public sealed class Phase12FollowupTests : IDisposable
{
    private readonly string _walDirectory = Path.Combine(
        Path.GetTempPath(), $"Phase12FollowupTests_{Guid.NewGuid():N}");

    public Phase12FollowupTests() => Directory.CreateDirectory(_walDirectory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_walDirectory))
            {
                Directory.Delete(_walDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    // ── 1. Sync ValidateHeader — too-short on reopen ────────────────────────

    /// <summary>
    /// When <c>EnsureSegmentReady</c> opens a pre-existing segment file shorter than the 8-byte
    /// WAL header whose bytes are a prefix of the header ("BS"), the file is a crash inside
    /// segment creation and holds no frame: <c>ValidateOrInitHeader</c> re-initialises it and the
    /// append succeeds. Distinct from the async recovery path (<c>ReadAndValidateHeaderAsync</c>)
    /// covered by other tests.
    /// </summary>
    [Fact]
    public async Task ValidateHeader_TornCreationOnReopen_ReinitialisesAndAppends()
    {
        var path = Path.Combine(_walDirectory, "wal_000001.log");
        await File.WriteAllBytesAsync(path, [0x42, 0x53]).ConfigureAwait(true);

        using (var wal = new WalType(_walDirectory))
        {
            var caught = await Record.ExceptionAsync(() => wal.AppendAsync(
                new SerializableWrapper<int>(1),
                new SerializableWrapper<string>("v"),
                1ul)).ConfigureAwait(true);
            caught.Should().BeNull("a torn segment creation holds no frame to protect");
        }

        using var reader = new WalType(_walDirectory);
        var entries = await reader.RecoverAsync().ConfigureAwait(true);
        entries.Should().ContainSingle().Which.Key.Value.Should().Be(1);
    }

    /// <summary>
    /// A pre-existing segment file shorter than the header whose bytes are NOT a header prefix
    /// is not a torn creation: the sync <c>ValidateHeader</c> length guard throws the typed
    /// <see cref="WriteAheadLogCorruptException"/> naming the segment, which group commit
    /// surfaces to the caller as-is, and the file is left as found.
    /// </summary>
    [Fact]
    public async Task ValidateHeader_TooShortForeignOnReopen_Throws()
    {
        var path = Path.Combine(_walDirectory, "wal_000001.log");
        await File.WriteAllBytesAsync(path, [0xDE, 0xAD]).ConfigureAwait(true);

        using var wal = new WalType(_walDirectory);

        Func<Task> act = async () => await wal.AppendAsync(
            new SerializableWrapper<int>(1),
            new SerializableWrapper<string>("v"),
            1ul).ConfigureAwait(true);

        var thrown = await act.Should().ThrowAsync<WriteAheadLogCorruptException>()
            .ConfigureAwait(true);
        var inner = thrown.Which;
        inner.Message.Should().Contain("too short");
        inner.SegmentPath.Should().Be(path);
        (await File.ReadAllBytesAsync(path).ConfigureAwait(true)).Should().Equal([0xDE, 0xAD]);
    }

    // ── 2. Sync ValidateHeader — bad magic on reopen ────────────────────────

    /// <summary>
    /// A pre-existing segment file long enough for the header but whose magic bytes are
    /// not "BSWL" trips the magic check in the sync <c>ValidateHeader</c> path on reopen.
    /// The typed <see cref="WriteAheadLogCorruptException"/> naming the segment surfaces to
    /// the caller as-is.
    /// </summary>
    [Fact]
    public async Task ValidateHeader_BadMagicOnReopen_Throws()
    {
        var path = Path.Combine(_walDirectory, "wal_000001.log");
        // 8 bytes — long enough to pass the length check but bytes 0..3 are not 'BSWL'.
        await File.WriteAllBytesAsync(path, [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x00, 0x00, 0x00]).ConfigureAwait(true);

        using var wal = new WalType(_walDirectory);

        Func<Task> act = async () => await wal.AppendAsync(
            new SerializableWrapper<int>(1),
            new SerializableWrapper<string>("v"),
            1ul).ConfigureAwait(true);

        var thrown = await act.Should().ThrowAsync<WriteAheadLogCorruptException>()
            .ConfigureAwait(true);
        var inner = thrown.Which;
        inner.Message.Should().Match("*BSWL*magic*");
        inner.SegmentPath.Should().Be(path);
    }

    // ── 3. Sync ValidateHeader — unknown format version on reopen ───────────

    /// <summary>
    /// A pre-existing segment file with the right magic but an unsupported format
    /// version trips the version check in the sync <c>ValidateHeader</c> path on reopen.
    /// Distinct from the async recovery path covered by
    /// <c>RecoverAsync_SegmentWithUnsupportedFormatVersion_Throws</c>; the flusher surfaces the
    /// typed <see cref="WriteAheadLogCorruptException"/> as-is.
    /// </summary>
    [Fact]
    public async Task ValidateHeader_UnknownFormatVersionOnReopen_Throws()
    {
        var path = Path.Combine(_walDirectory, "wal_000001.log");
        var header = new byte[8];
        // 'BSWL' magic
        header[0] = (byte)'B'; header[1] = (byte)'S'; header[2] = (byte)'W'; header[3] = (byte)'L';
        // version 99 (unsupported)
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 99u);
        await File.WriteAllBytesAsync(path, header).ConfigureAwait(true);

        using var wal = new WalType(_walDirectory);

        Func<Task> act = async () => await wal.AppendAsync(
            new SerializableWrapper<int>(1),
            new SerializableWrapper<string>("v"),
            1ul).ConfigureAwait(true);

        var thrown = await act.Should().ThrowAsync<WriteAheadLogCorruptException>()
            .ConfigureAwait(true);
        var inner = thrown.Which;
        inner.Message.Should().Match("*unknown format version*99*");
        inner.SegmentPath.Should().Be(path);
    }

    // ── 4. Async ReadAndValidateHeaderAsync — too-short on a tiny file ──────

    /// <summary>
    /// During recovery, a sole (therefore last) segment holding only the 4-byte "BSWL" magic is
    /// a crash inside segment creation — the header was cut before its version field and no
    /// frame was ever written. Recovery returns no entries and truncates the segment to zero
    /// bytes so the next append re-initialises it.
    /// </summary>
    [Fact]
    public async Task ReadAndValidateHeaderAsync_TornCreationFile_TruncatesToEmpty()
    {
        var path = Path.Combine(_walDirectory, "wal_000001.log");
        await File.WriteAllBytesAsync(path, [0x42, 0x53, 0x57, 0x4C]).ConfigureAwait(true);

        using var wal = new WalType(_walDirectory);

        IReadOnlyList<WalEntry<SerializableWrapper<int>, SerializableWrapper<string>>> entries = null;
        var caught = await Record.ExceptionAsync(async () => entries = await wal.RecoverAsync().ConfigureAwait(true))
            .ConfigureAwait(true);

        caught.Should().BeNull("a torn segment creation holds no acknowledged frame");
        entries.Should().BeEmpty();
        new FileInfo(path).Length.Should().Be(0, "recovery discards the torn header");
    }

    /// <summary>
    /// The async too-short branch in <c>ReadAndValidateHeaderAsync</c>: a non-empty segment
    /// shorter than the 8-byte header whose bytes are not a header prefix throws the typed
    /// <see cref="WriteAheadLogCorruptException"/> naming the segment and leaves it as found.
    /// </summary>
    [Fact]
    public async Task ReadAndValidateHeaderAsync_TooShortForeignFile_Throws()
    {
        var path = Path.Combine(_walDirectory, "wal_000001.log");
        await File.WriteAllBytesAsync(path, [0x4E, 0x4F, 0x54, 0x41]).ConfigureAwait(true);

        using var wal = new WalType(_walDirectory);

        Func<Task> act = async () => await wal.RecoverAsync().ConfigureAwait(true);

        var thrown = await act.Should().ThrowAsync<WriteAheadLogCorruptException>()
            .WithMessage("*too short*").ConfigureAwait(true);
        thrown.Which.SegmentPath.Should().Be(path);
        new FileInfo(path).Length.Should().Be(4);
    }

    // ── 5. AppendCoreAsync pre-cancelled token ──────────────────────────────

    /// <summary>
    /// <c>AppendCoreAsync</c>'s very first action after the disposed-check is
    /// <c>cancellationToken.ThrowIfCancellationRequested()</c> at L242. A pre-cancelled
    /// token must surface <see cref="OperationCanceledException"/> synchronously — no
    /// frame is built, no channel write is enqueued, no fsync is attempted.
    /// </summary>
    [Fact]
    public async Task AppendCoreAsync_PreCancelledToken_Throws()
    {
        using var wal = new WalType(_walDirectory);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await wal.AppendAsync(
            new SerializableWrapper<int>(1),
            new SerializableWrapper<string>("v"),
            1ul,
            cts.Token).ConfigureAwait(true);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);

        // No segment file should exist — the cancel fires before EnsureSegmentReady runs.
        Directory.GetFiles(_walDirectory, "wal_*.log").Should().BeEmpty(
            "ThrowIfCancellationRequested must short-circuit before any segment work");
    }

    // ── 6. DiscoverSegmentFiles — directory missing ─────────────────────────

    /// <summary>
    /// The segment-path diagnostic returns an empty list when the directory no longer exists.
    /// Release the WAL's directory lease before deleting it: Windows correctly prevents removing
    /// the live lock sidecar, so deleting an actively owned directory is not a portable fixture.
    /// </summary>
    [Fact]
    public void DiscoverSegmentFiles_DirectoryMissing_ReturnsEmpty()
    {
        using var wal = new WalType(_walDirectory);
        wal.Dispose();
        Directory.Delete(_walDirectory, recursive: true);

        var paths = wal.GetAllSegmentPaths();

        paths.Should().BeEmpty(
            "DiscoverSegmentFiles returns an empty list when the directory has been removed");
    }

}
