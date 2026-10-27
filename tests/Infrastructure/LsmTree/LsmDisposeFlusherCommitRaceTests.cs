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
/// Covers disposal racing with the background flusher after it has acquired the commit lock.
/// Disposal must be able to drain the flusher and reacquire that lock during shutdown.
/// </summary>
public sealed class LsmDisposeFlusherCommitRaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"LsmDisposeFlusherRace_{Guid.NewGuid():N}");

    public LsmDisposeFlusherCommitRaceTests()
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_RacingFlusherCommit_ReleasesEngineLock(bool useAsyncDisposal)
    {
        var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 1,
            segmentFolder: _dir,
            segmentPrefix: "seg",
            maxImmutableMemtables: 2);
        var commitLockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        engine.FlusherCommitLockHook = async cancellationToken =>
        {
            using var registration = cancellationToken.Register(
                () => disposalCancellationObserved.TrySetResult());
            commitLockAcquired.TrySetResult();
            await releaseCommit.Task.ConfigureAwait(true);
        };

        try
        {
            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("one"));
            await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("two"));
            await commitLockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var disposeTask = useAsyncDisposal
                ? Task.Run(async () => await engine.DisposeAsync().ConfigureAwait(true))
                : Task.Run(engine.Dispose);
            await disposalCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseCommit.TrySetResult();

            await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseCommit.TrySetResult();
        }
    }
}
