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
using Boutquin.Storage.Domain.ValueObjects.LsmTree;

namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// P4.3 — Tests for <see cref="LsmStorageEngine{TKey,TValue}.OpenAsync"/>.
/// Verifies the async factory: normal construction, IOException-to-InvalidDataException
/// wrapping, and disposal of partially-initialised state on failure.
/// </summary>
public sealed class LsmStorageEngineOpenAsyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmOpenAsync_{Guid.NewGuid():N}");

    public LsmStorageEngineOpenAsyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }

    private LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>> DefaultOptions(
        IWriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>? wal = null)
        => new(
            MemTableCapacity: 10,
            SegmentFolder: _dir,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            WriteAheadLog: wal,
            CompactionStrategy: null,
            ValueLog: null);

    private static SerializableWrapper<int> K(int i) => new(i);
    private static SerializableWrapper<string> V(string s) => new(s);

    [Fact]
    public async Task OpenAsync_ValidOptions_ReturnsWorkingEngine()
    {
        // Act
        using var engine = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
            .OpenAsync(DefaultOptions());

        // Assert: engine is functional
        await engine.SetAsync(K(1), V("hello"));
        (SerializableWrapper<string> val, var found) = await engine.TryGetValueAsync(K(1));
        found.Should().BeTrue();
        val.Value.Should().Be("hello");
    }

    [Fact]
    public async Task OpenAsync_WhenWalThrowsIOException_ThrowsInvalidDataException()
    {
        // Arrange: a WAL whose RecoverAsync blows up with IOException
        var faultyWal = new FaultyWal(new IOException("simulated WAL corruption"));
        var options = DefaultOptions(wal: faultyWal);

        // Act & Assert
        var act = async () => await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
            .OpenAsync(options);
        (await act.Should().ThrowAsync<InvalidDataException>())
            .WithInnerExceptionExactly<IOException>();
    }

    [Fact]
    public async Task OpenAsync_WhenWalThrowsNonIoException_PropagatesDirectly()
    {
        // Arrange: a WAL that throws a non-I/O exception
        var faultyWal = new FaultyWal(new InvalidOperationException("unexpected failure"));
        var options = DefaultOptions(wal: faultyWal);

        // Act & Assert: non-IO exceptions flow through without wrapping
        var act = async () => await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
            .OpenAsync(options);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task OpenAsync_NullOptions_ThrowsArgumentNullException()
    {
        var act = async () => await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
            .OpenAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Constructor option-validation paths ──────────────────────────────────

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Action act = () => _ = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            (LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_MemTableCapacityZero_Throws()
    {
        var opts = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 0,
            SegmentFolder: _dir,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            CompactionStrategy: null,
            ValueLog: null);
        Action act = () => _ = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(opts);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_MemTableCapacityNegative_Throws()
    {
        var opts = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: -1,
            SegmentFolder: _dir,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            CompactionStrategy: null,
            ValueLog: null);
        Action act = () => _ = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(opts);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_NonExistentSegmentFolder_CreatesIt()
    {
        var subdir = Path.Combine(_dir, "newly_created");
        var opts = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 4,
            SegmentFolder: subdir,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            CompactionStrategy: null,
            ValueLog: null);

        Directory.Exists(subdir).Should().BeFalse();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(opts);
        Directory.Exists(subdir).Should().BeTrue();
    }

    [Fact]
    public async Task SegmentCount_ReflectsFlushedSegments()
    {
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(DefaultOptions());
        engine.SegmentCount.Should().Be(0);

        await engine.SetAsync(K(1), V("a"));
        await engine.FlushAsync();
        engine.SegmentCount.Should().Be(1);

        await engine.SetAsync(K(2), V("b"));
        await engine.FlushAsync();
        engine.SegmentCount.Should().Be(2);
    }

    [Fact]
    public async Task RecoverFromDisk_ReopensExistingSegments()
    {
        var opts = DefaultOptions();
        // Write and flush in one engine, dispose, then open another over the same folder.
        {
            using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(opts);
            await engine.SetAsync(K(1), V("alpha"));
            await engine.SetAsync(K(2), V("beta"));
            await engine.FlushAsync();
            await engine.SetAsync(K(3), V("gamma"));
            await engine.FlushAsync();
        }

        // New engine should pick up two flushed segments and serve all keys.
        using var reopened = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(opts);
        reopened.SegmentCount.Should().BeGreaterThanOrEqualTo(1);
        var (v1, f1) = await reopened.TryGetValueAsync(K(1));
        f1.Should().BeTrue();
        v1.Value.Should().Be("alpha");
        var (v3, f3) = await reopened.TryGetValueAsync(K(3));
        f3.Should().BeTrue();
        v3.Value.Should().Be("gamma");
    }

    [Fact]
    public async Task TryGetValueAsync_KeyAbsent_ReturnsFalse()
    {
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(DefaultOptions());
        await engine.SetAsync(K(1), V("a"));
        await engine.FlushAsync();

        var (_, found) = await engine.TryGetValueAsync(K(999));
        found.Should().BeFalse();
    }

    [Fact]
    public async Task TryGetValueAsync_AfterDispose_Throws()
    {
        var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(DefaultOptions());
        await engine.SetAsync(K(1), V("a"));
        engine.Dispose();

        Func<Task> act = async () => await engine.TryGetValueAsync(K(1));
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task SetAsync_AfterDispose_Throws()
    {
        var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(DefaultOptions());
        engine.Dispose();

        Func<Task> act = async () => await engine.SetAsync(K(1), V("a"));
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task FlushAsync_OnEmptyMemTable_NoOps()
    {
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(DefaultOptions());
        engine.SegmentCount.Should().Be(0);
        await engine.FlushAsync();
        engine.SegmentCount.Should().Be(0);
    }

    [Fact]
    public async Task RemoveAsync_CallsArrive_TombstoneVisible()
    {
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(DefaultOptions());
        await engine.SetAsync(K(1), V("a"));
        await engine.FlushAsync();

        await engine.RemoveAsync(K(1));
        var (_, found) = await engine.TryGetValueAsync(K(1));
        found.Should().BeFalse("tombstone in memtable masks the older flushed value");
    }

    [Fact]
    public async Task ConstructorBuiltEngine_FirstReadIsProjectedRangeSnapshot_ServesWalRecoveredWrites()
    {
        var walDir = Path.Combine(_dir, "wal");
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir))
        using (var writer = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(DefaultOptions(wal)))
        {
            await writer.SetAsync(K(1), V("acked-1"));
            await writer.SetAsync(K(2), V("acked-2"));
        }

        // Nothing was flushed: the WAL is the only durable copy of both acknowledged writes.
        Directory.GetFiles(_dir, "seg_*.sst").Should().BeEmpty();

        using var reopenedWal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(DefaultOptions(reopenedWal));

        // The projected-range snapshot is the first operation on the constructor-built engine — the
        // read the columnar decorator issues for a column-subset range read.
        var snapshot = await engine.AcquireProjectedRangeSnapshotAsync(K(0), K(100));

        snapshot.MemtableEntries.Select(e => e.Key.Value).Should().BeEquivalentTo(
            [1, 2], "an engine reopened through the constructor serves every acknowledged WAL write on every read path");
    }

    [Fact]
    public async Task SetAsync_OverwriteSameKey_ReturnsLatestValue()
    {
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(DefaultOptions());
        await engine.SetAsync(K(1), V("v1"));
        await engine.FlushAsync();
        await engine.SetAsync(K(1), V("v2"));
        await engine.FlushAsync();

        var (v, found) = await engine.TryGetValueAsync(K(1));
        found.Should().BeTrue();
        v.Value.Should().Be("v2");
    }

    // ── Fake WAL ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Dispose_AfterCancelledFirstCall_DrainsSharedRecovery(bool clearPending, bool asyncDispose)
    {
        if (clearPending)
        {
            File.WriteAllText(Path.Combine(_dir, "CLEAR-PENDING"), string.Empty);
        }
        var wal = new GatedRecoveryWal();
        var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(DefaultOptions(wal));
        using var cts = new CancellationTokenSource();
        var read = engine.TryGetValueAsync(K(1), cts.Token);
        await wal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        cts.Cancel();
        Func<Task> canceledRead = async () => await read.ConfigureAwait(true);
        await canceledRead.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);

        var disposalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposal = Task.Run(async () =>
        {
            disposalEntered.SetResult();
            if (asyncDispose)
            {
                await engine.DisposeAsync().ConfigureAwait(true);
            }
            else
            {
                engine.Dispose();
            }
        });
        await disposalEntered.Task.ConfigureAwait(true);
        try
        {
            var completed = await Task.WhenAny(disposal, Task.Delay(200)).ConfigureAwait(true);
            completed.Should().NotBe(disposal, "shared recovery still owns a gated caller-supplied WAL operation");
        }
        finally
        {
            wal.Release.SetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        }
        wal.Completed.Should().BeTrue("no recovery operation may still run after disposal returns");
    }

    private sealed class GatedRecoveryWal : IWriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Completed { get; private set; }

        private async Task WaitAsync()
        {
            Entered.SetResult();
            // Caller-owned implementations may finish an already-started I/O despite cancellation.
            await Release.Task.ConfigureAwait(true);
            Completed = true;
        }

        public async Task<IReadOnlyList<WalEntry<SerializableWrapper<int>, SerializableWrapper<string>>>> RecoverAsync(
            CancellationToken cancellationToken = default)
        {
            await WaitAsync().ConfigureAwait(true);
            return [];
        }

        public Task TruncateAsync(CancellationToken cancellationToken = default) => WaitAsync();
        public Task AppendAsync(SerializableWrapper<int> key, SerializableWrapper<string> value,
            ulong sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendPointerAsync(SerializableWrapper<int> key,
            Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer pointer,
            ulong sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendDeleteAsync(SerializableWrapper<int> key, ulong sequenceNumber,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task TruncateCompletedSegmentsAsync(ulong upToSeqnoInclusive,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FaultyWal(Exception ex) : IWriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>
    {
        private readonly Exception _ex = ex;

        public Task<IReadOnlyList<WalEntry<SerializableWrapper<int>, SerializableWrapper<string>>>>
            RecoverAsync(CancellationToken cancellationToken = default)
            => throw _ex;

        public Task AppendAsync(SerializableWrapper<int> key, SerializableWrapper<string> value,
            ulong sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendPointerAsync(SerializableWrapper<int> key,
            Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer pointer,
            ulong sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendDeleteAsync(SerializableWrapper<int> key, ulong sequenceNumber,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task TruncateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task TruncateCompletedSegmentsAsync(ulong upToSeqnoInclusive,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
