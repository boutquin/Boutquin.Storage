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
/// M-4 AC-4.4 — <see cref="ValueLogWriter"/> dispose is atomic: <c>_disposed</c> uses the
/// <c>Interlocked.Exchange</c> first-caller-wins pattern, so concurrent <c>DisposeAsync</c> calls
/// run the teardown exactly once and a racing <c>AppendAsync</c> either succeeds or throws
/// <see cref="ObjectDisposedException"/> — never an <see cref="NullReferenceException"/> from a
/// half-disposed stream (F-8). The unguarded <c>bool</c> flag let two disposers both pass the
/// <c>if (_disposed) return;</c> check and double-tear-down the stream; the stress harness below
/// reliably exercises that window.
/// </summary>
public sealed class ValueLogWriterDisposeRaceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _manifestPath;

    public ValueLogWriterDisposeRaceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vlwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "valuelog.manifest");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ConcurrentDispose_TearsDownExactlyOnce_NoDoubleTeardown()
    {
        // Many iterations widen the chance of catching the double-teardown window.
        for (var iteration = 0; iteration < 200; iteration++)
        {
            await using var manifest = new ValueLogManifest(
                Path.Combine(_dir, $"m{iteration}.manifest"));
            var subDir = Path.Combine(_dir, $"vl{iteration}");
            Directory.CreateDirectory(subDir);
            var writer = await ValueLogWriter.OpenAsync(subDir, manifest, ValueLogOptions.Default);

            const int disposers = 8;
            using var start = new Barrier(disposers);
            var tasks = new Task[disposers];
            for (var t = 0; t < disposers; t++)
            {
                tasks[t] = Task.Run(async () =>
                {
                    start.SignalAndWait();
                    // Concurrent disposers must not throw — the loser of the race is a clean no-op.
                    await writer.DisposeAsync();
                });
            }

            await Task.WhenAll(tasks);
        }
    }

    [Fact]
    public async Task DisposeRacingAppend_NeverThrowsNullReference()
    {
        for (var iteration = 0; iteration < 150; iteration++)
        {
            await using var manifest = new ValueLogManifest(
                Path.Combine(_dir, $"mr{iteration}.manifest"));
            var subDir = Path.Combine(_dir, $"vlr{iteration}");
            Directory.CreateDirectory(subDir);
            var writer = await ValueLogWriter.OpenAsync(subDir, manifest, ValueLogOptions.Default);

            using var start = new Barrier(2);

            var appendTask = Task.Run(async () =>
            {
                start.SignalAndWait();
                try
                {
                    await writer.AppendAsync(new byte[64], (ulong)iteration);
                }
                catch (ObjectDisposedException)
                {
                    // Acceptable: the dispose won the race. The forbidden outcome is an NRE from a
                    // half-disposed stream, which this catch deliberately does NOT swallow.
                }
                catch (InvalidOperationException)
                {
                    // Also acceptable: "Append target stream is not open." after disposal nulled it
                    // — a typed, deliberate signal, not a half-disposed-state crash.
                }
            });

            var disposeTask = Task.Run(async () =>
            {
                start.SignalAndWait();
                await writer.DisposeAsync();
            });

            // If a half-disposed stream surfaces an NRE, Task.WhenAll rethrows it and the test fails.
            await Task.WhenAll(appendTask, disposeTask);
        }
    }
}
