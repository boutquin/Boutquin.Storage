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
/// Segment rotation must be atomic-or-nothing: a rotation whose manifest add fails must not
/// leave the writer appending into a segment the manifest does not list as live. The startup
/// reconciliation sweep deletes every on-disk segment the manifest does not know about, so an
/// unmanifested live append target turns acknowledged, fsync'd values into silently deleted
/// bytes at the next restart — a WiscKey durability violation (the value log is the only copy
/// of large payloads; DDIA 2e Ch. 4 key/value separation).
/// </summary>
public sealed class ValueLogRotationAtomicityTests : IAsyncDisposable
{
    private readonly string _dir;
    private readonly string _manifestPath;

    public ValueLogRotationAtomicityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vlra-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "valuelog.manifest");
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RotationManifestFailure_ThenSuccessfulAppend_SurvivesReopen()
    {
        // Small segment cap so the second append forces a rotation.
        var options = ValueLogOptions.Default with { SegmentMaxBytes = 256 };

        var manifest = new ValueLogManifest(_manifestPath);
        var writer = await ValueLogWriter.OpenAsync(_dir, manifest, options);

        // Fill segment 0 close to the cap so the next append rotates to segment 1.
        await writer.AppendAsync(new byte[200], sequenceNumber: 1ul);

        // Inject a one-shot manifest failure at the rotation's record-add step — the same
        // program point a real manifest I/O error or cancellation would fire at.
        var failedOnce = false;
        writer.RotationManifestRecordHook = _ =>
        {
            if (!failedOnce)
            {
                failedOnce = true;
                throw new IOException("injected manifest record failure");
            }

            return Task.CompletedTask;
        };

        var payload = Encoding.UTF8.GetBytes("value-after-failed-rotation");
        await Assert.ThrowsAsync<IOException>(() => writer.AppendAsync(payload, sequenceNumber: 2ul));

        // The retry append is ACKNOWLEDGED — from here on the value must be durable.
        var pointer = await writer.AppendAsync(payload, sequenceNumber: 3ul);

        await writer.DisposeAsync();
        await manifest.DisposeAsync();

        // Simulated restart: reopening runs the reconciliation sweep, which deletes every
        // on-disk segment the manifest does not list as live.
        var manifest2 = new ValueLogManifest(_manifestPath);
        var writer2 = await ValueLogWriter.OpenAsync(_dir, manifest2, options);

        try
        {
            using var reader = new ValueLogReader(_dir, options);
            var bytes = reader.Read(pointer);
            bytes.Should().Equal(payload, "an acknowledged append must survive restart even when an earlier rotation attempt failed at the manifest step");
        }
        finally
        {
            await writer2.DisposeAsync();
            await manifest2.DisposeAsync();
        }
    }
}
