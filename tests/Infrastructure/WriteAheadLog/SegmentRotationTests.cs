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

namespace Boutquin.Storage.Infrastructure.Tests.WriteAheadLog;

/// <summary>
/// D2 — WAL segment-rotation tests for <see cref="WriteAheadLog{TKey, TValue}"/>.
/// Verifies WAL segment-rotation: writes past SegmentMaxBytes roll over into
/// <c>wal_NNNNNN.log</c> files; recovery enumerates segments in counter order;
/// truncate deletes every segment fully covered by the watermark.
/// </summary>
public sealed class SegmentRotationTests : IDisposable
{
    private readonly string _walDirectory = Path.Combine(
        Path.GetTempPath(), $"SegmentRotationTests_{Guid.NewGuid():N}");

    public SegmentRotationTests() => Directory.CreateDirectory(_walDirectory);

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

    /// <summary>
    /// First append lands in <c>wal_000001.log</c> — the segment naming pattern is locked
    /// to a six-digit counter starting at 1.
    /// </summary>
    [Fact]
    public async Task FirstAppend_LandsInWal000001()
    {
        using (var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1UL);
        }

        File.Exists(Path.Combine(_walDirectory, "wal_000001.log")).Should().BeTrue();
    }

    /// <summary>
    /// Once the active segment crosses <see cref="WriteAheadLogOptions.SegmentMaxBytes"/>, the
    /// next append rolls over into a new segment — the v2 LSM no longer accumulates a single
    /// growing file. Two segments must be present after writing past the threshold; both
    /// must contribute entries to recovery.
    /// </summary>
    [Fact]
    public async Task WritePastThreshold_RotatesIntoNextSegment()
    {
        // 1 MiB threshold (the smallest the validator allows). With ~500-byte payloads we
        // roll within ~2,500 frames. Writes are dispatched concurrently so the group-commit
        // flusher coalesces them into wide batches — fast enough for CI.
        var options = new WriteAheadLogOptions { SegmentMaxBytes = 1L * 1024L * 1024L };
        const int writeCount = 4_000;
        var bigPayload = new string('x', 500);

        using (var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory, options))
        {
            var tasks = Enumerable.Range(0, writeCount)
                .Select(i => wal.AppendAsync(
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>(bigPayload),
                    (ulong)(i + 1)))
                .ToArray();
            await Task.WhenAll(tasks);
        }

        var segments = Directory.GetFiles(_walDirectory, "wal_*.log");
        segments.Should().HaveCountGreaterThan(1, "writes past SegmentMaxBytes must rotate into wal_NNNNNN.log");

        using var reader = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory, options);
        var entries = await reader.RecoverAsync();
        entries.Should().HaveCount(writeCount, "every entry across rotated segments must be recovered");
    }

    /// <summary>
    /// Recovery enumerates segments in counter order, so the surfaced entries reflect the
    /// monotonically increasing sequence numbers across rotation boundaries.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_RestoresEntriesInCounterOrder()
    {
        var options = new WriteAheadLogOptions { SegmentMaxBytes = 1L * 1024L * 1024L };
        const int writeCount = 3_000;
        var payload = new string('y', 400);

        // Sequential await so ordering is deterministic — the test asserts ascending seqno
        // which can only hold if the writer submitted them in order. Group commit still
        // coalesces consecutive appends when they land within the latency window.
        using (var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory, options))
        {
            for (var i = 0; i < writeCount; i++)
            {
                await wal.AppendAsync(
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>(payload),
                    (ulong)(i + 1));
            }
        }

        Directory.GetFiles(_walDirectory, "wal_*.log").Length.Should().BeGreaterThan(1);

        using var reader = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory, options);
        var entries = await reader.RecoverAsync();

        entries.Select(e => e.SequenceNumber).Should()
            .BeInAscendingOrder("recovery must enumerate segments in counter order, preserving seqno monotonicity");
        entries.Select(e => e.Key.Value).Should().Equal(Enumerable.Range(0, writeCount));
    }

    /// <summary>
    /// <see cref="WriteAheadLog{TKey,TValue}.TruncateAsync"/> deletes every segment file in
    /// the directory — this is the "watermark covers every entry" case used by the LSM after
    /// a successful MemTable flush. The next append starts a fresh <c>wal_000001.log</c>.
    /// </summary>
    [Fact]
    public async Task TruncateAsync_DeletesAllSegmentsAndResetsCounter()
    {
        var options = new WriteAheadLogOptions { SegmentMaxBytes = 1L * 1024L * 1024L };
        var payload = new string('z', 400);

        using var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory, options);
        var tasks = Enumerable.Range(0, 3_000)
            .Select(i => wal.AppendAsync(
                new SerializableWrapper<int>(i),
                new SerializableWrapper<string>(payload),
                (ulong)(i + 1)))
            .ToArray();
        await Task.WhenAll(tasks);

        Directory.GetFiles(_walDirectory, "wal_*.log").Length.Should().BeGreaterThan(1);

        await wal.TruncateAsync();

        Directory.GetFiles(_walDirectory, "wal_*.log").Should().BeEmpty(
            "TruncateAsync must delete every segment fully covered by the watermark (D2)");

        // A subsequent append starts the counter back at 1.
        await wal.AppendAsync(new SerializableWrapper<int>(99), new SerializableWrapper<string>("post-truncate"), 9999UL);
        File.Exists(Path.Combine(_walDirectory, "wal_000001.log")).Should().BeTrue();
    }

    /// <summary>
    /// A re-opened WAL discovers existing segments and continues the counter from the highest
    /// existing index — recovery doesn't overwrite the partially-filled tail segment, and a
    /// post-recovery append lands in (or extends) that segment's index.
    /// </summary>
    [Fact]
    public async Task ReopenAfterRotation_ContinuesCounterFromExistingSegments()
    {
        var options = new WriteAheadLogOptions { SegmentMaxBytes = 1L * 1024L * 1024L };
        var payload = new string('q', 400);

        using (var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory, options))
        {
            var tasks = Enumerable.Range(0, 3_000)
                .Select(i => wal.AppendAsync(
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>(payload),
                    (ulong)(i + 1)))
                .ToArray();
            await Task.WhenAll(tasks);
        }

        var segmentsBefore = Directory.GetFiles(_walDirectory, "wal_*.log").Length;
        segmentsBefore.Should().BeGreaterThan(1);

        // Re-open the WAL and append one more entry — discovery must reuse the highest
        // existing segment instead of overwriting wal_000001.log.
        using (var reopened = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory, options))
        {
            await reopened.AppendAsync(
                new SerializableWrapper<int>(9999),
                new SerializableWrapper<string>("post-reopen"),
                10_000UL);
        }

        // Segment count is the same (or one more if the post-reopen append crossed the
        // threshold — payload is small, so it should not).
        Directory.GetFiles(_walDirectory, "wal_*.log").Length.Should().Be(segmentsBefore);

        using var reader = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory, options);
        var entries = await reader.RecoverAsync();
        entries.Should().HaveCount(3_001);
        entries[^1].Key.Value.Should().Be(9999);
    }

    /// <summary>
    /// <see cref="WriteAheadLog{TKey,TValue}.GetCurrentSegmentPath"/> exposes the next-write
    /// target so consumers (and tests) can correlate segments without reaching into the
    /// directory listing.
    /// </summary>
    [Fact]
    public async Task GetCurrentSegmentPath_TracksRotationBoundary()
    {
        var options = new WriteAheadLogOptions { SegmentMaxBytes = 1L * 1024L * 1024L };
        var payload = new string('s', 400);

        using var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory, options);

        wal.GetCurrentSegmentPath().Should().EndWith("wal_000001.log");

        var tasks = Enumerable.Range(0, 3_000)
            .Select(i => wal.AppendAsync(
                new SerializableWrapper<int>(i),
                new SerializableWrapper<string>(payload),
                (ulong)(i + 1)))
            .ToArray();
        await Task.WhenAll(tasks);

        // After enough writes, GetCurrentSegmentPath should report a higher counter.
        var current = wal.GetCurrentSegmentPath();
        current.Should().NotEndWith("wal_000001.log",
            "the segment counter advances every time the active segment crosses SegmentMaxBytes");
    }

    // ── Recovery & discovery edge cases ──────────────────────────────────────

    [Fact]
    public async Task RecoverAsync_OnEmptyDirectory_ReturnsNoEntries()
    {
        using var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(
            _walDirectory,
            new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLogOptions { SegmentMaxBytes = 1L * 1024L * 1024L });

        var entries = await wal.RecoverAsync().ConfigureAwait(true);
        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task RecoverAsync_IgnoresFilesWithNonMatchingExtension()
    {
        // Files outside the SegmentFileGlob (wal_*.log) must be ignored entirely.
        File.WriteAllText(Path.Combine(_walDirectory, "not-a-wal.txt"), "garbage");
        File.WriteAllText(Path.Combine(_walDirectory, "wal_index.json"), "{}");

        using var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(
            _walDirectory,
            new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLogOptions { SegmentMaxBytes = 1L * 1024L * 1024L });

        await wal.AppendAsync(
            new SerializableWrapper<int>(1),
            new SerializableWrapper<string>("v"),
            sequenceNumber: 1ul);

        var entries = await wal.RecoverAsync().ConfigureAwait(true);
        entries.Should().HaveCount(1);
        entries[0].Key.Value.Should().Be(1);
    }

    [Fact]
    public async Task TruncateAsync_OnEmptyDirectory_DoesNotThrow()
    {
        using var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(
            _walDirectory,
            new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLogOptions { SegmentMaxBytes = 1L * 1024L * 1024L });

        Func<Task> act = async () => await wal.TruncateAsync().ConfigureAwait(true);
        await act.Should().NotThrowAsync();
    }
}
