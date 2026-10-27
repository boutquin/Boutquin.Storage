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
/// Sealed-segment integrity, as must-recover / must-fail-loud pairs. Only the highest (active)
/// value-log segment can hold an interrupted append: rotation seals segment N in the manifest —
/// its final byte length, frame count and checksum — before segment N+1 exists, and nothing is
/// appended to a sealed segment again. Recovery therefore tear-repairs the active segment only;
/// any damage to a sealed segment, including damage that leaves no damaged frame behind (a cut at
/// a frame boundary, an emptied file, extra bytes, reordered frames), fails loud and keeps every
/// byte.
/// </summary>
public sealed class ValueLogSegmentSealIntegrityTests : IAsyncDisposable
{
    private static readonly ValueLogOptions s_threeFramesPerSegment = ValueLogOptions.Default with { SegmentMaxBytes = 1000 };

    private readonly string _dir;
    private readonly string _manifestPath;

    public ValueLogSegmentSealIntegrityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vl-seal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "valuelog.manifest");
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Must fail loud: damage to the final frame of a sealed (non-highest) segment — a flipped payload
    /// byte, a cut inside the frame, or the frame zeroed — is the shape the tear rule would repair in
    /// the active segment. In a sealed segment every frame was acknowledged before its successor
    /// existed, so recovery refuses and leaves the segment byte for byte.
    /// </summary>
    [Theory]
    [InlineData("payload-flip-last-byte")]
    [InlineData("truncate-mid-final-frame")]
    [InlineData("zero-final-frame")]
    public async Task OpenAsync_DamagedFinalFrameOfSealedSegment_FailsLoudAndKeepsSegment(string damage)
    {
        var pointers = await AppendAsync(s_threeFramesPerSegment, Values(5, 300, seed: 10)).ConfigureAwait(true);
        pointers.Select(p => p.FileIndex).Should().Equal(0u, 0u, 0u, 1u, 1u);
        var seg0 = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytes = File.ReadAllBytes(seg0);
        var finalStart = (int)pointers[2].Offset;
        switch (damage)
        {
            case "payload-flip-last-byte":
                bytes[^5] ^= 0x01;
                break;
            case "truncate-mid-final-frame":
                bytes = bytes[..(finalStart + 100)];
                break;
            default:
                Array.Clear(bytes, finalStart, bytes.Length - finalStart);
                break;
        }

        File.WriteAllBytes(seg0, bytes);

        await OpenAsync(s_threeFramesPerSegment).Should().ThrowAsync<CorruptValueLogException>(
            "a sealed segment cannot hold an interrupted append, so damage to it is corruption").ConfigureAwait(true);
        File.ReadAllBytes(seg0).Should().Equal(bytes, "fail-loud recovery keeps the damaged segment untouched");
    }

    /// <summary>
    /// Must fail loud: an all-zero run covering several acknowledged frames at the end of a sealed
    /// segment. The tear rule accepts such a run only in the active segment.
    /// </summary>
    [Fact]
    public async Task OpenAsync_ZeroRunOverAcknowledgedFramesOfSealedSegment_FailsLoudAndKeepsSegment()
    {
        var options = ValueLogOptions.Default with { SegmentMaxBytes = 400 };
        var pointers = await AppendAsync(options, Values(4, 100, seed: 20)).ConfigureAwait(true);
        pointers.Select(p => p.FileIndex).Should().Equal(0u, 0u, 0u, 1u);
        var seg0 = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytes = File.ReadAllBytes(seg0);
        Array.Clear(bytes, (int)pointers[1].Offset, bytes.Length - (int)pointers[1].Offset);
        File.WriteAllBytes(seg0, bytes);

        await OpenAsync(options).Should().ThrowAsync<CorruptValueLogException>().ConfigureAwait(true);
        File.ReadAllBytes(seg0).Should().Equal(bytes);
    }

    /// <summary>
    /// Must fail loud: damage that leaves no damaged frame — a sealed segment cut at an interior frame
    /// boundary, emptied to its header, emptied entirely, or grown by extra bytes — disagrees with the
    /// length the seal recorded.
    /// </summary>
    [Theory]
    [InlineData("cut-at-frame-boundary")]
    [InlineData("emptied-to-header")]
    [InlineData("emptied")]
    [InlineData("zeros-appended")]
    [InlineData("valid-frame-appended")]
    public async Task OpenAsync_SealedSegmentLengthDisagreesWithSeal_FailsLoud(string damage)
    {
        var pointers = await AppendAsync(s_threeFramesPerSegment, Values(5, 300, seed: 30)).ConfigureAwait(true);
        var seg0 = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytes = File.ReadAllBytes(seg0);
        bytes = damage switch
        {
            "cut-at-frame-boundary" => bytes[..(int)pointers[1].Offset],
            "emptied-to-header" => bytes[..ValueLogFrame.HeaderSize],
            "emptied" => [],
            "zeros-appended" => [.. bytes, .. new byte[64]],
            _ => [.. bytes, .. bytes[(int)pointers[2].Offset..]],
        };
        File.WriteAllBytes(seg0, bytes);

        await OpenAsync(s_threeFramesPerSegment).Should().ThrowAsync<CorruptValueLogException>(
            "a sealed segment whose length differs from its seal lost or gained bytes").ConfigureAwait(true);
        File.ReadAllBytes(seg0).Should().Equal(bytes);
    }

    /// <summary>
    /// Must fail loud: two equal-size frames of a sealed segment swapped. Every frame still passes its
    /// own checksums and the length and frame count match the seal; only the segment checksum the
    /// seal recorded tells the difference.
    /// </summary>
    [Fact]
    public async Task OpenAsync_SealedSegmentWithReorderedFrames_FailsLoud()
    {
        var pointers = await AppendAsync(s_threeFramesPerSegment, Values(5, 300, seed: 40)).ConfigureAwait(true);
        var seg0 = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytes = File.ReadAllBytes(seg0);
        var a = (int)pointers[0].Offset;
        var b = (int)pointers[1].Offset;
        var frameSize = b - a;
        var first = bytes[a..b];
        bytes.AsSpan(b, frameSize).CopyTo(bytes.AsSpan(a));
        first.CopyTo(bytes.AsSpan(b));
        File.WriteAllBytes(seg0, bytes);

        await OpenAsync(s_threeFramesPerSegment).Should().ThrowAsync<CorruptValueLogException>().ConfigureAwait(true);
        File.ReadAllBytes(seg0).Should().Equal(bytes);
    }

    /// <summary>
    /// Must fail loud: a live segment below the active one with no seal record. Rotation seals a
    /// segment before its successor exists, so an unsealed lower segment means the manifest's seal
    /// was lost; its frames cannot be vouched for.
    /// </summary>
    [Fact]
    public async Task OpenAsync_UnsealedSegmentBelowActiveSegment_FailsLoud()
    {
        await AppendAsync(ValueLogOptions.Default, Values(2, 300, seed: 50)).ConfigureAwait(true);
        await using (var manifest = new ValueLogManifest(_manifestPath))
        {
            await manifest.RecordAddAsync(1).ConfigureAwait(true);
        }

        File.WriteAllBytes(ValueLogWriter.ResolveSegmentPath(_dir, 1), [(byte)'B', (byte)'S', (byte)'V', (byte)'L', 1, 0, 0, 0]);

        await OpenAsync(ValueLogOptions.Default).Should().ThrowAsync<CorruptValueLogException>().ConfigureAwait(true);
    }

    /// <summary>
    /// Must fail loud: a sealed middle segment the manifest lists as live is missing on disk.
    /// </summary>
    [Fact]
    public async Task OpenAsync_MissingSealedMiddleSegment_FailsLoud()
    {
        await AppendAsync(s_threeFramesPerSegment, Values(8, 300, seed: 60)).ConfigureAwait(true);
        File.Delete(ValueLogWriter.ResolveSegmentPath(_dir, 1));

        await OpenAsync(s_threeFramesPerSegment).Should().ThrowAsync<MissingValueLogFileException>().ConfigureAwait(true);
    }

    /// <summary>
    /// Must recover: the active segment keeps the tear rule while sealed segments below it are
    /// verified — a torn final append in the highest segment is truncated and every acknowledged
    /// value in every segment reads back.
    /// </summary>
    [Fact]
    public async Task OpenAsync_TornTailInActiveSegmentAboveSealedSegments_TruncatesAndKeepsAllValues()
    {
        var values = Values(5, 300, seed: 70);
        var pointers = await AppendAsync(s_threeFramesPerSegment, values).ConfigureAwait(true);
        var seg1 = ValueLogWriter.ResolveSegmentPath(_dir, 1);
        using (var fs = new FileStream(seg1, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength((long)pointers[4].Offset + 100);
        }

        await using var manifest = new ValueLogManifest(_manifestPath);
        await using var writer = await ValueLogWriter.OpenAsync(_dir, manifest, s_threeFramesPerSegment).ConfigureAwait(true);

        new FileInfo(seg1).Length.Should().Be((long)pointers[4].Offset);
        using var reader = new ValueLogReader(_dir, s_threeFramesPerSegment);
        for (var i = 0; i < 4; i++)
        {
            reader.Read(pointers[i]).Should().Equal(values[i]);
        }
    }

    /// <summary>
    /// Must recover: once a rotation sealed a segment, a later frame never lands in it — not even a
    /// smaller frame that would still fit after the successor's creation failed — and every value
    /// survives a reopen.
    /// </summary>
    [Fact]
    public async Task Rotation_FailedAfterSeal_NeverAppendsToSealedSegmentAndSurvivesReopen()
    {
        var options = ValueLogOptions.Default with { SegmentMaxBytes = 256 };
        var manifest = new ValueLogManifest(_manifestPath);
        var writer = await ValueLogWriter.OpenAsync(_dir, manifest, options).ConfigureAwait(true);
        var first = await writer.AppendAsync(new byte[200], 1ul).ConfigureAwait(true);
        writer.RotationManifestRecordHook = _ => throw new IOException("injected crash before the successor's add");

        await Assert.ThrowsAsync<IOException>(() => writer.AppendAsync(new byte[100], 2ul)).ConfigureAwait(true);

        writer.RotationManifestRecordHook = null;
        var small = await writer.AppendAsync(new byte[] { 7 }, 3ul).ConfigureAwait(true);
        small.FileIndex.Should().Be(1u, "a sealed segment never receives another frame, even one that would fit");
        await writer.DisposeAsync().ConfigureAwait(true);
        await manifest.DisposeAsync().ConfigureAwait(true);

        await using (var reopenedManifest = new ValueLogManifest(_manifestPath))
        await using (var reopened = await ValueLogWriter.OpenAsync(_dir, reopenedManifest, options).ConfigureAwait(true))
        {
            var next = await reopened.AppendAsync(new byte[] { 9 }, 4ul).ConfigureAwait(true);
            next.FileIndex.Should().Be(1u, "the sealed segment stays closed after a reopen");
            using var reader = new ValueLogReader(_dir, options);
            reader.Read(first).Should().HaveCount(200);
            reader.Read(small).Should().Equal(new byte[] { 7 });
            reader.Read(next).Should().Equal(new byte[] { 9 });
        }
    }

    /// <summary>
    /// Must recover: a crash between segment 0's durable seal and segment 1's add, reproduced by
    /// recording the seal and then failing the successor's creation before its add. Reopening
    /// verifies segment 0 against its seal and appends to a fresh segment 1.
    /// </summary>
    [Fact]
    public async Task OpenAsync_HighestSegmentSealedWithoutSuccessor_OpensFreshSegment()
    {
        var options = ValueLogOptions.Default with { SegmentMaxBytes = 256 };
        var manifest = new ValueLogManifest(_manifestPath);
        var writer = await ValueLogWriter.OpenAsync(_dir, manifest, options).ConfigureAwait(true);
        var first = await writer.AppendAsync(new byte[200], 1ul).ConfigureAwait(true);
        writer.RotationManifestRecordHook = _ => throw new IOException("injected crash before the successor's add");
        await Assert.ThrowsAsync<IOException>(() => writer.AppendAsync(new byte[100], 2ul)).ConfigureAwait(true);
        await writer.DisposeAsync().ConfigureAwait(true);
        await manifest.DisposeAsync().ConfigureAwait(true);

        await using var reopenedManifest = new ValueLogManifest(_manifestPath);
        (await reopenedManifest.GetLiveFilesAsync().ConfigureAwait(true)).Should().BeEquivalentTo([0u]);
        await using var reopened = await ValueLogWriter.OpenAsync(_dir, reopenedManifest, options).ConfigureAwait(true);
        reopened.CurrentFileIndex.Should().Be(1u, "a sealed highest segment is closed; appends go to a fresh segment");
        var next = await reopened.AppendAsync(new byte[] { 5 }, 3ul).ConfigureAwait(true);
        next.FileIndex.Should().Be(1u);
        using var reader = new ValueLogReader(_dir, options);
        reader.Read(first).Should().HaveCount(200);
    }

    private Func<Task> OpenAsync(ValueLogOptions options) => async () =>
    {
        await using var manifest = new ValueLogManifest(_manifestPath);
        await using var writer = await ValueLogWriter.OpenAsync(_dir, manifest, options).ConfigureAwait(false);
    };

    private async Task<ValueLogPointer[]> AppendAsync(ValueLogOptions options, byte[][] values)
    {
        await using var manifest = new ValueLogManifest(_manifestPath);
        await using var writer = await ValueLogWriter.OpenAsync(_dir, manifest, options).ConfigureAwait(true);
        var pointers = new ValueLogPointer[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            pointers[i] = await writer.AppendAsync(values[i], (ulong)(i + 1)).ConfigureAwait(true);
        }

        return pointers;
    }

    private static byte[][] Values(int count, int size, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count).Select(_ =>
        {
            var bytes = new byte[size];
            rng.NextBytes(bytes);
            return bytes;
        }).ToArray();
    }
}
