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
/// A fault mid-frame (partial write followed by an I/O error or cancellation) leaves torn
/// bytes at the segment tail with the stream position advanced past them. Without repair,
/// the NEXT append seals those torn bytes into the middle of the file: at reopen the
/// sequential scan finds an unreadable frame followed by a valid one — classified as
/// mid-file corruption — and the engine refuses to start, even though every acknowledged
/// frame is intact. The writer must truncate the torn tail back to the failed frame's start
/// offset before any subsequent append.
/// </summary>
public sealed class ValueLogAppendFaultRepairTests : IAsyncDisposable
{
    private readonly string _dir;
    private readonly string _manifestPath;

    public ValueLogAppendFaultRepairTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vlfr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "valuelog.manifest");
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task MidFrameWriteFault_TornTailRepaired_NextAppendAndReopenAreClean()
    {
        var options = ValueLogOptions.Default;

        var manifest = new ValueLogManifest(_manifestPath);
        var writer = await ValueLogWriter.OpenAsync(_dir, manifest, options);

        var payloadA = Encoding.UTF8.GetBytes("first-acknowledged-value");
        var pointerA = await writer.AppendAsync(payloadA, sequenceNumber: 1ul);

        // One-shot mid-frame fault: write only half the frame's bytes, then fail — the state a
        // disk-full / I/O error / cancellation inside WriteAsync produces.
        var faulted = false;
        writer.FrameWriteInterceptor = async (stream, frame, ct) =>
        {
            if (!faulted)
            {
                faulted = true;
                await stream.WriteAsync(frame[..(frame.Length / 2)], ct);
                throw new IOException("injected mid-frame write fault");
            }

            await stream.WriteAsync(frame, ct);
        };

        var payloadB = Encoding.UTF8.GetBytes("value-whose-append-faults");
        await Assert.ThrowsAsync<IOException>(() => writer.AppendAsync(payloadB, sequenceNumber: 2ul));

        // The next append is ACKNOWLEDGED — it must land on a clean tail, not after torn bytes.
        var payloadC = Encoding.UTF8.GetBytes("value-after-the-fault");
        var pointerC = await writer.AppendAsync(payloadC, sequenceNumber: 3ul);

        await writer.DisposeAsync();
        await manifest.DisposeAsync();

        // Simulated restart: the sequential scan must see an unbroken run of valid frames.
        var manifest2 = new ValueLogManifest(_manifestPath);
        var writer2 = await ValueLogWriter.OpenAsync(_dir, manifest2, options);

        try
        {
            using var reader = new ValueLogReader(_dir, options);
            reader.Read(pointerA).Should().Equal(payloadA);
            reader.Read(pointerC).Should().Equal(payloadC, "an append after a mid-frame fault must not be corrupted by the fault's torn bytes");
        }
        finally
        {
            await writer2.DisposeAsync();
            await manifest2.DisposeAsync();
        }
    }
}
