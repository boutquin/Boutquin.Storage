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
using Boutquin.Storage.Domain.ValueObjects.LsmTree;

namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Phase A4 — per-segment CRC32C in the <c>CURRENT</c> manifest. A bit-flip anywhere inside
/// a segment file must cause recovery to skip the segment rather than silently return the
/// corrupted data. Mirrors the DDIA 2e p. 122 prescription "checksums in the log and
/// discarding corrupted ... entries".
/// </summary>
public sealed class SegmentManifestCrcTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmCrc_{Guid.NewGuid():N}");

    public SegmentManifestCrcTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> NewEngine(int memTableCapacity = 3)
    {
        return LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: memTableCapacity,
            segmentFolder: _dir,
            segmentPrefix: "seg");
    }

    private static SerializableWrapper<int> K(int i) => new(i);
    private static SerializableWrapper<string> V(string s) => new(s);

    [Fact]
    public async Task FlushWritesManifestWith_NonZero_Crc_And_Size()
    {
        using (var engine = NewEngine(memTableCapacity: 2))
        {
            await engine.SetAsync(K(1), V("alpha"));
            await engine.SetAsync(K(2), V("beta"));
            await engine.SetAsync(K(3), V("gamma"));
            await engine.FlushAsync(); // drain rotation queue + seal active so manifest is written
        }

        var manifestPath = Path.Combine(_dir, "CURRENT");
        File.Exists(manifestPath).Should().BeTrue();

        var line = (await File.ReadAllLinesAsync(manifestPath))[0];
        var cols = line.Split('\t');
        cols.Length.Should().Be(4,
            "B3 manifest row format is {filename}\\t{crc32c_hex}\\t{size_bytes}\\t{level}");

        cols[0].Should().StartWith("seg_").And.EndWith(".sst");

        uint.TryParse(cols[1], System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var crc).Should().BeTrue();
        crc.Should().NotBe(0u, "flushed segment must have a computed CRC");

        long.TryParse(cols[2], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var size).Should().BeTrue();
        size.Should().BeGreaterThan(0);

        int.TryParse(cols[3], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var level).Should().BeTrue();
        level.Should().Be(0, "freshly flushed segments always land at L0");
    }

    [Fact]
    public async Task BitFlip_InSegmentFile_CausesRecoveryToFailClosed()
    {
        // Arrange: two flushes → two segments. Flip a byte in the first; recovery must
        // detect the CRC mismatch and fail the open. The corrupted segment holds
        // acknowledged data — silently loading "everything except it" would serve a state
        // with those keys absent (and older values/deletes resurrected), signalled only by
        // a Trace warning.
        using (var engine = NewEngine(memTableCapacity: 2))
        {
            await engine.SetAsync(K(1), V("alive"));
            await engine.SetAsync(K(2), V("bystander"));
            await engine.SetAsync(K(3), V("third"));
            await engine.SetAsync(K(4), V("d"));
            await engine.SetAsync(K(5), V("e"));
            await engine.SetAsync(K(6), V("f"));
            await engine.FlushAsync(); // drain rotation queue → segments durable on disk
        }

        var segFiles = Directory.GetFiles(_dir, "seg_*.sst").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        segFiles.Length.Should().BeGreaterThanOrEqualTo(2);
        var firstSegment = segFiles[0];

        // Flip a byte somewhere in the interior of the first segment.
        var bytes = await File.ReadAllBytesAsync(firstSegment);
        bytes.Length.Should().BeGreaterThan(10);
        bytes[bytes.Length / 2] ^= 0xAA;
        await File.WriteAllBytesAsync(firstSegment, bytes);

        // Act + Assert: reopen fails loudly, naming the damaged segment.
        Action act = () => _ = NewEngine(memTableCapacity: 2);
        act.Should().Throw<InvalidDataException>(
                "a manifest-listed segment failing its CRC holds acked data the engine cannot trust")
            .WithMessage($"*{Path.GetFileName(firstSegment)}*");
    }

    [Fact]
    public async Task LegacyFilenameOnlyManifestRow_ThrowsInvalidData()
    {
        // H4 (greenfield consolidation): v2.0.0 ships a single manifest format. Rows
        // missing the tab separators raise InvalidDataException — no fallback reader
        // for legacy filename-only rows (which never shipped on disk).
        using (var engine = NewEngine(memTableCapacity: 2))
        {
            await engine.SetAsync(K(1), V("x"));
            await engine.SetAsync(K(2), V("y"));
            await engine.SetAsync(K(3), V("z"));
            await engine.FlushAsync(); // drain rotation queue → manifest written
        }

        var manifestPath = Path.Combine(_dir, "CURRENT");
        var firstLine = (await File.ReadAllLinesAsync(manifestPath))[0];
        var legacyLine = firstLine.Split('\t')[0]; // drop the CRC/size columns
        await File.WriteAllTextAsync(manifestPath, legacyLine + Environment.NewLine);

        var act = () => NewEngine(memTableCapacity: 2);
        act.Should().Throw<InvalidDataException>();
    }
    // ───── ExcludeAndSurface opt-in (cache-tier disposition) ──────────────────

    private async Task<string[]> ArrangeTwoSegmentsAsync()
    {
        using var engine = NewEngine(memTableCapacity: 2);
        await engine.SetAsync(K(1), V("alive"));
        await engine.SetAsync(K(2), V("bystander"));
        await engine.SetAsync(K(3), V("third"));
        await engine.SetAsync(K(4), V("d"));
        await engine.SetAsync(K(5), V("e"));
        await engine.SetAsync(K(6), V("f"));
        await engine.FlushAsync();
        var segFiles = Directory.GetFiles(_dir, "seg_*.sst").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        segFiles.Length.Should().BeGreaterThanOrEqualTo(2);
        return segFiles;
    }

    private Task<LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>> ReopenExcludeAndSurfaceAsync(
        List<CorruptSegmentExclusion> exclusions)
        => LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>.OpenAsync(
            new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
                MemTableCapacity: 2,
                SegmentFolder: _dir,
                SegmentPrefix: "seg",
                SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>())
            {
                CorruptSegmentRecovery = CorruptSegmentRecoveryDisposition.ExcludeAndSurface,
                OnCorruptSegmentExcluded = exclusions.Add,
            });

    [Fact]
    public async Task BitFlip_WithExcludeAndSurface_OpensAndSurfacesTheExclusion()
    {
        // The cache-tier opt-in: the store's contents are rebuildable from an upstream
        // source, so the consumer prefers "open with the damaged segment excluded and tell
        // me exactly what was lost" over a fail-stop. The exclusion carries the manifest
        // vs observed CRCs so the consumer can shrink coverage and re-ingest.
        var segFiles = await ArrangeTwoSegmentsAsync();
        var firstSegment = segFiles[0];

        var bytes = await File.ReadAllBytesAsync(firstSegment);
        bytes[bytes.Length / 2] ^= 0xAA;
        await File.WriteAllBytesAsync(firstSegment, bytes);

        var exclusions = new List<CorruptSegmentExclusion>();
        using var reopened = await ReopenExcludeAndSurfaceAsync(exclusions);

        var exclusion = exclusions.Should().ContainSingle().Which;
        exclusion.FileName.Should().Be(Path.GetFileName(firstSegment));
        exclusion.FileMissing.Should().BeFalse();
        exclusion.ObservedCrc32C.Should().NotBe(exclusion.ManifestCrc32C);

        // Keys in the intact (second) segment still serve; keys exclusively in the
        // excluded segment are absent — the accepted, surfaced trade.
        var (v6, found6) = await reopened.TryGetValueAsync(K(6));
        found6.Should().BeTrue("the intact segment must still serve reads");
        v6.Value.Should().Be("f");
        var (_, found1) = await reopened.TryGetValueAsync(K(1));
        found1.Should().BeFalse("the excluded segment's keys are gone until re-ingest");
    }

    [Fact]
    public async Task MissingSegmentFile_WithExcludeAndSurface_OpensAndSurfacesTheExclusion()
    {
        var segFiles = await ArrangeTwoSegmentsAsync();
        var firstSegment = segFiles[0];
        File.Delete(firstSegment);

        var exclusions = new List<CorruptSegmentExclusion>();
        using var reopened = await ReopenExcludeAndSurfaceAsync(exclusions);

        var exclusion = exclusions.Should().ContainSingle().Which;
        exclusion.FileName.Should().Be(Path.GetFileName(firstSegment));
        exclusion.FileMissing.Should().BeTrue();

        var (_, found6) = await reopened.TryGetValueAsync(K(6));
        found6.Should().BeTrue("the intact segment must still serve reads");
    }

    [Fact]
    public async Task BitFlip_DefaultDisposition_StillFailsClosed_EvenWithCallbackRegistered()
    {
        // The callback alone must not soften the default — only the explicit enum opt-in does.
        var segFiles = await ArrangeTwoSegmentsAsync();
        var bytes = await File.ReadAllBytesAsync(segFiles[0]);
        bytes[bytes.Length / 2] ^= 0xAA;
        await File.WriteAllBytesAsync(segFiles[0], bytes);

        var exclusions = new List<CorruptSegmentExclusion>();
        var act = () => LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>.OpenAsync(
            new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
                MemTableCapacity: 2,
                SegmentFolder: _dir,
                SegmentPrefix: "seg",
                SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>())
            {
                OnCorruptSegmentExcluded = exclusions.Add,
            });

        await act.Should().ThrowAsync<InvalidDataException>().ConfigureAwait(true);
        exclusions.Should().BeEmpty();
    }

}
