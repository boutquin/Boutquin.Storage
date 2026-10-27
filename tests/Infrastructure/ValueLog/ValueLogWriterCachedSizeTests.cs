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
/// M-4 AC-4.3 — the rotation check reads a cached byte counter, not <c>_currentStream.Length</c>
/// (F-7). Per DR-9 the cached field inherits <see cref="ValueLogWriter"/>'s existing concurrency
/// model: appends are single-writer / append-serialized by the engine's write semaphore, so the
/// counter is updated under that same serialization and needs no new lock. There is no concurrent
/// mutator of the current append target, so these tests exercise the boundary on the single-writer
/// path the contract guarantees.
/// </summary>
public sealed class ValueLogWriterCachedSizeTests : IAsyncDisposable
{
    private readonly string _dir;
    private readonly string _manifestPath;
    private ValueLogManifest? _manifest;

    public ValueLogWriterCachedSizeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vlwc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "valuelog.manifest");
    }

    public async ValueTask DisposeAsync()
    {
        if (_manifest is not null)
        {
            await _manifest.DisposeAsync();
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The rotation check (<c>MaybeRotateAsync</c>) must not stat the stream via
    /// <c>_currentStream.Length</c>; it reads the cached counter instead. RED while the
    /// length-stat remains; GREEN once the cached counter drives the decision.
    /// </summary>
    [Fact]
    public void RotationCheck_DoesNotStatStreamLength()
    {
        var source = ReadWriterSource();
        var maybeRotate = ExtractMethod(source, "private async Task MaybeRotateAsync");

        // Strip // comments so the explanatory note ("not _currentStream.Length") doesn't trip the
        // assertion — only an EXECUTABLE stat of the stream length counts as the defect.
        var executable = StripLineComments(maybeRotate);

        executable.Should().NotContain(
            "_currentStream.Length",
            "AC-4.3: the rotation check must read the cached byte counter, not stat _currentStream.Length");
    }

    [Fact]
    public async Task Append_AtSegmentBoundary_RotatesByCachedCounter()
    {
        // SegmentMaxBytes chosen so the FIRST append fits and the SECOND would overflow → rotate.
        var payload = new byte[200];
        var options = new ValueLogOptions(
            ValueLogThresholdBytes: 64,
            SegmentMaxBytes: 300,
            ReadHandleCacheSize: 16,
            MaxEntryBytes: 64L * 1024 * 1024);

        var (writer, _) = await OpenWriterAsync(options);
        await using var _w = writer;

        var first = await writer.AppendAsync(payload, 1);
        first.FileIndex.Should().Be(0u, "first frame fits inside SegmentMaxBytes");

        // After the first append the cached counter must equal the on-disk segment length so the
        // next rotation decision is exact at the boundary.
        var onDisk = new FileInfo(ValueLogWriter.ResolveSegmentPath(_dir, 0)).Length;
        writer.CurrentFileBytes.Should().Be(onDisk, "the cached counter tracks the on-disk size exactly");

        var second = await writer.AppendAsync(payload, 2);
        second.FileIndex.Should().Be(1u, "the cached-counter boundary check rotates before the overflow");
    }

    [Fact]
    public async Task Append_WellWithinSegment_DoesNotRotate()
    {
        var options = new ValueLogOptions(
            ValueLogThresholdBytes: 64,
            SegmentMaxBytes: 1_000_000,
            ReadHandleCacheSize: 16,
            MaxEntryBytes: 64L * 1024 * 1024);

        var (writer, _) = await OpenWriterAsync(options);
        await using var _w = writer;

        var a = await writer.AppendAsync(new byte[10], 1);
        var b = await writer.AppendAsync(new byte[10], 2);

        a.FileIndex.Should().Be(0u);
        b.FileIndex.Should().Be(0u, "both small frames stay in the same segment");
        writer.CurrentFileBytes.Should().Be(
            new FileInfo(ValueLogWriter.ResolveSegmentPath(_dir, 0)).Length);
    }

    private async Task<(ValueLogWriter writer, ValueLogManifest manifest)> OpenWriterAsync(ValueLogOptions? options = null)
    {
        _manifest = new ValueLogManifest(_manifestPath);
        var writer = await ValueLogWriter.OpenAsync(_dir, _manifest, options ?? ValueLogOptions.Default);
        return (writer, _manifest);
    }

    private static string StripLineComments(string code)
    {
        var lines = code.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            kept.Add(idx >= 0 ? line[..idx] : line);
        }

        return string.Join('\n', kept);
    }

    private static string ExtractMethod(string source, string signaturePrefix)
    {
        var start = source.IndexOf(signaturePrefix, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"expected to find '{signaturePrefix}' in the source");

        // Find the opening brace, then walk to its matching close.
        var braceOpen = source.IndexOf('{', start);
        var depth = 0;
        var i = braceOpen;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }
        }

        return source.Substring(start, i - start + 1);
    }

    private static string ReadWriterSource()
    {
        var asmDir = Path.GetDirectoryName(typeof(ValueLogWriterCachedSizeTests).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
        var sourcePath = Path.Combine(repoRoot, "src", "Infrastructure", "ValueLog", "ValueLogWriter.cs");
        File.Exists(sourcePath).Should().BeTrue($"expected ValueLogWriter source at {sourcePath}");
        return File.ReadAllText(sourcePath);
    }
}
