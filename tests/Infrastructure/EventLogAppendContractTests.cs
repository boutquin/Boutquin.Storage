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
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.Streaming.State;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// The acknowledgement contract of <see cref="AppendOnlyEventLog{TEvent}"/>: every position
/// <c>AppendAsync</c> returns must stay readable through <c>ReadFromAsync</c> for the life of the
/// file, an append that cannot be made durable must surface its failure rather than hang, and no
/// reader may observe a frame before its append has been acknowledged.
/// </summary>
public sealed class EventLogAppendContractTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eventlog-append-contract-").FullName;

    private string NewPath() => Path.Combine(_dir, $"{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static async Task<List<(long Position, string? Event)>> ReadAllAsync(
        AppendOnlyEventLog<string?> log, long from = 0)
    {
        var result = new List<(long, string?)>();
        await foreach (var entry in log.ReadFromAsync(from).ConfigureAwait(true))
        {
            result.Add(entry);
        }

        return result;
    }

    [Fact]
    public async Task AppendAsync_NullEvent_IsRejectedBeforeAnyPositionIsAcknowledged()
    {
        var path = NewPath();
        using var log = await AppendOnlyEventLog<string?>.CreateAsync(path).ConfigureAwait(true);
        await log.AppendAsync("e0").ConfigureAwait(true);
        var lengthBefore = new FileInfo(path).Length;

        var appendNull = async () => await log.AppendAsync(null).ConfigureAwait(true);

        await appendNull.Should().ThrowAsync<ArgumentNullException>(
            "the reader can never return a null event, so acknowledging one would hand out a position no replay can pass")
            .ConfigureAwait(true);
        new FileInfo(path).Length.Should().Be(lengthBefore, "a rejected append writes nothing");
    }

    [Fact]
    public async Task ReplayFromZero_AfterRejectedNullAppend_ReturnsGapFreeHistory_AcrossReopen()
    {
        var path = NewPath();
        using (var log = await AppendOnlyEventLog<string?>.CreateAsync(path).ConfigureAwait(true))
        {
            await log.AppendAsync("e0").ConfigureAwait(true);
            try
            {
                await log.AppendAsync(null).ConfigureAwait(true);
            }
            catch (ArgumentNullException)
            {
                // Expected once the boundary guard exists; the replay below is the property.
            }

            await log.AppendAsync("e2").ConfigureAwait(true);
        }

        using var reopened = await AppendOnlyEventLog<string?>.CreateAsync(path).ConfigureAwait(true);
        var readAll = async () => await ReadAllAsync(reopened).ConfigureAwait(true);
        var replay = (await readAll.Should().NotThrowAsync(
            "no acknowledged frame may poison replay from an earlier position").ConfigureAwait(true)).Subject;

        replay.Should().Equal(new List<(long, string?)> { (0, "e0"), (1, "e2") },
            "every acknowledged position must replay from 0, gap-free, after a reopen");
    }

    [Fact]
    public async Task AppendAsync_WhenLockFileCannotBeOpened_SurfacesTheIOFault_InsteadOfRetryingForever()
    {
        var logDir = Path.Combine(_dir, "vanishing");
        Directory.CreateDirectory(logDir);
        var path = Path.Combine(logDir, "events.log");
        using (var seed = await AppendOnlyEventLog<string?>.CreateAsync(path).ConfigureAwait(true))
        {
            await seed.AppendAsync("e0").ConfigureAwait(true);
        }
        using var log = await AppendOnlyEventLog<string?>.CreateAsync(path).ConfigureAwait(true);

        // The sidecar lock now lives in a directory that no longer exists: a non-contention I/O
        // fault no amount of waiting can clear. Reopen without appending first: startup releases
        // its recovery handle, whereas an active writer prevents parent deletion on Windows.
        // The token only bounds the test if the append spins.
        Directory.Delete(logDir, recursive: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var append = async () => await log.AppendAsync("e1", cts.Token).ConfigureAwait(true);

        await append.Should().ThrowAsync<DirectoryNotFoundException>(
            "only a lock held by another handle is worth retrying; any other I/O fault must reach the caller")
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task AppendAsync_WhileAnotherHandleHoldsTheLock_WaitsAndThenCompletes()
    {
        var path = NewPath();
        using var log = await AppendOnlyEventLog<string?>.CreateAsync(path).ConfigureAwait(true);
        await log.AppendAsync("e0").ConfigureAwait(true);

        Task<long> pending;
        using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            pending = log.AppendAsync("e1").AsTask();
            var winner = await Task.WhenAny(pending, Task.Delay(300)).ConfigureAwait(true);
            winner.Should().NotBeSameAs(pending, "an append must wait while another handle holds the append lock");
        }

        (await pending.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true)).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentReader_NeverObservesAFrameWhoseAppendIsRolledBack()
    {
        var path = NewPath();
        using var log = await AppendOnlyEventLog<string?>.CreateAsync(path).ConfigureAwait(true);
        await log.AppendAsync("e0").ConfigureAwait(true);

        // Reproduce a failed fsync: the frame's bytes reach the OS (visible to any reader of the
        // file) before durability fails, and a reader runs inside that window.
        Task<List<(long Position, string? Event)>>? reader = null;
        log._frameWriteFaultHook = async (stream, frame, ct) =>
        {
            await stream.WriteAsync(frame, ct).ConfigureAwait(true);
            ((FileStream)stream).Flush(flushToDisk: false);
            reader = Task.Run(() => ReadAllAsync(log));
            await Task.WhenAny(reader, Task.Delay(500, ct)).ConfigureAwait(true);
            throw new IOException("Injected fsync failure after the write reached the OS.");
        };

        var doomed = async () => await log.AppendAsync("A-never-acknowledged").ConfigureAwait(true);
        await doomed.Should().ThrowAsync<IOException>().ConfigureAwait(true);
        log._frameWriteFaultHook = null;

        var observed = await reader!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        observed.Should().Equal(new List<(long, string?)> { (0, "e0") },
            "a reader may only see acknowledged frames — the rolled-back position is reassigned next");

        (await log.AppendAsync("B").ConfigureAwait(true)).Should().Be(1);
        (await ReadAllAsync(log).ConfigureAwait(true)).Should().Equal(
            new List<(long, string?)> { (0, "e0"), (1, "B") });
    }

    /// <summary>
    /// A crashed writer left a torn tail (the file grew, its bytes never landed). A reader's
    /// snapshot taken under the append lock must still end at the last acknowledged frame: a live
    /// writer trims that tail and writes its next frame at the old end, and if that append then
    /// fails its fsync and rolls back, the reader must not have yielded it — the position is
    /// reassigned to a different event. The log is large enough that the tail lies outside the
    /// reader's first buffered read.
    /// </summary>
    [Fact]
    public async Task ConcurrentReader_WhoseSnapshotSpansACrashedWritersTornTail_NeverObservesAnUnacknowledgedFrame()
    {
        const int Committed = 1000;
        var path = NewPath();
        using (var image = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            for (var i = 0; i < Committed; i++)
            {
                var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes<string?>($"e{i}-{new string('p', 100)}");
                image.Write(EventLogFrames.Encode(i, payload));
            }
        }

        using var reader = await AppendOnlyEventLog<string?>.CreateAsync(path).ConfigureAwait(true);
        using var writer = await AppendOnlyEventLog<string?>.CreateAsync(path).ConfigureAwait(true);
        using (var crashed = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            crashed.Write(new byte[200]);
        }

        var observed = new List<(long Position, string? Event)>();
        var enumerator = reader.ReadFromAsync(0).GetAsyncEnumerator();
        try
        {
            (await enumerator.MoveNextAsync().ConfigureAwait(true)).Should().BeTrue();
            observed.Add(enumerator.Current);

            // The writer's frame reaches the OS, the reader runs to its snapshot end inside that
            // window, and then the durable flush fails.
            writer._frameWriteFaultHook = async (stream, frame, ct) =>
            {
                await stream.WriteAsync(frame, ct).ConfigureAwait(true);
                ((FileStream)stream).Flush(flushToDisk: false);
                while (await enumerator.MoveNextAsync().ConfigureAwait(true))
                {
                    observed.Add(enumerator.Current);
                }

                throw new IOException("Injected fsync failure after the write reached the OS.");
            };

            var doomed = async () => await writer.AppendAsync("x-never-acknowledged").ConfigureAwait(true);
            await doomed.Should().ThrowAsync<IOException>().ConfigureAwait(true);
        }
        finally
        {
            writer._frameWriteFaultHook = null;
            await enumerator.DisposeAsync().ConfigureAwait(true);
        }

        observed.Select(e => e.Position).Should().Equal(Enumerable.Range(0, Committed).Select(i => (long)i),
            "the reader may only yield acknowledged frames; the rolled-back frame's position is reassigned next");
        (await writer.AppendAsync("y").ConfigureAwait(true)).Should().Be(Committed);
    }
}

