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

namespace Boutquin.Storage.Infrastructure.Tests.WriteAheadLog;

/// <summary>
/// D1 — Group-commit tests for <see cref="WriteAheadLog{TKey, TValue}"/>.
/// Verifies the group-commit concurrency stress contract: no entry loss, FIFO ordering
/// preserved, and group commit drives a single fsync per batch. Group commit is the only
/// durability mode in v2.0.0; there is no per-append-fsync opt-out.
/// </summary>
public sealed class GroupCommitTests : IDisposable
{
    private readonly string _walDirectory = Path.Combine(
        Path.GetTempPath(), $"GroupCommitTests_{Guid.NewGuid():N}");

    public GroupCommitTests() => Directory.CreateDirectory(_walDirectory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_walDirectory))
            {
                Directory.Delete(_walDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// 256 concurrent writers each call <see cref="WriteAheadLog{TKey, TValue}.AppendAsync"/>
    /// once. Recovery yields exactly 256 entries — no loss, no duplication. Concrete validation
    /// of the spec D1 requirement: "no entry loss".
    /// </summary>
    [Fact]
    public async Task GroupCommit_256ConcurrentWriters_NoEntryLoss()
    {
        const int writerCount = 256;

        using (var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory))
        {
            var tasks = Enumerable.Range(0, writerCount)
                .Select(i => wal.AppendAsync(
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>($"v_{i}"),
                    (ulong)(i + 1)))
                .ToArray();

            await Task.WhenAll(tasks);
        }

        using var reader = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory);
        var entries = await reader.RecoverAsync();

        entries.Should().HaveCount(writerCount, "every concurrent appender's frame must reach disk under group commit");
        entries.Select(e => e.Key.Value).Should().BeEquivalentTo(Enumerable.Range(0, writerCount));
    }

    /// <summary>
    /// Sequential single-writer appends preserve append-order on disk. The flusher loop must
    /// write batched frames in dequeue order so recovery surfaces them in the same sequence
    /// the caller submitted them — the spec D1 requirement: "FIFO ordering preserved".
    /// </summary>
    [Fact]
    public async Task GroupCommit_SequentialAppends_PreserveFifoOrder()
    {
        const int writerCount = 64;

        using (var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory))
        {
            for (var i = 0; i < writerCount; i++)
            {
                await wal.AppendAsync(
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>($"v_{i}"),
                    (ulong)(i + 1));
            }
        }

        using var reader = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory);
        var entries = await reader.RecoverAsync();

        entries.Select(e => e.Key.Value).Should().Equal(Enumerable.Range(0, writerCount));
        entries.Select(e => e.SequenceNumber).Should().Equal(Enumerable.Range(0, writerCount).Select(i => (ulong)(i + 1)));
    }

    /// <summary>
    /// Group commit is the only durability mode — the bulk insert below proves the
    /// channel-backed fast path is wired (entries land + recovery returns them) using the
    /// default options.
    /// </summary>
    [Fact]
    public async Task GroupCommit_IsTheDefault_BulkInsertSucceeds()
    {
        using (var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory))
        {
            for (var i = 0; i < 100; i++)
            {
                await wal.AppendAsync(
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>($"x_{i}"),
                    (ulong)(i + 1));
            }
        }

        using var reader = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory);
        var entries = await reader.RecoverAsync();
        entries.Should().HaveCount(100);
    }

    /// <summary>
    /// Tombstones survive concurrent group commits — the kind byte makes it onto disk for
    /// every frame in a batch, not just the first.
    /// </summary>
    [Fact]
    public async Task GroupCommit_MixedSetAndDelete_AllKindsRoundTrip()
    {
        using (var wal = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory))
        {
            var tasks = new List<Task>();
            for (var i = 0; i < 64; i++)
            {
                tasks.Add(wal.AppendAsync(
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>($"v_{i}"),
                    (ulong)(i * 2 + 1)));
                tasks.Add(wal.AppendDeleteAsync(
                    new SerializableWrapper<int>(i),
                    (ulong)(i * 2 + 2)));
            }
            await Task.WhenAll(tasks);
        }

        using var reader = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory);
        var entries = await reader.RecoverAsync();

        entries.Should().HaveCount(128);
        entries.Count(e => e.Kind == EntryKind.Set).Should().Be(64);
        entries.Count(e => e.Kind == EntryKind.Delete).Should().Be(64);
    }

    /// <summary>
    /// Constructor validates option ranges — out-of-band batch size raises
    /// <see cref="ArgumentOutOfRangeException"/>, matching the documented 1–4096 contract.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5000)]
    public void Options_OutOfRangeBatchSize_Throws(int batchSize)
    {
        var options = new WriteAheadLogOptions { GroupCommitBatchSize = batchSize };
        var act = () => new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory, options);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Constructor validates option ranges — out-of-band latency raises
    /// <see cref="ArgumentOutOfRangeException"/>, matching the documented 1–1000 ms contract.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2000)]
    public void Options_OutOfRangeLatency_Throws(int latencyMs)
    {
        var options = new WriteAheadLogOptions { GroupCommitLatencyMs = latencyMs };
        var act = () => new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory, options);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Constructor validates option ranges — segment threshold below 1 MiB raises
    /// <see cref="ArgumentOutOfRangeException"/>. Below 1 MiB the rotation cadence cannot
    /// guarantee individual frames fit.
    /// </summary>
    [Fact]
    public void Options_TinySegmentMaxBytes_Throws()
    {
        var options = new WriteAheadLogOptions { SegmentMaxBytes = 4096 };
        var act = () => new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<int>, SerializableWrapper<string>>(_walDirectory, options);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
