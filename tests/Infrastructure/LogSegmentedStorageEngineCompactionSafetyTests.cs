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
/// Compaction must never destroy its inputs before its output is published. A compaction that is
/// cancelled, or that faults part-way through rewriting, has to leave every acknowledged key
/// readable with its latest value — the input segments stay authoritative until the replacement
/// segment set is complete (DDIA 2e Ch. 4: merged input segments keep serving reads until the
/// merge output replaces them).
/// </summary>
public sealed class LogSegmentedStorageEngineCompactionSafetyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"CompactionSafety_{Guid.NewGuid():N}");

    /// <summary>
    /// A segment engine that runs a caller-supplied interruption after each successful append
    /// once armed — cancelling the compaction's token, or throwing an I/O fault — so the
    /// interruption lands exactly mid-rewrite.
    /// </summary>
    private sealed class InterruptingSegment(
        IStorageFile file,
        IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>> serializer,
        Func<bool> interrupt)
        : AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(file, serializer)
    {
        public override async Task SetAsync(SerializableWrapper<int> key, SerializableWrapper<string> value, CancellationToken cancellationToken = default)
        {
            await base.SetAsync(key, value, cancellationToken).ConfigureAwait(true);
            if (interrupt())
            {
                throw new IOException("injected write fault");
            }
        }
    }

    private LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> NewEngine(
        long maxSegmentSize, Func<bool> interrupt)
    {
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        return new LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            serializer, _folder, "seg", maxSegmentSize,
            (location, name, s, _) => new InterruptingSegment(new StorageFile(location, name), s, interrupt));
    }

    private static async Task<int> CountReadableAsync(
        LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> engine, int keys)
    {
        var found = 0;
        for (var i = 1; i <= keys; i++)
        {
            var (value, ok) = await engine.TryGetValueAsync(new SerializableWrapper<int>(i)).ConfigureAwait(true);
            if (ok && value.Value == "v" + i)
            {
                found++;
            }
        }

        return found;
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 1048576)]
    [InlineData(true, 1)]
    [InlineData(true, 1048576)]
    public async Task SetBulkAsync_InterruptedAfterAnAppend_PreservesOnlyPreviouslyAcknowledgedValues(bool cancel, long segmentSize)
    {
        using var cancellation = new CancellationTokenSource();
        var armed = false;
        var engine = NewEngine(segmentSize, () =>
        {
            if (!armed)
            {
                return false;
            }
            if (cancel)
            {
                cancellation.Cancel();
                return false;
            }
            throw new IOException("injected batch append failure");
        });
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("old")).ConfigureAwait(true);
        armed = true;

        var batch = new[]
        {
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(new(1), new("new")),
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(new(2), new("second")),
        };
        var failure = await Record.ExceptionAsync(() => engine.SetBulkAsync(batch, cancellation.Token)).ConfigureAwait(true);
        if (cancel)
        {
            failure.Should().BeOfType<OperationCanceledException>();
        }
        else
        {
            failure.Should().BeOfType<IOException>();
        }
        armed = false;
        var old = await engine.TryGetValueAsync(new SerializableWrapper<int>(1)).ConfigureAwait(true);
        old.Found.Should().BeTrue();
        old.Value.Value.Should().Be("old", "a failed batch cannot publish its overwrite prefix");
        (await engine.TryGetValueAsync(new SerializableWrapper<int>(2)).ConfigureAwait(true)).Found.Should().BeFalse();
        (await engine.GetAllItemsAsync().ConfigureAwait(true)).Should().HaveCount(1);

        await engine.SetBulkAsync(batch).ConfigureAwait(true);
        (await engine.TryGetValueAsync(new SerializableWrapper<int>(1)).ConfigureAwait(true)).Value.Value.Should().Be("new");
        (await engine.TryGetValueAsync(new SerializableWrapper<int>(2)).ConfigureAwait(true)).Value.Value.Should().Be("second");
        await engine.CompactAsync().ConfigureAwait(true);
        (await engine.GetAllItemsAsync().ConfigureAwait(true)).Should().HaveCount(2);
    }

    /// <summary>
    /// The caller's token is cancelled after the first rewritten entry. Every one of the five
    /// acknowledged keys must still be readable afterwards.
    /// </summary>
    [Fact]
    public async Task CompactAsync_CancelledMidRewrite_KeepsEveryAcknowledgedKey()
    {
        using var cts = new CancellationTokenSource();
        var armed = false;
        var engine = NewEngine(1 << 20, () =>
        {
            if (armed)
            {
                cts.Cancel();
            }

            return false;
        });
        for (var i = 1; i <= 5; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>("v" + i)).ConfigureAwait(true);
        }

        armed = true;
        var act = () => engine.CompactAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
        armed = false;

        (await CountReadableAsync(engine, 5).ConfigureAwait(true)).Should().Be(5, "a cancelled compaction must not lose acknowledged keys");
        (await engine.GetAllItemsAsync().ConfigureAwait(true)).Should().HaveCount(5);
    }

    /// <summary>
    /// The second rewritten append faults with an I/O error. The failed compaction must leave
    /// the input segments authoritative: every acknowledged key keeps its latest value.
    /// </summary>
    [Fact]
    public async Task CompactAsync_FaultMidRewrite_KeepsEveryAcknowledgedKey()
    {
        var armed = false;
        var appendsAfterArm = 0;
        var engine = NewEngine(1 << 20, () => armed && ++appendsAfterArm == 2);
        for (var i = 1; i <= 5; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>("stale" + i)).ConfigureAwait(true);
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>("v" + i)).ConfigureAwait(true);
        }

        armed = true;
        var act = () => engine.CompactAsync();
        await act.Should().ThrowAsync<IOException>().ConfigureAwait(true);
        armed = false;

        (await CountReadableAsync(engine, 5).ConfigureAwait(true)).Should().Be(5, "a failed compaction must not lose acknowledged keys");
        // The aborted output must not be left behind as extra live segment data.
        (await engine.GetAllItemsAsync().ConfigureAwait(true)).Should().HaveCount(10);
    }

    /// <summary>
    /// The small-segment merge must also publish before it deletes. Stack (oldest→newest):
    /// small A holding key 1, full B, small C holding key 2. Merging run A completes (its source
    /// used to be deleted right there), then the merge of run C is cancelled. Key 1 lives only in
    /// A and must remain readable.
    /// </summary>
    [Fact]
    public async Task MergeSmallSegments_CancelledInLaterRun_KeepsEarlierRunKeys()
    {
        const long maxSize = 200L;
        using var cts = new CancellationTokenSource();
        var armed = false;
        var appendsAfterArm = 0;
        var engine = NewEngine(maxSize, () =>
        {
            if (armed && ++appendsAfterArm == 2)
            {
                cts.Cancel();
            }

            return false;
        });
        var serializer = engine.EntrySerializer;
        var a = new InterruptingSegment(new StorageFile(_folder, "a.log"), serializer, () => false);
        var b = new InterruptingSegment(new StorageFile(_folder, "b.log"), serializer, () => false);
        var c = new InterruptingSegment(new StorageFile(_folder, "c.log"), serializer, () => false);
        await a.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v1")).ConfigureAwait(true);
        await b.SetAsync(new SerializableWrapper<int>(9), new SerializableWrapper<string>(new string('x', (int)maxSize))).ConfigureAwait(true);
        await c.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("v2")).ConfigureAwait(true);

        var engineType = engine.GetType();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var stack = (Stack<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>)
            engineType.GetField("_segments", flags)!.GetValue(engine)!;
        stack.Clear();
        stack.Push(a);
        stack.Push(b);
        stack.Push(c);
        engineType.GetField("_currentSegment", flags)!.SetValue(engine, c);

        armed = true;
        var merge = (Task)engineType.GetMethod("MergeSmallSegmentsAsync", flags)!.Invoke(engine, [cts.Token])!;
        var act = () => merge;
        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
        armed = false;

        (await CountReadableAsync(engine, 2).ConfigureAwait(true)).Should().Be(2, "a cancelled merge must not lose keys of an already-merged run");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }
}
