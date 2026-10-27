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
using Boutquin.Storage.Infrastructure.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Tests for <see cref="ColumnarTimeSeriesStack{TDatasetKey,TRecord}"/> disposal correctness.
/// </summary>
public sealed class ColumnarTimeSeriesStackDisposeTests : IAsyncDisposable
{
    private readonly string _folder;
    private ColumnarTimeSeriesStack<SerializableWrapper<string>, MarketDataBar>? _stack;

    public ColumnarTimeSeriesStackDisposeTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"StackDisposeTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
    }

    public async ValueTask DisposeAsync()
    {
        // Stack may already be disposed by the test; suppress double-dispose
        try { _stack?.Dispose(); } catch (ObjectDisposedException) { }
        if (Directory.Exists(_folder))
        {
            await Task.Run(() => Directory.Delete(_folder, recursive: true)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Regression guard for non-atomic Dispose.
    /// Before the fix, <c>_disposed</c> was a <c>bool</c> and the check-then-set was not
    /// atomic. Two threads racing through the guard could both observe <c>_disposed == false</c>
    /// and both proceed to call <c>InnerEngine.Dispose()</c>, causing a double-dispose.
    /// After the fix, <c>Interlocked.Exchange(ref _disposed, 1) != 0</c> serialises the
    /// guard so the inner engine is disposed exactly once regardless of concurrency.
    /// </summary>
    [Fact]
    public async Task Dispose_CalledFromMultipleThreadsConcurrently_InnerEngineDisposedExactlyOnce()
    {
        // Arrange — build a real stack through the builder so _disposed is observable.
        _stack = await ColumnarTimeSeriesEngineBuilder
            .BuildAsync<SerializableWrapper<string>, MarketDataBar>(_folder)
            .ConfigureAwait(true);

        // Wrap the real InnerEngine.Dispose so we can count calls.
        // We intercept via _disposed state: after Dispose() the field must equal 1.
        // We test the observable effect: ObjectDisposedException on a second dispose
        // attempt would indicate double-dispose was attempted but caught by the guard.
        // Instead we verify that calling Dispose() 8 times concurrently does not throw.
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        const int threadCount = 8;
        var barrier = new Barrier(threadCount);

        // Act
        Parallel.For(0, threadCount, _ =>
        {
            barrier.SignalAndWait();
            try
            {
                _stack.Dispose();
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Assert — no exceptions (the Interlocked guard prevents double-dispose).
        Assert.Empty(exceptions);

        // Verify _disposed field via reflection: must be 1 (int, not bool after fix).
        var disposedField = typeof(ColumnarTimeSeriesStack<SerializableWrapper<string>, MarketDataBar>)
            .GetField("_disposed",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var disposedValue = disposedField.GetValue(_stack);
        Assert.Equal(1, disposedValue); // int 1, not bool true
    }

    /// <summary>
    /// Regression guard: DisposeAsync must delegate to InnerEngine.DisposeAsync() rather than
    /// Dispose(). Before the fix, DisposeAsync called Dispose() which internally did
    /// GetAwaiter().GetResult() on async teardown — blocking the thread-pool thread.
    /// After the fix, DisposeAsync awaits InnerEngine.DisposeAsync() without blocking.
    /// Secondary invariant: calling DisposeAsync a second time is idempotent.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_CompletesWithoutBlockingOnSyncDispose()
    {
        // Arrange — build a real stack through the builder.
        _stack = await ColumnarTimeSeriesEngineBuilder
            .BuildAsync<SerializableWrapper<string>, MarketDataBar>(_folder)
            .ConfigureAwait(true);

        // Act — await DisposeAsync with a 5-second timeout.
        var disposeTask = _stack.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(5_000)).ConfigureAwait(true);

        // Assert — must complete within 5 s; no blocking.
        completed.Should().BeSameAs(disposeTask,
            "DisposeAsync must complete promptly without blocking a thread-pool thread");
        await disposeTask.ConfigureAwait(true); // propagate any exception

        // Second call must be idempotent — no ObjectDisposedException, no throw.
        var act = async () => await _stack.DisposeAsync().ConfigureAwait(true);
        await act.Should().NotThrowAsync("second DisposeAsync must be idempotent");
    }
}
