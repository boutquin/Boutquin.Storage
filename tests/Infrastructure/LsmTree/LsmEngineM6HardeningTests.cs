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
/// M-6 LSM-engine hardening: <c>SetBulkAsync</c> faulted-state + per-item guards + documented
/// partial-apply atomicity, atomic dispose, and read-surface range/key validation. Each test
/// pins one acceptance criterion through the production <see cref="LsmStorageEngine{TKey,TValue}"/>
/// surface (constructed via the real factory), never a unit in isolation.
/// </summary>
public sealed class LsmEngineM6HardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmM6_{Guid.NewGuid():N}");

    public LsmEngineM6HardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private static SerializableWrapper<int> K(int i) => new(i);
    private static SerializableWrapper<string> V(string s) => new(s);

    private LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> NewEngine(
        int memTableCapacity = 8,
        ISegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>? format = null)
        => LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: memTableCapacity,
            segmentFolder: _dir,
            segmentPrefix: "m6",
            segmentFormat: format);

    /// <summary>
    /// A segment format whose <c>WriteAsync</c> always throws, driving the background flusher
    /// into the engine's terminal Faulted state on the first flush. Modeled on the
    /// <c>BlockableSegmentFormat</c> double in <c>LsmClearFlusherRaceTests</c>.
    /// </summary>
    private sealed class ThrowingSegmentFormat
        : ISegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>
    {
        private readonly SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>> _inner = new();

        public string FileExtension => _inner.FileExtension;
        public bool SupportsSnapshots => _inner.SupportsSnapshots;

        public Task<ISegmentReader<SerializableWrapper<int>, SerializableWrapper<string>>> WriteAsync(
            string segmentFolder,
            string segmentFileName,
            IReadOnlyList<SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>> sortedEntries,
            IReadOnlyDictionary<SerializableWrapper<int>, ulong>? tombstones = null,
            CancellationToken cancellationToken = default)
            => throw new IOException("injected segment-write failure (test).");

        public ISegmentReader<SerializableWrapper<int>, SerializableWrapper<string>> Open(
            string segmentFolder, string segmentFileName)
            => _inner.Open(segmentFolder, segmentFileName);
    }

    /// <summary>
    /// Seals + flushes a throwing-format engine so the flusher captures the write failure and
    /// the engine transitions to the terminal Faulted state. Returns the faulted engine.
    /// </summary>
    private async Task<LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>> FaultedEngineAsync()
    {
        var engine = NewEngine(memTableCapacity: 2, format: new ThrowingSegmentFormat());
        await engine.SetAsync(K(1), V("a"));
        await engine.SetAsync(K(2), V("b"));
        // The next write fills past capacity and seals; FlushAsync drains the seal, the flusher's
        // throwing WriteAsync faults the engine, and FlushAsync surfaces the IOException.
        await engine.SetAsync(K(3), V("c"));
        try
        {
            await engine.FlushAsync();
        }
        catch (IOException)
        {
            // expected — the flush failed and faulted the engine.
        }
        return engine;
    }

    // ---- AC-6.1 — SetBulkAsync calls ThrowIfFaulted (F-25) ----

    [Fact]
    public async Task AC6_1_SetBulkAsync_OnFaultedEngine_Throws()
    {
        using var engine = await FaultedEngineAsync();

        var items = new[]
        {
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(10), V("x")),
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(11), V("y")),
        };

        // A bulk write against a faulted engine must surface the fault (IOException),
        // matching SetAsync's ThrowIfFaulted contract — not silently accept the batch.
        await Assert.ThrowsAsync<IOException>(() => engine.SetBulkAsync(items));
    }

    // ---- AC-6.2 — SetInternalAsync guards null/default key + value (F-26) ----

    [Fact]
    public async Task AC6_2_SetBulkAsync_DefaultKeyInBatch_ThrowsArgumentException()
    {
        using var engine = NewEngine();

        // SerializableWrapper<int> default == new(0) — a default-valued key. Routed through
        // SetBulkAsync → SetInternalAsync it must be rejected with ArgumentException, not stored
        // as a default-keyed tree entry nor surfaced as an NRE.
        var items = new[]
        {
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(1), V("ok")),
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(default!, V("bad")),
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => engine.SetBulkAsync(items));
    }

    [Fact]
    public async Task AC6_2_SetBulkAsync_DefaultValueInBatch_ThrowsArgumentException()
    {
        using var engine = NewEngine();

        var items = new[]
        {
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(1), V("ok")),
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(2), default!),
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => engine.SetBulkAsync(items));
    }

    // ---- AC-6.3 — SetBulkAsync atomicity: documented all-or-nothing, no partial apply (F-24) ----

    [Fact]
    public async Task AC6_3_SetBulkAsync_InvalidItemMidBatch_AppliesNothing()
    {
        using var engine = NewEngine();

        // Pre-seed a known key so we can prove the bulk call did not partially mutate state.
        await engine.SetAsync(K(100), V("seed"));

        // Batch with a valid first item, then a default-key item that fails validation. Under
        // the documented all-or-nothing contract NEITHER item is applied: the batch is fully
        // validated before any apply.
        var items = new[]
        {
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(1), V("first")),
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(default!, V("boom")),
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(2), V("third")),
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => engine.SetBulkAsync(items));

        // No partial apply: the valid-but-pre-fault item K(1) must NOT be present.
        var (_, found1) = await engine.TryGetValueAsync(K(1));
        found1.Should().BeFalse("the batch failed validation, so no item is applied (all-or-nothing).");
        var (_, found2) = await engine.TryGetValueAsync(K(2));
        found2.Should().BeFalse("the post-failure item is also not applied.");

        // The pre-existing seed is untouched.
        var (seed, foundSeed) = await engine.TryGetValueAsync(K(100));
        foundSeed.Should().BeTrue();
        seed.Value.Should().Be("seed");
    }

    [Fact]
    public async Task AC6_3_SetBulkAsync_AllValid_AppliesEveryItem()
    {
        using var engine = NewEngine();

        var items = new[]
        {
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(1), V("one")),
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(2), V("two")),
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(3), V("three")),
        };

        await engine.SetBulkAsync(items);

        foreach (var (k, expected) in new[] { (1, "one"), (2, "two"), (3, "three") })
        {
            var (v, found) = await engine.TryGetValueAsync(K(k));
            found.Should().BeTrue();
            v.Value.Should().Be(expected);
        }
    }

    // ---- AC-6.4 — atomic dispose, first-caller-wins (F-38) ----

    [Fact]
    public async Task AC6_4_ConcurrentDispose_RunsTeardownExactlyOnce()
    {
        var engine = NewEngine();
        await engine.SetAsync(K(1), V("a"));

        // Race many concurrent DisposeAsync callers. First-caller-wins (Interlocked.Exchange)
        // means teardown runs exactly once; a check-then-act flag would let several callers pass
        // the guard and double-run teardown — disposing the SemaphoreSlim / CTS twice surfaces as
        // an ObjectDisposedException escaping one of the racing callers.
        const int racers = 16;
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task[racers];
        for (var i = 0; i < racers; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await go.Task.ConfigureAwait(false);
                await engine.DisposeAsync().ConfigureAwait(false);
            });
        }

        go.SetResult();

        // No racer may throw: a double teardown would dispose already-disposed primitives.
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task AC6_4_SyncAndAsyncDispose_Interleaved_DoNotDoubleTeardown()
    {
        var engine = NewEngine();
        await engine.SetAsync(K(1), V("a"));

        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncTask = Task.Run(async () => { await go.Task.ConfigureAwait(false); engine.Dispose(); });
        var asyncTask = Task.Run(async () => { await go.Task.ConfigureAwait(false); await engine.DisposeAsync().ConfigureAwait(false); });

        go.SetResult();

        // Whichever wins the Interlocked.Exchange runs teardown; the loser is a clean no-op.
        await Task.WhenAll(syncTask, asyncTask);
    }

    // ---- AC-6.5 — GetRangeAsync inverted-range + read-surface key guards (F-39, F-71) ----

    [Fact]
    public async Task AC6_5_GetRangeAsync_InvertedRange_ThrowsArgumentException()
    {
        using var engine = NewEngine();
        await engine.SetBulkAsync(new[]
        {
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(1), V("a")),
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(2), V("b")),
        });

        // startKey > endKey is a caller error — fail fast with ArgumentException rather than
        // silently returning an empty (or, worse, ambiguously-interpreted) range.
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in engine.GetRangeAsync(K(5), K(1)))
            {
            }
        });
    }

    [Fact]
    public async Task AC6_5_GetRangeAsync_NullStartKey_ThrowsArgumentException()
    {
        using var engine = NewEngine();

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in engine.GetRangeAsync(default!, K(5)))
            {
            }
        });
    }

    [Fact]
    public async Task AC6_5_TryGetValueAsync_DefaultKey_ThrowsArgumentException()
    {
        using var engine = NewEngine();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => engine.TryGetValueAsync(default!));
    }

    [Fact]
    public async Task AC6_5_ContainsKeyAsync_DefaultKey_ThrowsArgumentException()
    {
        using var engine = NewEngine();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => engine.ContainsKeyAsync(default!));
    }

    [Fact]
    public async Task AC6_5_ContainsKeyAsync_PresentAndAbsent_ReportsCorrectly()
    {
        using var engine = NewEngine();
        await engine.SetAsync(K(7), V("seven"));

        (await engine.ContainsKeyAsync(K(7))).Should().BeTrue();
        (await engine.ContainsKeyAsync(K(8))).Should().BeFalse();
    }

    [Fact]
    public async Task AC6_5_GetRangeAsync_EqualBounds_ReturnsSingletonWhenPresent()
    {
        using var engine = NewEngine();
        await engine.SetBulkAsync(new[]
        {
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(1), V("a")),
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(2), V("b")),
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(3), V("c")),
        });

        var results = new List<int>();
        await foreach (var (k, _) in engine.GetRangeAsync(K(2), K(2)))
        {
            results.Add(k.Value);
        }

        results.Should().Equal(2);
    }
}
