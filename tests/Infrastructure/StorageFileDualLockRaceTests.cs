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
using System.Collections.Concurrent;
using System.Reflection;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// C03 — STORAGEFILE-DUAL-LOCK-001. <see cref="StorageFile"/>'s <c>_fileStream</c> field was
/// mutated under TWO different locks: an instance <c>_semaphore</c> in <c>Open</c>/<c>Close</c>,
/// and the static per-path semaphore in <c>Create</c>/<c>WriteAllBytes…</c>/<c>Dispose</c>/etc.
/// The doc claimed the instance semaphore guarded <c>_fileStream</c>, but it did not guard the
/// dominant mutation paths, so the two paths could race the same stream (a just-opened stream
/// disposed out from under its user).
///
/// <para>The single-lock invariant is pinned deterministically: hold the per-path semaphore
/// externally, then assert that <c>Open</c> BLOCKS on it (proving <c>Open</c> now serialises
/// against every other <c>_fileStream</c> mutator). On the unfixed code <c>Open</c> took a
/// DIFFERENT lock, so it would complete immediately while the per-path semaphore was held — that
/// is the race. A concurrency stress race on this defect is inherently flaky in both directions;
/// this characterization makes the invariant a deterministic, load-bearing assertion.</para>
/// </summary>
public sealed class StorageFileDualLockRaceTests
{
    private static SemaphoreSlim PerPathSemaphore(StorageFile sf)
    {
        var field = typeof(StorageFile).GetField(
            "s_fileSemaphores", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dict = (ConcurrentDictionary<string, SemaphoreSlim>)field.GetValue(null)!;
        return dict.GetOrAdd(sf.FilePath, _ => new SemaphoreSlim(1, 1));
    }

    [Fact]
    public async Task Open_SerialisesAgainstThePerPathSemaphore_TheSingleStreamGuard()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"StorageDualLock_{Guid.NewGuid():N}");
        try
        {
            using var sf = new StorageFile(dir, "dual-lock.bin");

            // Hold the per-path semaphore — the lock that guards _fileStream for Create /
            // WriteAllBytes / Dispose. While it is held, NO other _fileStream mutation may run.
            var perPath = PerPathSemaphore(sf);
            await perPath.WaitAsync().ConfigureAwait(true);
            try
            {
                // Open must now block: it must acquire the SAME per-path semaphore. On the unfixed
                // dual-lock code Open took the instance semaphore instead, so this Task completed
                // immediately even though the per-path lock was held — the defect this test pins.
                var openTask = Task.Run(() => sf.Open(FileMode.OpenOrCreate));
                var finishedWhileLockHeld = await Task.WhenAny(
                    openTask, Task.Delay(TimeSpan.FromMilliseconds(500))).ConfigureAwait(true) == openTask;

                finishedWhileLockHeld.Should().BeFalse(
                    "Open must acquire the per-path semaphore that guards _fileStream — if it " +
                    "completes while that lock is held, it is using a DIFFERENT lock and racing " +
                    "the stream against Create/WriteAllBytes/Dispose");

                // Release the lock: Open must now complete promptly using that very lock.
                perPath.Release();
                var stream = await openTask.ConfigureAwait(true);
                stream.Should().NotBeNull("Open proceeds once the per-path semaphore is free");
            }
            catch
            {
                // Ensure the held lock is released on any assertion failure so cleanup can run.
                if (perPath.CurrentCount == 0)
                {
                    perPath.Release();
                }
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
