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

using WalType = Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>>;

namespace Boutquin.Storage.Infrastructure.Tests.WriteAheadLog;

/// <summary>
/// C5-1 — Standalone-append torn-tail repair.
/// Verifies that when a WAL segment has a torn tail (length prefix present but payload
/// truncated), a subsequent append (without calling RecoverAsync first) triggers the
/// torn-tail repair, and a subsequent RecoverAsync produces 0 entries (the torn entry
/// is discarded, not surfaced as a corrupt frame after the fresh append).
/// </summary>
public sealed class StandaloneAppendTornTailRepairTests : IDisposable
{
    private readonly string _walDirectory = Path.Combine(
        Path.GetTempPath(), $"StandaloneAppendTornTailRepairTests_{Guid.NewGuid():N}");

    public StandaloneAppendTornTailRepairTests() => Directory.CreateDirectory(_walDirectory);

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
    /// Appends one entry, then physically truncates the WAL file to remove the last N bytes of
    /// the frame payload (but leaves the 4-byte length prefix intact). A fresh WAL instance then
    /// performs a standalone append (without RecoverAsync). Recovery on a third instance must
    /// yield exactly 0 entries — the torn entry is discarded, and the fresh append survives.
    /// </summary>
    [Fact]
    public async Task StandaloneAppend_AfterTornTail_RepairsAndRecoverYieldsOnlyFreshEntry()
    {
        // ── Step 1: write one entry and close cleanly ─────────────────────────────────────
        using (var wal = new WalType(_walDirectory))
        {
            await wal.AppendAsync(
                new SerializableWrapper<int>(42),
                new SerializableWrapper<string>("original"),
                1ul).ConfigureAwait(true);
        }

        // ── Step 2: corrupt the segment by truncating the tail ────────────────────────────
        // Frame layout from offset 0:
        //   [0..7]   file header (magic + format_version=1)     = 8 bytes
        //   [8..11]  payload-length LE int32                     = 4 bytes
        //   [12]     EntryKind byte                              = 1 byte
        //   [13]     ValueKind byte                              = 1 byte
        //   [14..21] SequenceNumber LE u64                       = 8 bytes
        //   [22..]   payload bytes (key + value serialized)
        //   [end-4.] CRC32C LE u32                               = 4 bytes
        //
        // We keep the 4-byte length prefix but remove the last N bytes of the payload so the
        // frame is definitely incomplete (the structual "position + framingTail > Length" check
        // fires, returning null — the "truncated tail" path).
        var segmentPath = Path.Combine(_walDirectory, "wal_000001.log");
        var originalBytes = await File.ReadAllBytesAsync(segmentPath).ConfigureAwait(true);

        // Remove last 10 bytes (well into the payload / CRC region, past the length prefix).
        var truncatedLength = originalBytes.Length - 10;
        truncatedLength.Should().BeGreaterThan(8 + 4,
            "truncation must leave the 8-byte file header and 4-byte length prefix intact");

        using (var fs = new FileStream(segmentPath, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(truncatedLength);
        }

        // ── Step 3: standalone append (no RecoverAsync) on the damaged segment ───────────
        // The WAL opens the pre-existing segment, detects the torn tail via RepairTornTail,
        // truncates to the last clean frame boundary (= the header, since no full frame exists),
        // then writes the new entry cleanly.
        using (var wal = new WalType(_walDirectory))
        {
            // This must complete without exception — torn-tail repair runs before the append.
            var appendTask = wal.AppendAsync(
                new SerializableWrapper<int>(99),
                new SerializableWrapper<string>("fresh"),
                2ul);

            await appendTask.ConfigureAwait(true);
        }

        // ── Step 4: recover and assert ────────────────────────────────────────────────────
        // The torn entry was discarded by the repair; recovery returns only the fresh entry.
        using var reader = new WalType(_walDirectory);
        var entries = await reader.RecoverAsync().ConfigureAwait(true);

        entries.Should().HaveCount(1,
            "torn entry is discarded by the repair; only the fresh append survives recovery");
        entries[0].Key.Value.Should().Be(99,
            "the surviving entry is the one appended after the torn-tail repair");
    }

    /// <summary>
    /// When the segment has a torn tail and no standalone append is made, RecoverAsync itself
    /// repairs the torn tail and returns 0 entries (the torn frame is not surfaced).
    /// This is the symmetric test — RecoverAsync's own repair path vs the standalone-append path.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_AfterTornTail_ReturnsZeroEntries()
    {
        // Write one entry then corrupt it.
        using (var wal = new WalType(_walDirectory))
        {
            await wal.AppendAsync(
                new SerializableWrapper<int>(7),
                new SerializableWrapper<string>("will-be-torn"),
                1ul).ConfigureAwait(true);
        }

        var segmentPath = Path.Combine(_walDirectory, "wal_000001.log");
        var bytes = await File.ReadAllBytesAsync(segmentPath).ConfigureAwait(true);

        // Truncate 10 bytes from the end — tears the frame payload, leaving the length prefix.
        using (var fs = new FileStream(segmentPath, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(bytes.Length - 10);
        }

        // Recovery must complete without exception AND return 0 entries.
        using var reader = new WalType(_walDirectory);
        var act = async () => await reader.RecoverAsync().ConfigureAwait(true);

        var entries = await act().ConfigureAwait(true);

        entries.Should().BeEmpty("a torn tail (partial frame after the length prefix) is discarded — not surfaced as an entry");
    }
}
