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
#nullable disable

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// A key durably present in the engine must never read as not-found because a concurrent
/// <c>CompactAsync</c> deleted the segment files a reader was about to probe. DDIA 2e Ch. 4
/// prescribes that merged input segments keep serving reads until the merge output replaces
/// them — a reader that captured pre-compaction segment references must not observe a silent
/// false negative for live data. The engine's <c>ReadSnapshotIterateHook</c> seam parks the
/// reader inside the exact window to make the interleaving deterministic.
/// </summary>
public sealed class LogSegmentedConcurrentReadTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"LogSegConcurrentRead_{Guid.NewGuid():N}");

    public LogSegmentedConcurrentReadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* test-runner cleanup — ignore */ }
        }
    }

    private LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> NewEngine()
    {
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        return new LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            serializer,
            _dir,
            "seg",
            maxSegmentSize: 256, // small segments so the hot key's segment is not the current one
            (location, name, ser, _) => new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
                new StorageFile(location, name), ser));
    }

    [Fact]
    public async Task TryGetValue_StraddlingACompaction_NeverReturnsFalseNegativeForLiveKey()
    {
        var engine = NewEngine();
        var hotKey = new SerializableWrapper<int>(1);
        var hotValue = new SerializableWrapper<string>("hot-value");

        // The hot key lands in the oldest segment; fillers roll several more segments.
        await engine.SetAsync(hotKey, hotValue);
        for (var i = 10; i < 30; i++)
        {
            await engine.SetAsync(
                new SerializableWrapper<int>(i), new SerializableWrapper<string>(new string('x', 40)));
        }

        // Park one read inside the snapshot→probe window (one-shot hook), run a full
        // compaction while it is parked, then let the read proceed.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fired = 0;
        engine.ReadSnapshotIterateHook = async () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
            {
                parked.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }
        };

        var readTask = engine.TryGetValueAsync(hotKey);

        // Two engine shapes complete this deterministically:
        // — snapshot-then-iterate-outside-the-lock: the parked read holds no lock, so the
        //   compaction runs to completion FIRST (WhenAny returns via compact), and only then
        //   does the read resume against its stale snapshot;
        // — reads-serialised-under-the-lock: the parked read holds the engine lock, the
        //   compaction queues behind it (WhenAny returns via the grace delay), the read
        //   resumes against intact segments, and the compaction follows.
        var compactTask = Task.Run(async () =>
        {
            await parked.Task.ConfigureAwait(false);
            var compact = engine.CompactAsync();
            await Task.WhenAny(compact, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            release.TrySetResult();
            await compact.ConfigureAwait(false);
        });

        var (value, found) = await readTask;
        await compactTask;
        engine.ReadSnapshotIterateHook = null;

        found.Should().BeTrue("the hot key is durably present; a compaction must not make it unreadable");
        value.Value.Should().Be("hot-value");
    }
}