/// <summary>
/// Point-in-time contract of <see cref="InMemoryStateStore{TKey, TValue}.SnapshotAsync"/>: a snapshot
/// taken while writers run must equal a state the table actually held. (Hosted here because it is
/// the batch's only permitted new test file; it belongs beside the streaming state-store tests.)
/// </summary>
public sealed class InMemoryStateStoreSnapshotTests
{
    [Fact]
    public async Task SnapshotAsync_WithAWriterInterleavedMidCopy_ReturnsAStateTheTableHeld()
    {
        var store = new InMemoryStateStore<HookKey, int>();
        var a = new HookKey("A");
        var b = new HookKey("B");
        await store.SetAsync(a, 1).ConfigureAwait(true);
        await store.SetAsync(b, 1).ConfigureAwait(true);

        // The first key hash computed during the snapshot fires one writer that updates that key
        // and then the other: the table only ever holds (first=1, other=1) -> (2, 1) -> (2, 2).
        HookKey? first = null;
        HookKey.OnHash = key =>
        {
            first = key;
            var other = ReferenceEquals(key, a) ? b : a;
            store.SetAsync(key, 2).AsTask().GetAwaiter().GetResult();
            store.SetAsync(other, 2).AsTask().GetAwaiter().GetResult();
        };

        IReadOnlyDictionary<HookKey, int> snapshot;
        try
        {
            snapshot = await store.SnapshotAsync().ConfigureAwait(true);
        }
        finally
        {
            HookKey.OnHash = null;
        }

        first.Should().NotBeNull("the interleaving hook must have fired during the snapshot");
        var otherKey = ReferenceEquals(first, a) ? b : a;
        var observed = (snapshot[first!], snapshot[otherKey]);
        observed.Should().BeOneOf(new[] { (1, 1), (2, 1), (2, 2) },
            "a point-in-time snapshot must equal a state the table actually held");
    }

    internal sealed class HookKey(string name) : IEquatable<HookKey>
    {
        private static Action<HookKey>? s_onHash;

        public static Action<HookKey>? OnHash
        {
            get => Volatile.Read(ref s_onHash);
            set => Volatile.Write(ref s_onHash, value);
        }

        public string Name { get; } = name;

        public bool Equals(HookKey? other) => other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as HookKey);

        public override int GetHashCode()
        {
            // One-shot: disarm before the writer runs, since its own SetAsync hashes keys too.
            Interlocked.Exchange(ref s_onHash, null)?.Invoke(this);
            return StringComparer.Ordinal.GetHashCode(Name);
        }

        public override string ToString() => Name;
    }
}
