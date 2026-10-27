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
/// LSM sequence-number invariant tests.
/// <para>
/// The engine exposes a monotonic <c>NextSequenceNumber</c> that increments on every
/// successful <c>SetAsync</c> / <c>RemoveAsync</c> and that recovers across restart via
/// WAL replay and/or SST footer scan — the <c>max(sst_max, wal_max) + 1</c> invariant.
/// After Phase H, the default <see cref="LsmStorageEngine{TKey,TValue}"/> uses
/// <see cref="SstSegmentFormat{TKey,TValue}"/> so every flushed segment surfaces a
/// non-zero <c>sst_max</c>.
/// </para>
/// </summary>
public sealed class LsmSequenceNumberTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"LsmSeqnoTests_{Guid.NewGuid():N}");

    public LsmSequenceNumberTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void NextSequenceNumber_FreshEngine_IsOne()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg");

        engine.NextSequenceNumber.Should().Be(1UL,
            "seqnos start at 1; the next one to be assigned is the counter's current value");
    }

    [Fact]
    public async Task NextSequenceNumber_IncrementsOnSet()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg");

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
        engine.NextSequenceNumber.Should().Be(2UL);

        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
        engine.NextSequenceNumber.Should().Be(3UL);

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a2"));
        engine.NextSequenceNumber.Should().Be(4UL,
            "every write increments, including overwrites of the same key");
    }

    [Fact]
    public async Task NextSequenceNumber_IncrementsOnRemove()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg");

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
        await engine.RemoveAsync(new SerializableWrapper<int>(1));

        engine.NextSequenceNumber.Should().Be(4UL,
            "2 Sets + 1 Remove = 3 increments; next is 4");
    }

    [Fact]
    public void LastAssignedSequenceNumber_FreshEngine_IsZero()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg");

        engine.LastAssignedSequenceNumber.Should().Be(0UL,
            "nothing has been assigned yet");
    }

    [Fact]
    public async Task LastAssignedSequenceNumber_TracksHighestAssigned()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg");

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
        await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"));

        engine.LastAssignedSequenceNumber.Should().Be(3UL);
    }

    [Fact]
    public async Task NextSequenceNumber_SurvivesFlush()
    {
        // Flushing the MemTable must not reset the counter — seqnos are engine-global,
        // not MemTable-local. Recovery contract: "preserves the monotonic-across-restart
        // invariant". Within a single run, the invariant is "monotonic-across-flushes".
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg");

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
        await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"));
        // Third Set triggers a flush (capacity=2); counter must still count it.
        await engine.SetAsync(new SerializableWrapper<int>(4), new SerializableWrapper<string>("d"));

        engine.NextSequenceNumber.Should().Be(5UL);
        engine.LastAssignedSequenceNumber.Should().Be(4UL);
    }

    [Fact]
    public async Task Recovery_FromWal_ReseedsCounterPastWalMax()
    {
        // Recovery contract: rebuild counter to max(sst, wal) + 1 before accepting new writes.
        // Deliberately skip the flush so sst_max = 0 and wal_max drives the reseed.
        var walPath = Path.Combine(_testDirectory, "recovery.wal");

        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
            using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 10,
                segmentFolder: _testDirectory,
                segmentPrefix: "seg",
                writeAheadLog: wal);

            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
            await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
            await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"));

            engine.NextSequenceNumber.Should().Be(4UL);
            // Deliberately do NOT flush — the WAL is the authoritative record for
            // recovery, and we want to exercise the replay path.
        }

        {
            using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
            using var engine2 = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 10,
                segmentFolder: _testDirectory,
                segmentPrefix: "seg",
                writeAheadLog: wal2);

            engine2.NextSequenceNumber.Should().Be(4UL,
                "3 WAL entries replayed → counter reseeded to wal_max + 1 = 4");
            engine2.LastAssignedSequenceNumber.Should().Be(3UL);

            // Next write must continue monotonically from the recovered counter.
            await engine2.SetAsync(new SerializableWrapper<int>(4), new SerializableWrapper<string>("d"));
            engine2.LastAssignedSequenceNumber.Should().Be(4UL);
            engine2.NextSequenceNumber.Should().Be(5UL);
        }
    }

    [Fact]
    public async Task Recovery_WithTombstones_CountsBothSetAndDeleteEntries()
    {
        var walPath = Path.Combine(_testDirectory, "recovery_tombstones.wal");

        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
            using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 10,
                segmentFolder: _testDirectory,
                segmentPrefix: "seg",
                writeAheadLog: wal);

            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
            await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
            await engine.RemoveAsync(new SerializableWrapper<int>(1));
            engine.NextSequenceNumber.Should().Be(4UL);
        }

        {
            using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
            using var engine2 = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 10,
                segmentFolder: _testDirectory,
                segmentPrefix: "seg",
                writeAheadLog: wal2);

            engine2.NextSequenceNumber.Should().Be(4UL,
                "3 WAL entries (2 Set + 1 Delete) replayed; counter = 4");
        }
    }

    [Fact]
    public async Task Recovery_FromSstablesOnly_ReseedsCounterPastSstMax()
    {
        // Phase H: SstSegmentFormat persists every flushed entry's seqno. After a flush
        // and WAL truncation, the engine restarts with an empty WAL but surviving SSTables
        // and must reseed from sst_max alone.
        var walPath = Path.Combine(_testDirectory, "sst_only.wal");

        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
            using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 10,
                segmentFolder: _testDirectory,
                segmentPrefix: "seg",
                writeAheadLog: wal);

            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
            await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
            await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"));
            // Flush + WAL truncate. Post-flush, wal_max = 0 but sst_max = 3.
            await engine.FlushAsync();
        }

        {
            using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
            using var engine2 = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 10,
                segmentFolder: _testDirectory,
                segmentPrefix: "seg",
                writeAheadLog: wal2);

            engine2.LastAssignedSequenceNumber.Should().Be(3UL,
                "SST footer-derived sst_max = 3 reseeds the counter even though WAL is empty");
            engine2.NextSequenceNumber.Should().Be(4UL);
        }
    }

    [Fact]
    public async Task Recovery_FromBoth_ReseedsToMaxOfSstAndWal()
    {
        // Classic recovery invariant: max(sst_max, wal_max) + 1. Flush part of the work
        // (driving sst_max up to some N), then write more via SetAsync *without* flushing
        // so the WAL extends past N. Recovery must see the WAL's higher max.
        var walPath = Path.Combine(_testDirectory, "both.wal");

        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
            using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 10,
                segmentFolder: _testDirectory,
                segmentPrefix: "seg",
                writeAheadLog: wal);

            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
            await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
            await engine.FlushAsync(); // sst_max = 2, WAL truncated

            // Post-flush writes — these stay in the WAL only.
            await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"));
            await engine.SetAsync(new SerializableWrapper<int>(4), new SerializableWrapper<string>("d"));
            engine.LastAssignedSequenceNumber.Should().Be(4UL);
        }

        {
            using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
            using var engine2 = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 10,
                segmentFolder: _testDirectory,
                segmentPrefix: "seg",
                writeAheadLog: wal2);

            engine2.LastAssignedSequenceNumber.Should().Be(4UL,
                "max(sst_max=2, wal_max=4) = 4");
            engine2.NextSequenceNumber.Should().Be(5UL);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
