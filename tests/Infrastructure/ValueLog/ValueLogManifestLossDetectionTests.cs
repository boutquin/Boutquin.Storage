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

using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests.ValueLog;

/// <summary>
/// Lost-manifest detection, as must-fail-loud / must-recover pairs. Appends to a value-log segment
/// start only after its add record is durable, so a segment holding frames beyond its header that
/// no manifest record lists as live or as deleted proves the manifest lost an acknowledged add —
/// whether the manifest ends cleanly on a record boundary, holds only its header, is emptied or
/// zero-filled, or is missing. Recovery fails loud and keeps every byte rather than letting the
/// orphan sweep delete the segment. The reachable crash shapes — a successor created but never
/// added, a segment whose delete record is durable but whose unlink did not happen — still recover.
/// </summary>
public sealed class ValueLogManifestLossDetectionTests : IAsyncDisposable
{
    private static readonly ValueLogOptions s_threeFramesPerSegment = ValueLogOptions.Default with { SegmentMaxBytes = 1000 };

    private readonly string _dir;
    private readonly string _manifestPath;
    private readonly string _otherDir;

    public ValueLogManifestLossDetectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vl-loss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "valuelog.manifest");
        _otherDir = Path.Combine(Path.GetTempPath(), "vl-loss-m-" + Guid.NewGuid().ToString("N"));
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_otherDir, recursive: true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Must fail loud: the manifest ends exactly on a record boundary without the add of the active
    /// segment, which holds acknowledged frames. No byte of the lost record survives, so only the
    /// directory can show the loss.
    /// </summary>
    [Fact]
    public async Task OpenAsync_ManifestEndingCleanlyWithoutAddOfSegmentHoldingFrames_FailsLoudAndKeepsSegments()
    {
        var pointers = await AppendAsync(8).ConfigureAwait(true);
        pointers.Select(p => p.FileIndex).Should().Equal(0u, 0u, 0u, 1u, 1u, 1u, 2u, 2u);
        TruncateManifestBy(ValueLogManifest.RecordSize); // drops the final add of segment 2
        var before = Snapshot();

        await Open(_manifestPath, _dir).Should().ThrowAsync<CorruptValueLogException>(
            "segment 2 holds frames that no manifest record lists or deletes").ConfigureAwait(true);
        Snapshot().Should().BeEquivalentTo(before, "fail-loud recovery deletes nothing");
    }

    /// <summary>
    /// Must fail loud: a manifest holding only its header beside segments that hold acknowledged frames.
    /// </summary>
    [Fact]
    public async Task OpenAsync_HeaderOnlyManifestBesideSegmentsHoldingFrames_FailsLoudAndKeepsSegments()
    {
        await AppendAsync(8).ConfigureAwait(true);
        var manifest = File.ReadAllBytes(_manifestPath);
        File.WriteAllBytes(_manifestPath, manifest[..ValueLogManifest.HeaderSize]);
        var before = Snapshot();

        await Open(_manifestPath, _dir).Should().ThrowAsync<CorruptValueLogException>().ConfigureAwait(true);
        Snapshot().Should().BeEquivalentTo(before);
    }

    /// <summary>
    /// Must recover: a crash after segment 1's seal and its successor's creation, before the successor's
    /// add became durable. The successor holds only its header, so nothing was acknowledged in it.
    /// </summary>
    [Fact]
    public async Task OpenAsync_ManifestEndingCleanlyBeforeAddOfFramelessSuccessor_RecoversEveryAcknowledgedValue()
    {
        var pointers = await AppendAsync(7).ConfigureAwait(true);
        pointers[6].FileIndex.Should().Be(2u);
        TruncateManifestBy(ValueLogManifest.RecordSize); // the successor's add never became durable
        var seg2 = ValueLogWriter.ResolveSegmentPath(_dir, 2);
        File.WriteAllBytes(seg2, File.ReadAllBytes(seg2)[..ValueLogFrame.HeaderSize]); // value 7 was never written

        await using (var m = new ValueLogManifest(_manifestPath))
        await using (var w = await ValueLogWriter.OpenAsync(_dir, m, s_threeFramesPerSegment).ConfigureAwait(true))
        {
            var next = await w.AppendAsync(Payload(9), 99).ConfigureAwait(true);
            next.FileIndex.Should().Be(2u);
            using var reader = new ValueLogReader(_dir, s_threeFramesPerSegment);
            for (var i = 0; i < 6; i++)
            {
                reader.Read(pointers[i]).Should().Equal(Payload(i));
            }

            reader.Read(next).Should().Equal(Payload(9));
        }
    }

    /// <summary>
    /// Must recover: a segment whose delete record is durable but whose unlink a crash interrupted
    /// still holds its frames; the delete record vouches for it, so the sweep removes it.
    /// </summary>
    [Fact]
    public async Task OpenAsync_SegmentLingeringAfterDurableDeleteRecord_IsSweptAsOrphan()
    {
        var pointers = await AppendAsync(8).ConfigureAwait(true);
        await using (var m = new ValueLogManifest(_manifestPath))
        {
            await m.RecordDeleteAsync(0).ConfigureAwait(true);
        }

        var seg0 = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        File.Exists(seg0).Should().BeTrue();

        await using (var m = new ValueLogManifest(_manifestPath))
        await using (var w = await ValueLogWriter.OpenAsync(_dir, m, s_threeFramesPerSegment).ConfigureAwait(true))
        {
            File.Exists(seg0).Should().BeFalse("a segment the manifest records as deleted is an orphan");
            using var reader = new ValueLogReader(_dir, s_threeFramesPerSegment);
            for (var i = 3; i < 8; i++)
            {
                reader.Read(pointers[i]).Should().Equal(Payload(i));
            }
        }
    }

    /// <summary>
    /// Must fail loud: a manifest that is emptied, zero-filled, holds only a header prefix, or is missing
    /// beside segments that hold acknowledged frames. A manifest torn at creation cannot coexist with
    /// such segments (no segment exists before the manifest header is durable), so it is not treated as
    /// fresh state, and neither the manifest nor any segment is rewritten.
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("zero-3")]
    [InlineData("zero-8")]
    [InlineData("prefix-4")]
    [InlineData("prefix-7")]
    [InlineData("missing")]
    public async Task OpenAsync_ManifestTornAtCreationOrMissingBesideSegmentsHoldingFrames_FailsLoudAndKeepsEvidence(string shape)
    {
        await AppendAsync(8).ConfigureAwait(true);
        var header = File.ReadAllBytes(_manifestPath)[..ValueLogManifest.HeaderSize];
        byte[]? manifest = shape switch
        {
            "empty" => [],
            "zero-3" => new byte[3],
            "zero-8" => new byte[8],
            "prefix-4" => header[..4],
            "prefix-7" => header[..7],
            _ => null,
        };
        if (manifest is null)
        {
            File.Delete(_manifestPath);
        }
        else
        {
            File.WriteAllBytes(_manifestPath, manifest);
        }

        var before = Snapshot();

        await Open(_manifestPath, _dir).Should().ThrowAsync<CorruptValueLogException>(
            "segments holding acknowledged frames prove the manifest lost its records").ConfigureAwait(true);
        Snapshot().Should().BeEquivalentTo(before, "neither the manifest nor any segment is rewritten or deleted");
        File.Exists(_manifestPath).Should().Be(manifest is not null);
    }

    /// <summary>
    /// Must recover: a manifest torn at creation beside a segment holding only its header (no frame
    /// was ever acknowledged) still has its header rewritten and opens empty.
    /// </summary>
    [Fact]
    public async Task OpenAsync_ManifestTornAtCreationBesideFramelessSegment_RewritesHeaderAndOpens()
    {
        File.WriteAllBytes(_manifestPath, []);
        File.WriteAllBytes(ValueLogWriter.ResolveSegmentPath(_dir, 0), [(byte)'B', (byte)'S', (byte)'V', (byte)'L', 1, 0, 0, 0]);

        await using (var m = new ValueLogManifest(_manifestPath))
        await using (var w = await ValueLogWriter.OpenAsync(_dir, m, s_threeFramesPerSegment).ConfigureAwait(true))
        {
            (await m.GetLiveFilesAsync().ConfigureAwait(true)).Should().BeEquivalentTo([0u]);
            var pointer = await w.AppendAsync(Payload(1), 1).ConfigureAwait(true);
            using var reader = new ValueLogReader(_dir, s_threeFramesPerSegment);
            reader.Read(pointer).Should().Equal(Payload(1));
        }

        new FileInfo(_manifestPath).Length.Should().BeGreaterThan(ValueLogManifest.HeaderSize);
    }

    /// <summary>
    /// Must fail loud: the writer's orphan sweep runs over the segment directory, which need not be the
    /// manifest's. A segment there holding frames that the manifest neither lists nor deletes is
    /// refused at the sweep itself, so no manifest layout can turn it into a deletion.
    /// </summary>
    [Fact]
    public async Task OpenAsync_SegmentDirectoryHoldingUnrecordedFramesWithManifestElsewhere_FailsLoudAndKeepsSegments()
    {
        await AppendAsync(8).ConfigureAwait(true);
        var before = Snapshot();

        await Open(Path.Combine(_otherDir, "valuelog.manifest"), _dir).Should().ThrowAsync<CorruptValueLogException>()
            .ConfigureAwait(true);
        Snapshot().Should().BeEquivalentTo(before);
    }

    private static Func<Task> Open(string manifestPath, string segmentDirectory) => async () =>
    {
        await using var m = new ValueLogManifest(manifestPath);
        await using var w = await ValueLogWriter.OpenAsync(segmentDirectory, m, s_threeFramesPerSegment).ConfigureAwait(true);
    };

    private async Task<List<ValueLogPointer>> AppendAsync(int count)
    {
        var pointers = new List<ValueLogPointer>();
        await using (var m = new ValueLogManifest(_manifestPath))
        await using (var w = await ValueLogWriter.OpenAsync(_dir, m, s_threeFramesPerSegment).ConfigureAwait(true))
        {
            for (var i = 0; i < count; i++)
            {
                pointers.Add(await w.AppendAsync(Payload(i), (ulong)(i + 1)).ConfigureAwait(true));
            }
        }

        return pointers;
    }

    private void TruncateManifestBy(int bytes)
    {
        using var fs = new FileStream(_manifestPath, FileMode.Open, FileAccess.Write);
        fs.SetLength(fs.Length - bytes);
    }

    private Dictionary<string, string> Snapshot()
        => Directory.EnumerateFiles(_dir).ToDictionary(p => Path.GetFileName(p), p => Convert.ToHexString(File.ReadAllBytes(p)));

    private static byte[] Payload(int i) => Enumerable.Repeat((byte)(0x41 + i), 300).ToArray();
}
