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
using System.Reflection;
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Infrastructure.Caching;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Two contracts about files a component did not positively identify.
/// <list type="bullet">
/// <item>LSM recovery deletes an unlisted segment file only once every <c>CURRENT</c> row has been
/// proven to name a present, CRC-matching file, and only when the unlisted name is one the engine
/// itself issues. A damaged manifest row must never turn a live segment into an "orphan".</item>
/// <item>A <see cref="SortedStringTable{TKey, TValue}"/> exposes no raw whole-file mutator: its
/// footer, bloom filter and block-cache identity are derived from the bytes it wrote, so a raw
/// rewrite would leave the instance answering from bytes no longer on disk. Delete resets that
/// derived state.</item>
/// </list>
/// </summary>
public sealed class LsmRecoveryFilenameAndRawMutatorContractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lsm-rec-fname-{Guid.NewGuid():N}");

    public LsmRecoveryFilenameAndRawMutatorContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static SerializableWrapper<int> K(int k) => new(k);
    private static SerializableWrapper<string> V(string v) => new(v);

    private async Task ArrangeFlushedSegmentsAsync(int count)
    {
        var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100, segmentFolder: _dir, segmentPrefix: "seg");
        for (var i = 0; i < count; i++)
        {
            await engine.SetAsync(K(i + 1), V($"v{i + 1}"));
            await engine.FlushAsync();
        }
        await engine.DisposeAsync();

        SstNames().Should().Equal(Enumerable.Range(0, count).Select(i => $"seg_{i:D6}.sst"));
    }

    private string[] SstNames()
        => [.. Directory.GetFiles(_dir, "*.sst").Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    /// <summary>Overwrites one byte of the manifest row naming <paramref name="rowName"/>.</summary>
    private void RotManifestRowName(string rowName, int byteOffsetInName, byte replacement)
    {
        var current = Path.Combine(_dir, "CURRENT");
        var bytes = File.ReadAllBytes(current);
        var needle = Encoding.UTF8.GetBytes(rowName + "\t");
        var at = bytes.AsSpan().IndexOf(needle);
        at.Should().BeGreaterThanOrEqualTo(0, "the manifest lists the row to damage");
        bytes[at + byteOffsetInName] = replacement;
        File.WriteAllBytes(current, bytes);
    }

    private Task<LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>> ReopenExcludeAndSurfaceAsync(
        List<CorruptSegmentExclusion> exclusions)
        => LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>.OpenAsync(
            new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
                MemTableCapacity: 100,
                SegmentFolder: _dir,
                SegmentPrefix: "seg",
                SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>())
            {
                CorruptSegmentRecovery = CorruptSegmentRecoveryDisposition.ExcludeAndSurface,
                OnCorruptSegmentExcluded = exclusions.Add,
            });

    // ── LSM recovery: a damaged CURRENT row never deletes the file it should have named ──────

    [Theory]
    [InlineData((byte)'7')]
    [InlineData((byte)0xFF)]
    public async Task MisnamedManifestRow_FailClosed_KeepsTheListedSegmentFile(byte rotted)
    {
        await ArrangeFlushedSegmentsAsync(2);
        var original = await File.ReadAllBytesAsync(Path.Combine(_dir, "seg_000000.sst"));
        RotManifestRowName("seg_000000.sst", byteOffsetInName: 9, rotted);

        var act = () => LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100, segmentFolder: _dir, segmentPrefix: "seg");

        act.Should().Throw<InvalidDataException>().WithMessage("*missing from disk*");
        SstNames().Should().Contain("seg_000000.sst",
            "the open refuses because a listed segment is missing; deleting the file the damaged row should have named destroys the only copy the operator is told to restore");
        (await File.ReadAllBytesAsync(Path.Combine(_dir, "seg_000000.sst"))).Should().Equal(original);
    }

    [Fact]
    public async Task MisnamedManifestRow_ExcludeAndSurface_SurfacesTheRowAndKeepsTheFile()
    {
        await ArrangeFlushedSegmentsAsync(2);
        RotManifestRowName("seg_000000.sst", byteOffsetInName: 9, (byte)'7');

        var exclusions = new List<CorruptSegmentExclusion>();
        using (var reopened = await ReopenExcludeAndSurfaceAsync(exclusions))
        {
            var exclusion = exclusions.Should().ContainSingle().Which;
            exclusion.FileName.Should().Be("seg_000007.sst");
            exclusion.FileMissing.Should().BeTrue();
            var (v2, found2) = await reopened.TryGetValueAsync(K(2));
            found2.Should().BeTrue();
            v2.Value.Should().Be("v2");
        }

        SstNames().Should().Contain("seg_000000.sst",
            "a manifest that names a missing file is not evidence that unlisted files are orphans");
    }

    [Fact]
    public async Task MisnamedManifestRow_ExcludeAndSurface_NewSegmentsNeverOverwriteAKeptFile()
    {
        await ArrangeFlushedSegmentsAsync(3);
        var keptPath = Path.Combine(_dir, "seg_000002.sst");
        var kept = await File.ReadAllBytesAsync(keptPath);
        // The highest-numbered row loses its counter digit, so no row still carries counter 2.
        RotManifestRowName("seg_000002.sst", byteOffsetInName: 9, 0xFF);

        var exclusions = new List<CorruptSegmentExclusion>();
        using (var reopened = await ReopenExcludeAndSurfaceAsync(exclusions))
        {
            exclusions.Should().ContainSingle();
            for (var i = 0; i < 3; i++)
            {
                await reopened.SetAsync(K(100 + i), V($"n{i}"));
                await reopened.FlushAsync();
            }
        }

        File.Exists(keptPath).Should().BeTrue(
            "a new segment that takes a kept file's name replaces it, and compaction then reclaims it as its own input");
        (await File.ReadAllBytesAsync(keptPath)).Should().Equal(kept,
            "a file recovery kept because it could not prove it an orphan must not be replaced by a new segment taking its name");
    }

    [Fact]
    public async Task OrphanSweep_DeletesOnlyEngineIssuedSegmentNames()
    {
        await ArrangeFlushedSegmentsAsync(2);
        var donor = await File.ReadAllBytesAsync(Path.Combine(_dir, "seg_000001.sst"));
        // An unlisted file under an engine-issued name is residue the sweep reclaims; a file the
        // engine never names (it only issues "{prefix}_{counter:D6}{ext}") is not its to delete.
        await File.WriteAllBytesAsync(Path.Combine(_dir, "seg_000009.sst"), donor);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "seg_backup.sst"), donor);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "seg_12.sst"), donor);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "seg_0000005.sst"), donor);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "seg_00000000.sst"), donor);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "seg_1000000.sst"), donor);

        using (var reopened = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100, segmentFolder: _dir, segmentPrefix: "seg"))
        {
            var (v1, found1) = await reopened.TryGetValueAsync(K(1));
            found1.Should().BeTrue();
            v1.Value.Should().Be("v1");
        }

        SstNames().Should().Equal(["seg_000000.sst", "seg_00000000.sst", "seg_0000005.sst", "seg_000001.sst", "seg_12.sst", "seg_backup.sst"]);
    }

    // ── SortedStringTable: no raw whole-file mutators; Delete resets derived state ────────────

    private static readonly string[] s_rawMutators =
    [
        "WriteAllBytes", "WriteAllBytesAsync", "WriteAllText", "WriteAllTextAsync",
        "AppendAllBytes", "AppendAllBytesAsync", "AppendAllText", "AppendAllTextAsync",
        "Create", "Open", "Close",
    ];

    [Fact]
    public void SortedStringTable_ExposesNoRawWholeFileMutator()
    {
        var type = typeof(SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>);
        var exposed = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .Intersect(s_rawMutators)
            .ToArray();

        exposed.Should().BeEmpty(
            "a raw rewrite replaces the bytes the footer, bloom filter and block-cache identity were derived from, leaving the instance serving contents no longer on disk");
        typeof(ISortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>)
            .GetInterfaces().Should().NotContain(typeof(IStorageFile));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SortedStringTable_Delete_RejectsReadsUntilRewritten(bool cached)
    {
        IBlockCache? cache = cached ? new LruBlockCache(maxBytes: 1 << 20) : null;
        var table = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _dir, "t.sst", blockCache: cache);
        table.Write([(K(1), V("OLD"), 5UL)]);
        table.TryGetValue(K(1), out _).Should().BeTrue();

        table.Delete(FileDeletionHandling.DeleteIfExists);

        File.Exists(Path.Combine(_dir, "t.sst")).Should().BeFalse();
        table.WrittenFileCrc32C.Should().Be(0u);
        table.WrittenFileSizeBytes.Should().Be(0L);
        Action pointRead = () => table.TryGetValue(K(1), out _);
        pointRead.Should().Throw<ObjectDisposedException>();
        Action countRead = () => table.GetEntryCount();
        countRead.Should().Throw<ObjectDisposedException>();
        Action sequenceRead = () => _ = table.MaxSequenceNumber;
        sequenceRead.Should().Throw<ObjectDisposedException>();
        Action rangeRead = () => table.TryGetRange(out _, out _);
        rangeRead.Should().Throw<ObjectDisposedException>();
        Action scan = () => table.EnumerateInline().ToArray();
        scan.Should().Throw<ObjectDisposedException>();
        table.Write([(K(2), V("NEW"), 6UL)]);
        table.TryGetValue(K(1), out _).Should().BeFalse();
        table.TryGetValue(K(2), out var value).Should().BeTrue();
        value.Value.Should().Be("NEW");
        table.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SortedStringTable_Delete_InterruptsAnAlreadyStartedScan(bool fromKey)
    {
        using var table = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _dir, "scan.sst");
        table.Write([(K(1), V("a"), 1UL), (K(2), V("b"), 2UL)]);
        using var scan = (fromKey ? table.EnumerateBlockEntriesFrom(K(1)) : table.EnumerateBlockEntriesFiltered()).GetEnumerator();
        scan.MoveNext().Should().BeTrue();
        table.Delete(FileDeletionHandling.DeleteIfExists);
        Action next = () => scan.MoveNext();
        next.Should().Throw<ObjectDisposedException>("a partial scan must never look complete after retirement");
    }
}
