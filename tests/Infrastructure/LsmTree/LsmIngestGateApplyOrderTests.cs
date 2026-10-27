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
/// The ingest gate (rotation wait) releases and reacquires the engine lock, and the gate now
/// runs BEFORE the sequence number is assigned or any WAL frame is appended. A mutator
/// parked across the gate window therefore serializes AFTER every mutator that overtakes it:
/// when it resumes it draws a LATER seqno, and seqno order provably equals apply order and
/// WAL-frame order (all three happen under one continuous lock hold). This test pins that
/// serialization contract: the overtaken mutator wins last-writer-wins because it genuinely
/// is the last writer, and the point-read and scan paths agree on the outcome before and
/// after the state is flushed. The engine's IngestGateWindowHook seam parks one mutator
/// inside the exact window to make the interleaving deterministic.
/// </summary>
public sealed class LsmIngestGateApplyOrderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmGateOrder_{Guid.NewGuid():N}");

    public LsmIngestGateApplyOrderTests()
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

    private static SerializableWrapper<int> K(int i) => new(i);
    private static SerializableWrapper<string> V(string s) => new(s);

    [Fact]
    public async Task SetParkedAcrossGate_SerializesAfterOvertakingRemove_AndReadPathsAgree()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _dir,
            segmentPrefix: "seg");

        // Fill the active memtable so the next mutator takes the gate path.
        await engine.SetAsync(K(1), V("a"));
        await engine.SetAsync(K(2), V("b"));

        // Park the Set inside the release→gate window (one-shot hook).
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fired = 0;
        engine.IngestGateWindowHook = async () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
            {
                parked.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }
        };

        // T1: Set(5) — parks inside the gate window BEFORE drawing a seqno (gate-first order).
        var setTask = engine.SetAsync(K(5), V("set-late"));
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        engine.IngestGateWindowHook = null;

        // T2: Remove(5) — overtakes and completes first, drawing the EARLIER seqno.
        await engine.RemoveAsync(K(5));

        // Resume T1: it serializes after the Remove, draws the LATER seqno, and wins LWW —
        // it genuinely is the last writer under the gate-first ordering.
        release.TrySetResult();
        await setTask;

        var (liveValue, foundLive) = await engine.TryGetValueAsync(K(5));
        foundLive.Should().BeTrue(
            "the parked Set serialized after the Remove and carries the newer sequence number");
        liveValue.Value.Should().Be("set-late");
        (await engine.GetAllItemsAsync()).Any(kv => kv.Key.Equals(K(5))).Should().BeTrue(
            "the scan path must agree with the point read");

        // The outcome must be durable and unchanged across a flush.
        await engine.FlushAsync();
        var (flushedValue, foundFlushed) = await engine.TryGetValueAsync(K(5));
        foundFlushed.Should().BeTrue("the last writer's value must survive the flush");
        flushedValue.Value.Should().Be("set-late");
        (await engine.GetAllItemsAsync()).Any(kv => kv.Key.Equals(K(5))).Should().BeTrue(
            "the scan path must agree with the point read after the flush");
    }
}
