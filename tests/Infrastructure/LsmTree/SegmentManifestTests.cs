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
namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Tests for <see cref="SegmentManifest"/> — the <c>CURRENT</c> file that records the
/// durable segment set for an LSM engine.
/// </summary>
public sealed class SegmentManifestTests : IDisposable
{
    private readonly string _dir;

    public SegmentManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "segment-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Write_ThenRead_RoundTrips()
    {
        var segments = new[] { "seg_000000.dat", "seg_000001.dat", "seg_000002.dat" };

        WriteFileNames(_dir, segments);
        var roundTrip = SegmentManifest.Read(_dir);

        roundTrip.Should().NotBeNull();
        roundTrip!.Should().Equal(segments);
    }

    [Fact]
    public void Read_WhenFileDoesNotExist_ReturnsNull()
    {
        var result = SegmentManifest.Read(_dir);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65)]
    [InlineData(int.MaxValue)]
    public void WriteEntries_InvalidLevel_PreservesCommittedManifest(int level)
    {
        SegmentManifest.WriteEntries(_dir, [new SegmentManifest.Entry("kept.sst", 123u, 10L, 64)]);
        var committed = File.ReadAllBytes(Path.Combine(_dir, SegmentManifest.FileName));

        Action write = () => SegmentManifest.WriteEntries(_dir, [new SegmentManifest.Entry("invalid.sst", 0u, 0L, level)]);

        write.Should().Throw<ArgumentException>();
        File.ReadAllBytes(Path.Combine(_dir, SegmentManifest.FileName)).Should().Equal(committed);
        SegmentManifest.ReadEntries(_dir).Should().ContainSingle().Which.Level.Should().Be(64);
    }

    [Fact]
    public void Write_EmptyList_ProducesEmptyManifest()
    {
        WriteFileNames(_dir, []);
        var roundTrip = SegmentManifest.Read(_dir);

        roundTrip.Should().NotBeNull();
        roundTrip!.Should().BeEmpty();
    }

    [Fact]
    public void Write_IsAtomic_WritesToTempThenRenames()
    {
        var segments = new[] { "seg_000000.dat" };
        WriteFileNames(_dir, segments);

        // No temp file left behind after a successful write.
        Directory.GetFiles(_dir, "*.tmp*").Should().BeEmpty();
        Directory.GetFiles(_dir, "CURRENT*").Should().ContainSingle();
    }

    [Fact]
    public void Exists_ReflectsManifestState()
    {
        SegmentManifest.Exists(_dir).Should().BeFalse();

        WriteFileNames(_dir, ["a.dat"]);
        SegmentManifest.Exists(_dir).Should().BeTrue();
    }

    [Fact]
    public void Write_OverwritesExistingManifest()
    {
        WriteFileNames(_dir, ["old.dat"]);
        WriteFileNames(_dir, ["new1.dat", "new2.dat"]);

        var roundTrip = SegmentManifest.Read(_dir);
        roundTrip.Should().Equal("new1.dat", "new2.dat");
    }

    // Helper: tests continue to use a filename-only overload for brevity; CRC/size/level
    // default to zero (the tests do not exercise the metadata columns).
    private static void WriteFileNames(string folder, IReadOnlyList<string> fileNames)
    {
        var entries = new SegmentManifest.Entry[fileNames.Count];
        for (var i = 0; i < fileNames.Count; i++)
        {
            entries[i] = new SegmentManifest.Entry(fileNames[i], Crc32C: 0u, SizeBytes: 0L);
        }
        SegmentManifest.WriteEntries(folder, entries);
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("../evil.dat")]
    [InlineData("sub/evil.dat")]
    [InlineData("/etc/passwd")]
    public void ReadEntries_FilenameEscapesSegmentFolder_Throws(string maliciousName)
    {
        // A crafted/corrupt manifest row whose filename contains a directory separator,
        // a '..' component, or an absolute/rooted path is a path-traversal primitive:
        // recovery reads and compaction deletes by that name, escaping the segment folder.
        // ReadEntries must reject such rows as manifest corruption.
        File.WriteAllText(
            Path.Combine(_dir, "CURRENT"),
            $"{maliciousName}\t00000000\t0\t0\n");

        var act = () => SegmentManifest.ReadEntries(_dir);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Read_SkipsBlankLines()
    {
        // A manifest with blank lines shouldn't produce phantom entries; defensive
        // against editors that add trailing newlines. Every row must still carry the
        // four-column format (filename, crc, size, level) — H4 stripped legacy
        // filename-only rows from v2.0.0.
        File.WriteAllText(
            Path.Combine(_dir, "CURRENT"),
            "a.dat\t00000000\t0\t0\n\nb.dat\t00000000\t0\t0\n\n");

        var roundTrip = SegmentManifest.Read(_dir);
        roundTrip.Should().Equal("a.dat", "b.dat");
    }

    [Fact]
    public void ReadEntries_LevelExceedsMaximum_ThrowsInvalidDataException()
    {
        // The writer and compaction planner share the reader's maximum level. Reject a
        // higher level introduced by external modification so a
        // corrupted manifest never silently feeds a nonsensical level into the compaction
        // strategy or the LSM engine's level accounting.
        File.WriteAllText(
            Path.Combine(_dir, "CURRENT"),
            "a.dat\t00000000\t1024\t65\n");

        var act = () => SegmentManifest.ReadEntries(_dir);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*level*65*exceeds*maximum*");
    }
}
