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
/// M-4 AC-4.1 — <see cref="ValueLogWriter.AppendAsync"/> and the fresh-segment open path use
/// <c>await WriteAsync(...).ConfigureAwait(false)</c> rather than a synchronous
/// <c>Stream.Write</c> on the async path (F-4, F-5). The structural change is verified by a
/// source grep (no <c>_currentStream.Write(</c> remains on the async path) and the behavioral
/// correctness of append + rotation is verified by the round-trip below.
/// </summary>
public sealed class ValueLogWriterAsyncPathTests : IAsyncDisposable
{
    private readonly string _dir;
    private readonly string _manifestPath;
    private ValueLogManifest? _manifest;

    public ValueLogWriterAsyncPathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vlwa-" + Guid.NewGuid().ToString("N"));
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
    /// The async write path must contain NO synchronous <c>_currentStream.Write(</c> call — the
    /// frame header (fresh-segment open) and entry frame (append) must both go through
    /// <c>WriteAsync</c>. This source-level assertion is RED while the sync writes remain and GREEN
    /// once they are converted. The path to the source is resolved relative to the test assembly so
    /// the assertion travels with the repo.
    /// </summary>
    [Fact]
    public void AsyncPath_HasNoSynchronousCurrentStreamWrite()
    {
        var source = ReadWriterSource();

        // The non-async ScanEffectiveLengthAsync / ReadFully helpers use Stream.Read, never Write,
        // so any "_currentStream.Write(" is on the append/open async path and is the forbidden form.
        source.Should().NotContain(
            "_currentStream.Write(",
            "AC-4.1: the async append/open path must use await WriteAsync(...), not synchronous Stream.Write");
    }

    [Fact]
    public async Task AppendAsync_RoundTripsThroughAsyncWrite()
    {
        var (writer, _) = await OpenWriterAsync();
        await using var _w = writer;

        var payload = Encoding.UTF8.GetBytes("async-written value log entry");
        var pointer = await writer.AppendAsync(payload, sequenceNumber: 99ul);

        var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        using var _r = reader;
        var read = reader.Read(pointer);

        read.Should().Equal(payload);
    }

    [Fact]
    public async Task AppendAsync_RotationThroughAsyncWrite_PreservesAllEntries()
    {
        var payload = new byte[200];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        var smallSegmentOptions = new ValueLogOptions(
            ValueLogThresholdBytes: 64,
            SegmentMaxBytes: 300, // forces rotation on the second append
            ReadHandleCacheSize: 16,
            MaxEntryBytes: 64L * 1024 * 1024);

        var (writer, _) = await OpenWriterAsync(smallSegmentOptions);
        await using var _w = writer;

        var first = await writer.AppendAsync(payload, 1);
        var second = await writer.AppendAsync(payload, 2);

        first.FileIndex.Should().Be(0u);
        second.FileIndex.Should().Be(1u, "the fresh segment opens through the async write path on rotation");

        var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        using var _r = reader;
        reader.Read(first).Should().Equal(payload);
        reader.Read(second).Should().Equal(payload);
    }

    private async Task<(ValueLogWriter writer, ValueLogManifest manifest)> OpenWriterAsync(ValueLogOptions? options = null)
    {
        _manifest = new ValueLogManifest(_manifestPath);
        var writer = await ValueLogWriter.OpenAsync(_dir, _manifest, options ?? ValueLogOptions.Default);
        return (writer, _manifest);
    }

    private static string ReadWriterSource()
    {
        // tests/Infrastructure/bin/Debug/net10.0 -> repo root is five levels up.
        var asmDir = Path.GetDirectoryName(typeof(ValueLogWriterAsyncPathTests).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
        var sourcePath = Path.Combine(repoRoot, "src", "Infrastructure", "ValueLog", "ValueLogWriter.cs");
        File.Exists(sourcePath).Should().BeTrue($"expected ValueLogWriter source at {sourcePath}");
        return File.ReadAllText(sourcePath);
    }
}
