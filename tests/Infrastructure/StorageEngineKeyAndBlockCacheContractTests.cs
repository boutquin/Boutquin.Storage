// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//
using System.Buffers.Binary;
using System.Xml.Linq;
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.Caching;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Contracts shared by the LSM engine, its SSTable segments and the key type they store: a rewritten
/// table never answers from the previous contents' cached blocks, a segment prefix is admitted only
/// when the manifest can round-trip the filenames it produces, a write whose durable append has
/// completed is applied to the memtable even when its caller cancels afterwards, footer corruption
/// surfaces with its cause, and the key contract states the canonical-bytes requirement the bloom
/// filter depends on.
/// </summary>
public sealed class StorageEngineKeyAndBlockCacheContractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"engine-key-cache-{Guid.NewGuid():N}");

    public StorageEngineKeyAndBlockCacheContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Block cache: a rewritten table must not serve the old file's blocks ─────────────

    [Fact]
    public void RewrittenTableWithBlockCache_PointLookupReturnsNewContents()
    {
        var table = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _dir, "rewrite.sst", blockCache: new LruBlockCache(maxBytes: 64 * 1024));
        table.Write([(new SerializableWrapper<int>(1), new SerializableWrapper<string>("first"), 1UL)]);
        table.TryGetValue(new SerializableWrapper<int>(1), out var before).Should().BeTrue();
        before.Value.Should().Be("first");

        table.Write([(new SerializableWrapper<int>(1), new SerializableWrapper<string>("second"), 2UL)]);

        table.TryGetValue(new SerializableWrapper<int>(1), out var after).Should().BeTrue();
        after.Value.Should().Be("second", "the rewrite replaced the file, so a cached block of the old file must not answer");
    }

    [Fact]
    public void DeletedThenRewrittenTableWithBlockCache_PointLookupReturnsNewContents()
    {
        var table = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _dir, "delete-rewrite.sst", blockCache: new LruBlockCache(maxBytes: 64 * 1024));
        table.Write([(new SerializableWrapper<int>(7), new SerializableWrapper<string>("old"), 1UL)]);
        table.TryGetValue(new SerializableWrapper<int>(7), out _).Should().BeTrue();

        table.Delete(FileDeletionHandling.DeleteIfExists);
        table.Write([(new SerializableWrapper<int>(7), new SerializableWrapper<string>("new"), 2UL)]);

        table.TryGetValue(new SerializableWrapper<int>(7), out var after).Should().BeTrue();
        after.Value.Should().Be("new");
    }

    // ── Segment prefix admission ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(" seg")]
    [InlineData("\tseg")]
    [InlineData("se\tg")]
    [InlineData("se\ng")]
    [InlineData("se\rg")]
    [InlineData("se/g")]
    [InlineData("se\\g")]
    [InlineData("\uFEFFseg")]
    public async Task Open_SegmentPrefixTheManifestCannotRoundTrip_IsRefused(string prefix)
    {
        var options = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            100, _dir, prefix, new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>());

        var open = async () =>
        {
            var engine = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>.OpenAsync(options);
            await engine.DisposeAsync();
        };

        await open.Should().ThrowAsync<ArgumentException>(
            "a prefix whose segment filenames the manifest reader cannot parse back would make acknowledged data unopenable");
    }

    [Theory]
    [InlineData(" seg_000000.sst")]
    [InlineData("seg\t000000.sst")]
    [InlineData("seg\n000000.sst")]
    [InlineData("\uFEFFseg_000000.sst")]
    public void ManifestWrite_FilenameItsReaderCannotRoundTrip_IsRefused(string fileName)
    {
        var write = () => SegmentManifest.WriteEntries(_dir, [new SegmentManifest.Entry(fileName, 0u, 0L, 0)]);

        write.Should().Throw<ArgumentException>("the writer must not emit a row its own reader rejects or reads back differently");
    }

    [Fact]
    public async Task Open_SegmentPrefixWithAnUnpairedSurrogate_IsRefused()
    {
        // Built at run time: an unpaired surrogate is not representable in theory-data serialization.
        var prefix = new string([(char)0xD800, 's', 'e', 'g']);
        var options = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            100, _dir, prefix, new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>());

        var open = async () =>
        {
            var engine = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>.OpenAsync(options);
            await engine.DisposeAsync();
        };

        await open.Should().ThrowAsync<ArgumentException>(
            "the manifest's UTF-8 writer replaces an unpaired surrogate, so the name would read back differently");
    }

    // ── Cancellation after the durable append ──────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Set_CancelledAfterDurableAppend_IsVisibleLiveAndAfterReopen(bool pointerPath)
    {
        var segDir = Path.Combine(_dir, "seg");
        var walDir = Path.Combine(_dir, "wal");
        Directory.CreateDirectory(segDir);
        Directory.CreateDirectory(walDir);
        var valueLog = new ValueLogOptions(ValueLogThresholdBytes: 64);
        var payload = pointerPath ? new string('p', 200) : "v1";

        using var cts = new CancellationTokenSource();
        var inner = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        var wal = new CancelAfterAppendWal(inner, cts);
        var engine = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>.OpenAsync(
            new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
                1000, segDir, "seg", new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(), wal, ValueLog: valueLog));
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v0"));

        wal.Armed = true;
        try
        {
            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>(payload), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Whether the call reports the cancellation is not the property; visibility is.
        }

        wal.AppendCompleted.Should().BeTrue("the probe cancels only once the durable append has returned");
        var (live, liveFound) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
        await engine.DisposeAsync();
        inner.Dispose();

        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        var reopened = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>.OpenAsync(
            new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
                1000, segDir, "seg", new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(), wal2, ValueLog: valueLog));
        var (recovered, recoveredFound) = await reopened.TryGetValueAsync(new SerializableWrapper<int>(1));
        await reopened.DisposeAsync();

        recoveredFound.Should().BeTrue();
        recovered.Value.Should().Be(payload, "recovery replays the durable frame");
        liveFound.Should().BeTrue();
        live.Value.Should().Be(recovered.Value, "the live store must show the write recovery will reinstall");
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(2)]
    public async Task SetBulk_CancelledAfterTheFirstDurableAppend_AppliesTheWholeBatchLiveAndAfterReopen(int memTableCapacity)
    {
        var segDir = Path.Combine(_dir, "seg");
        var walDir = Path.Combine(_dir, "wal");
        Directory.CreateDirectory(segDir);
        Directory.CreateDirectory(walDir);
        int[] keys = [1, 2, 3, 4, 5];

        using var cts = new CancellationTokenSource();
        var inner = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        var wal = new CancelAfterAppendWal(inner, cts) { AppendsBeforeCancel = 2 };
        var engine = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>.OpenAsync(
            new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
                memTableCapacity, segDir, "seg", new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(), wal));

        wal.Armed = true;
        try
        {
            await engine.SetBulkAsync(
                keys.Select(k => new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(
                    new SerializableWrapper<int>(k), new SerializableWrapper<string>("v" + k))),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Whether the call reports the cancellation is not the property; batch atomicity is.
        }

        wal.AppendCompleted.Should().BeTrue("the probe cancels only once a durable append of the batch has returned");
        var live = (await engine.GetAllItemsAsync()).Select(kv => kv.Key.Value).Order().ToArray();
        await engine.DisposeAsync();
        inner.Dispose();

        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        var reopened = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>.OpenAsync(
            new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
                memTableCapacity, segDir, "seg", new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(), wal2));
        var recovered = (await reopened.GetAllItemsAsync()).Select(kv => kv.Key.Value).Order().ToArray();
        await reopened.DisposeAsync();

        live.Should().Equal(keys, "once an item of the batch is durable the caller observes the whole batch, never a partial prefix");
        recovered.Should().Equal(live, "recovery reinstalls exactly what the live store showed");
    }

    // ── Footer open diagnostics ────────────────────────────────────────────────────────

    [Fact]
    public void Open_FooterCrcMismatch_ReportsTheCrcFailureAsInnerCause()
    {
        const string fileName = "footer-crc.sst";
        var writer = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(_dir, fileName);
        writer.Write([(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1UL)]);

        var path = Path.Combine(_dir, fileName);
        var file = File.ReadAllBytes(path);
        var trailer = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(file.Length - 4, 4));
        var footerStart = file.Length - 4 - (int)trailer;
        file[footerStart + 6] ^= 0x01; // inside the CRC-covered body, past the magic
        File.WriteAllBytes(path, file);

        var open = () => new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(_dir, fileName);

        var thrown = open.Should().Throw<InvalidDataException>().Which;
        thrown.InnerException.Should().NotBeNull("the specific footer check that failed must not be discarded");
        thrown.Message.Should().Contain("CRC32C");
    }

    // ── Key contract: canonical serialized bytes ───────────────────────────────────────

    [Fact]
    public void StorageKeyContract_RequiresCompareEqualKeysToSerializeToIdenticalBytes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Boutquin.Storage.Domain.xml");
        var member = XDocument.Load(path).Descendants("member")
            .Single(m => (string?)m.Attribute("name") == "T:Boutquin.Storage.Domain.Interfaces.IStorageKey`1");
        var remarks = string.Join(' ', member.Element("remarks")!.Value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        remarks.Should().Contain("byte-identical",
            "the SSTable bloom filter is keyed on the serialized bytes, so CompareTo-equal keys must serialize identically");
        remarks.Should().NotContain("free to choose any encoding");
    }

    /// <summary>Pass-through WAL that cancels the caller's token once an armed append has returned.</summary>
    private sealed class CancelAfterAppendWal(
        IWriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>> inner,
        CancellationTokenSource cts) : IWriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>
    {
        public bool Armed { get; set; }

        public int AppendsBeforeCancel { get; set; } = 1;

        public bool AppendCompleted { get; private set; }

        private int _armedAppends;

        public Task AppendAsync(SerializableWrapper<int> key, SerializableWrapper<string> value, ulong sequenceNumber, CancellationToken cancellationToken = default)
            => AfterAsync(inner.AppendAsync(key, value, sequenceNumber, cancellationToken));

        public Task AppendPointerAsync(SerializableWrapper<int> key, ValueLogPointer pointer, ulong sequenceNumber, CancellationToken cancellationToken = default)
            => AfterAsync(inner.AppendPointerAsync(key, pointer, sequenceNumber, cancellationToken));

        public Task AppendDeleteAsync(SerializableWrapper<int> key, ulong sequenceNumber, CancellationToken cancellationToken = default)
            => AfterAsync(inner.AppendDeleteAsync(key, sequenceNumber, cancellationToken));

        public Task<IReadOnlyList<WalEntry<SerializableWrapper<int>, SerializableWrapper<string>>>> RecoverAsync(CancellationToken cancellationToken = default)
            => inner.RecoverAsync(cancellationToken);

        public Task TruncateAsync(CancellationToken cancellationToken = default) => inner.TruncateAsync(cancellationToken);

        public Task TruncateCompletedSegmentsAsync(ulong upToInclusive, CancellationToken cancellationToken = default)
            => inner.TruncateCompletedSegmentsAsync(upToInclusive, cancellationToken);

        public void Dispose() => inner.Dispose();

        private async Task AfterAsync(Task append)
        {
            await append.ConfigureAwait(true);
            if (Armed && ++_armedAppends >= AppendsBeforeCancel)
            {
                AppendCompleted = true;
                await cts.CancelAsync().ConfigureAwait(true);
            }
        }
    }
}
