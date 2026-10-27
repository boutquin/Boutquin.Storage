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
using System.Buffers.Binary;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Direct unit tests for <see cref="ColumnarSegmentFormat{TDatasetKey, TRecord}"/>. Exercises
/// the LSM-segment-level envelope — dataset grouping, per-dataset block offsets, footer/trailer
/// integrity — against <see cref="CompressedColumnarSegmentFormat{TRecord}"/> as the inner
/// format.
/// </summary>
public sealed class ColumnarSegmentFormatTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"ColumnarSegmentFormatTests_{Guid.NewGuid():N}");

    public ColumnarSegmentFormatTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar> NewFormat()
    {
        var inner = new CompressedColumnarSegmentFormat<MarketDataBar>();
        return new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(inner);
    }

    private static (TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber) Kv(
        string dataset, DateOnly date, decimal close)
        => (
            new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>(dataset), date),
            new MarketDataBar { Date = date, Close = close, Open = close, High = close, Low = close, SplitRatio = 1m },
            0UL);

    [Fact]
    public void FileExtension_IsDotBsct()
    {
        NewFormat().FileExtension.Should().Be(".bsct");
    }

    [Fact]
    public void Ctor_NullInner_Throws()
    {
        var act = () => new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("innerColumnarFormat");
    }

    [Fact]
    public async Task WriteAsync_EmptyInput_CreatesValidEmptySegment()
    {
        var format = NewFormat();
        var reader = await format.WriteAsync(
            _folder,
            $"seg_000000{format.FileExtension}",
            Array.Empty<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>());

        var all = (await reader.GetAllItemsAsync()).ToList();
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_SingleDataset_RoundTripsAllRecords()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = Enumerable.Range(0, 10)
            .Select(i => Kv("AAPL", d0.AddDays(i), 100m + (i * 0.25m)))
            .ToList();

        var reader = await format.WriteAsync(_folder, "seg_single.bsct", entries);

        var all = (await reader.GetAllItemsAsync()).ToList();
        all.Should().HaveCount(10);
        all.Select(x => x.Key.Dataset.Value).Distinct().Should().ContainSingle().Which.Should().Be("AAPL");
        all.Select(x => x.Value.Close).Should().BeEquivalentTo(entries.Select(e => e.Value.Close));
    }

    [Fact]
    public async Task WriteAsync_MultipleDatasets_SeparatesIntoDistinctBlocks()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m), Kv("AAPL", d0.AddDays(1), 2m), Kv("AAPL", d0.AddDays(2), 3m),
            Kv("MSFT", d0, 10m), Kv("MSFT", d0.AddDays(1), 11m),
            Kv("TSLA", d0, 100m),
        };

        var reader = await format.WriteAsync(_folder, "seg_multi.bsct", entries);

        var all = (await reader.GetAllItemsAsync()).ToList();
        all.Should().HaveCount(6);

        var byDataset = all.GroupBy(x => x.Key.Dataset.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Value.Close).ToList());
        byDataset["AAPL"].Should().Equal(1m, 2m, 3m);
        byDataset["MSFT"].Should().Equal(10m, 11m);
        byDataset["TSLA"].Should().Equal(100m);
    }

    // A non-ascending dataset stream splits one dataset into two blocks; FindBlock serves only
    // the first, so the second block's rows are acknowledged but unreachable by a point read.
    // The writer must reject the input (fail-fast, like the SSTable writer) instead.
    [Fact]
    public async Task WriteAsync_UnsortedDatasetOrder_ThrowsArgumentException()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 10m),
            Kv("MSFT", d0, 20m),
            Kv("AAPL", d0.AddDays(1), 11m),
        };

        var act = async () => await format.WriteAsync(_folder, "seg_unsorted.bsct", entries);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("sortedEntries");
        File.Exists(Path.Combine(_folder, "seg_unsorted.bsct")).Should().BeFalse();
    }

    // Two entries with the same key: the segment would hold both rows, and a point read would
    // return whichever sorts first rather than the newer one. Strictly-ascending keys only.
    [Fact]
    public async Task WriteAsync_DuplicateKey_ThrowsArgumentException()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 10m) with { SequenceNumber = 1UL },
            Kv("AAPL", d0, 99m) with { SequenceNumber = 2UL },
        };

        var act = async () => await format.WriteAsync(_folder, "seg_duplicate.bsct", entries);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("sortedEntries");
        File.Exists(Path.Combine(_folder, "seg_duplicate.bsct")).Should().BeFalse();
    }

    // The envelope persists only the record and readers rebuild each key from record.Date, so an
    // entry whose key date differs from its record's date would move to the record's date across
    // a flush. The writer must refuse it rather than silently re-key it.
    [Fact]
    public async Task WriteAsync_KeyDateDiffersFromRecordDate_ThrowsArgumentException()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var mismatched = Kv("AAPL", d0.AddDays(4), 10m) with { Value = Kv("AAPL", d0, 10m).Value };
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            mismatched,
        };

        var act = async () => await format.WriteAsync(_folder, "seg_rekey.bsct", entries);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("2024-01-05");
        File.Exists(Path.Combine(_folder, "seg_rekey.bsct")).Should().BeFalse();
    }

    // The same refusal must happen before the engine acknowledges the write: a refusal at flush
    // would leave an acknowledged entry that faults every later flush.
    [Fact]
    public async Task LsmEngineSetAsync_KeyDateDiffersFromRecordDate_RejectedBeforeAcknowledgement()
    {
        await using var engine = NewColumnarLsmEngine();
        var d0 = new DateOnly(2024, 1, 1);
        var (key, _, _) = Kv("AAPL", d0.AddDays(4), 10m);
        var (_, bar, _) = Kv("AAPL", d0, 10m);

        var act = () => engine.SetAsync(key, bar);

        await act.Should().ThrowAsync<ArgumentException>();
        (await engine.TryGetValueAsync(key)).Found.Should().BeFalse("a refused write is never acknowledged");
        var (goodKey, goodBar, _) = Kv("AAPL", d0, 11m);
        await engine.SetAsync(goodKey, goodBar);
        await engine.FlushAsync();
        (await engine.TryGetValueAsync(goodKey)).Found.Should().BeTrue("the engine is not faulted by the refusal");
    }

    [Fact]
    public async Task LsmEngineSetBulkAsync_OneKeyDateDiffersFromRecordDate_RejectsWholeBatch()
    {
        await using var engine = NewColumnarLsmEngine();
        var d0 = new DateOnly(2024, 1, 1);
        var (goodKey, goodBar, _) = Kv("AAPL", d0, 10m);
        var (badKey, _, _) = Kv("AAPL", d0.AddDays(4), 10m);

        var act = () => engine.SetBulkAsync([new(goodKey, goodBar), new(badKey, goodBar)]);

        await act.Should().ThrowAsync<ArgumentException>();
        (await engine.TryGetValueAsync(goodKey)).Found.Should().BeFalse("the batch is validated before any entry is applied");
    }

    // The footer stores a [BitmapIndex] key's UTF-8 length in a 16-bit field, so a longer value can
    // never be flushed. It must be refused before the engine acknowledges it, not at flush.
    [Fact]
    public async Task LsmEngineSetAsync_BitmapIndexedStringOverFooterKeyCap_RejectedBeforeAcknowledgement()
    {
        await using var engine = NewTickerNoteLsmEngine();
        var d0 = new DateOnly(2024, 1, 1);
        var key = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0);
        // 32,768 two-byte characters: under the cap in chars, one byte over it in UTF-8.
        var bar = new TickerNoteBar { Date = d0, Ticker = new string('é', 32_768) };

        var act = () => engine.SetAsync(key, bar);

        await act.Should().ThrowAsync<ArgumentException>("the footer cannot carry the key").WithMessage("*Ticker*");
        (await engine.TryGetValueAsync(key)).Found.Should().BeFalse("a refused write is never acknowledged");
        var atCap = new TickerNoteBar { Date = d0, Ticker = new string('é', 32_767) + "e" };
        await engine.SetAsync(key, atCap);
        await engine.FlushAsync();
        (await engine.TryGetValueAsync(key)).Value.Ticker.Should().Be(atCap.Ticker, "a key of exactly 65,535 UTF-8 bytes still flushes");
    }

    [Fact]
    public async Task LsmEngineSetBulkAsync_OneBitmapIndexedStringOverFooterKeyCap_RejectsWholeBatch()
    {
        await using var engine = NewTickerNoteLsmEngine();
        var d0 = new DateOnly(2024, 1, 1);
        var goodKey = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0);
        var badKey = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("MSFT"), d0);
        var good = new TickerNoteBar { Date = d0, Ticker = "AAPL" };
        var bad = new TickerNoteBar { Date = d0, Ticker = new string('y', 70_000) };

        var act = () => engine.SetBulkAsync([new(goodKey, good), new(badKey, bad)]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Ticker*");
        (await engine.TryGetValueAsync(goodKey)).Found.Should().BeFalse("the batch is validated before any entry is applied");
        await engine.SetAsync(goodKey, good);
        var flush = () => engine.FlushAsync();
        await flush.Should().NotThrowAsync("the engine is not faulted by the refusal");
    }

    // The columnar string codec encodes strict UTF-8, so a string with an unpaired surrogate can never
    // be flushed even when the record's own serializer (lenient UTF-8 here) accepts it.
    [Fact]
    public async Task LsmEngineSetAsync_StringColumnWithUnpairedSurrogate_RejectedBeforeAcknowledgement()
    {
        await using var engine = NewTickerNoteLsmEngine();
        var d0 = new DateOnly(2024, 1, 1);
        var key = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0);
        var bar = new TickerNoteBar { Date = d0, Ticker = "AAPL", Note = "a\uD800b" };

        var act = () => engine.SetAsync(key, bar);

        await act.Should().ThrowAsync<ArgumentException>("the column codec cannot encode the value").WithMessage("*Note*");
        (await engine.TryGetValueAsync(key)).Found.Should().BeFalse("a refused write is never acknowledged");
        var paired = bar with { Note = "a\uD83D\uDE00b" };
        await engine.SetAsync(key, paired);
        await engine.FlushAsync();
        (await engine.TryGetValueAsync(key)).Value.Note.Should().Be(paired.Note, "a well-formed surrogate pair still flushes");
    }

    // The columnar codecs encode no null values, so a null string column can never be flushed even when
    // the record's own serializer coerces it.
    [Fact]
    public async Task LsmEngineSetAsync_NullStringColumn_RejectedBeforeAcknowledgement()
    {
        await using var engine = NewTickerNoteLsmEngine();
        var d0 = new DateOnly(2024, 1, 1);
        var key = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0);
        var bar = new TickerNoteBar { Date = d0, Ticker = "AAPL", Note = null! };

        var act = () => engine.SetAsync(key, bar);

        await act.Should().ThrowAsync<ArgumentException>("the column codecs cannot encode a null value").WithMessage("*Note*");
        (await engine.TryGetValueAsync(key)).Found.Should().BeFalse("a refused write is never acknowledged");
        await engine.SetAsync(key, bar with { Note = "" });
        var flush = () => engine.FlushAsync();
        await flush.Should().NotThrowAsync("the engine is not faulted by the refusal");
    }

    private LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, TickerNoteBar> NewTickerNoteLsmEngine()
    {
        var folder = Path.Combine(_folder, $"lsm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        // The threshold is raised so a long record stays inline; the cap under test is the footer's,
        // not the value log's.
        return LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, TickerNoteBar>(
            memTableCapacity: 16, segmentFolder: folder, segmentPrefix: "ts",
            segmentFormat: new ColumnarSegmentFormat<SerializableWrapper<string>, TickerNoteBar>(
                new CompressedColumnarSegmentFormat<TickerNoteBar>()),
            valueLog: Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogOptions.Default with
            {
                ValueLogThresholdBytes = 128 * 1024 * 1024,
            });
    }

    private LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> NewColumnarLsmEngine()
    {
        var folder = Path.Combine(_folder, $"lsm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
            memTableCapacity: 16, segmentFolder: folder, segmentPrefix: "ts", segmentFormat: NewFormat());
    }

    [Fact]
    public async Task TryGetValueAsync_ExistingKeyInFirstDataset_Returns()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m), Kv("AAPL", d0.AddDays(1), 2m),
            Kv("MSFT", d0, 10m),
        };

        var reader = await format.WriteAsync(_folder, "seg_get1.bsct", entries);

        var (value, found) = await reader.TryGetValueAsync(
            new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0.AddDays(1)));

        found.Should().BeTrue();
        value.Close.Should().Be(2m);
    }

    [Fact]
    public async Task TryGetValueAsync_ExistingKeyInLaterDataset_Returns()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 6, 15);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
            Kv("MSFT", d0, 10m),
            Kv("TSLA", d0, 100m),
        };

        var reader = await format.WriteAsync(_folder, "seg_get2.bsct", entries);

        var (value, found) = await reader.TryGetValueAsync(
            new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("TSLA"), d0));

        found.Should().BeTrue();
        value.Close.Should().Be(100m);
    }

    [Fact]
    public async Task TryGetValueAsync_MissingDataset_ReturnsNotFound()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
        };

        var reader = await format.WriteAsync(_folder, "seg_missds.bsct", entries);

        var (_, found) = await reader.TryGetValueAsync(
            new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("GOOG"), d0));

        found.Should().BeFalse();
    }

    [Fact]
    public async Task TryGetValueAsync_MissingDateInExistingDataset_ReturnsNotFound()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m), Kv("AAPL", d0.AddDays(2), 3m),
        };

        var reader = await format.WriteAsync(_folder, "seg_missdt.bsct", entries);

        var (_, found) = await reader.TryGetValueAsync(
            new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0.AddDays(1)));

        found.Should().BeFalse();
    }

    [Fact]
    public async Task ClearAsync_DeletesFile()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
        };

        var reader = await format.WriteAsync(_folder, "seg_clear.bsct", entries);
        var path = Path.Combine(_folder, "seg_clear.bsct");
        File.Exists(path).Should().BeTrue();

        await reader.ClearAsync();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task Open_ReadsExistingSegmentFromDisk()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
            Kv("MSFT", d0, 10m),
        };

        _ = await format.WriteAsync(_folder, "seg_open.bsct", entries);

        var reopened = format.Open(_folder, "seg_open.bsct");
        var all = (await reopened.GetAllItemsAsync()).ToList();
        all.Should().HaveCount(2);
        var (v, found) = await reopened.TryGetValueAsync(
            new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("MSFT"), d0));
        found.Should().BeTrue();
        v.Close.Should().Be(10m);
    }

    [Fact]
    public void Open_NonBoutquinFile_ThrowsInvalidSegment()
    {
        var format = NewFormat();
        var path = Path.Combine(_folder, "not_a_segment.bsct");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F]);

        var reader = format.Open(_folder, "not_a_segment.bsct");

        var act = () => reader.GetAllItemsAsync().GetAwaiter().GetResult();
        act.Should().Throw<InvalidSegmentException>();
    }

    [Fact]
    public async Task Open_TruncatedFile_ThrowsTruncated()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
        };

        _ = await format.WriteAsync(_folder, "seg_trunc.bsct", entries);
        var path = Path.Combine(_folder, "seg_trunc.bsct");

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(4);
        }

        var reader = format.Open(_folder, "seg_trunc.bsct");

        var act = () => reader.GetAllItemsAsync().GetAwaiter().GetResult();
        act.Should().Throw<TruncatedSegmentException>();
    }

    [Fact]
    public async Task Open_CorruptFooterCrc_ThrowsCorrupt()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
        };

        _ = await format.WriteAsync(_folder, "seg_crc.bsct", entries);
        var path = Path.Combine(_folder, "seg_crc.bsct");

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            var crcOffset = fs.Length - 8 - 4;
            fs.Seek(crcOffset, SeekOrigin.Begin);
            var b = (byte)fs.ReadByte();
            fs.Seek(crcOffset, SeekOrigin.Begin);
            fs.WriteByte((byte)(b ^ 0xFF));
        }

        var reader = format.Open(_folder, "seg_crc.bsct");

        var act = () => reader.GetAllItemsAsync().GetAwaiter().GetResult();
        act.Should().Throw<CorruptSegmentException>();
    }

    [Fact]
    public async Task Open_UnknownFormatVersion_ThrowsUnsupported()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
        };

        _ = await format.WriteAsync(_folder, "seg_ver.bsct", entries);
        var path = Path.Combine(_folder, "seg_ver.bsct");

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            Span<byte> future = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(future, ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>.CurrentFormatVersion + 1);
            fs.Seek(4, SeekOrigin.Begin);
            fs.Write(future);
        }

        var reader = format.Open(_folder, "seg_ver.bsct");

        var act = () => reader.GetAllItemsAsync().GetAwaiter().GetResult();
        act.Should().Throw<UnsupportedFormatVersionException>();
    }

    // ── TryGetBlockEntryAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task TryGetBlockEntryAsync_ExistingKey_ReturnsBlockEntryWithInlineKind()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 3, 15);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 42m),
        };
        var reader = await format.WriteAsync(_folder, "seg_blkentry.bsct", entries);

        var key = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0);
        var (entry, found) = await reader.TryGetBlockEntryAsync(key).ConfigureAwait(true);

        found.Should().BeTrue();
        entry.Kind.Should().Be(ValueKind.Inline);
        entry.SequenceNumber.Should().Be(0UL);
        entry.Value.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TryGetBlockEntryAsync_MissingDate_ReturnsNotFound()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
        };
        var reader = await format.WriteAsync(_folder, "seg_blkmissdt.bsct", entries);

        var key = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("AAPL"), d0.AddDays(1));
        var (_, found) = await reader.TryGetBlockEntryAsync(key).ConfigureAwait(true);

        found.Should().BeFalse();
    }

    [Fact]
    public async Task TryGetBlockEntryAsync_MissingDataset_ReturnsNotFound()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
        };
        var reader = await format.WriteAsync(_folder, "seg_blkmissds.bsct", entries);

        var key = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("GOOG"), d0);
        var (_, found) = await reader.TryGetBlockEntryAsync(key).ConfigureAwait(true);

        found.Should().BeFalse();
    }

    // ── Pointer rejection ─────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_WithValueLogPointerEntry_ThrowsNotSupportedException()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var key = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0);
        var pointerBytes = new byte[12]; // ValueLogPointer.SerializedSize = 12
        var pointerEntry = SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>
            .Pointer(key, pointerBytes, 0UL);

        var act = async () => await format.WriteAsync(
            _folder, "seg_ptr.bsct",
            [pointerEntry]).ConfigureAwait(true);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*ColumnarSegmentFormat*").ConfigureAwait(true);
    }

    // ── SortSpec ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_WithSortSpec_UnsortedKeys_StillRejected()
    {
        // A sort spec reorders rows inside a dataset block; it does not license an unsorted key
        // stream. Key order is the writer's input contract regardless of the spec.
        var inner = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var sortSpec = ColumnSortSpec.DateAscending;
        var format = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(inner, sortSpec);

        // Supply dates out of ascending order: Dec, Jan, Jun
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", new DateOnly(2024, 12, 1), 3m),
            Kv("AAPL", new DateOnly(2024, 1, 15), 1m),
            Kv("AAPL", new DateOnly(2024, 6, 1), 2m),
        };

        var act = async () => await format.WriteAsync(_folder, "seg_sorted.bsct", entries).ConfigureAwait(true);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("sortedEntries").ConfigureAwait(true);
    }

    [Fact]
    public async Task ApplySortSpec_UndefinedDirection_ThrowsAtConstruction()
    {
        // AC-2.3 (feature-storage-review-remediation-2026-06 M-2) deliberately moved the
        // undefined-SortDirection rejection from the downstream WriteAsync/comparer path INTO the
        // ColumnSortSpec constructor (fail-fast). The exception type (ArgumentOutOfRangeException)
        // is unchanged; only WHERE it fires moved earlier. The bad-direction spec is therefore no
        // longer constructible, so the construction is the action under assertion.
        var inner = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
        };

        var act = async () =>
        {
            var badSpec = new ColumnSortSpec([("Date", (SortDirection)99)]);
            var format = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(inner, badSpec);
            await format.WriteAsync(_folder, "seg_desc.bsct", entries).ConfigureAwait(true);
        };

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().ConfigureAwait(true);
    }

    // The sort spec is resolved against the record type when the format is built: a spec the
    // flush could not apply would otherwise fault every flush after the writes were acknowledged.
    [Fact]
    public void Constructor_NullableSortColumn_ThrowsInvalidOperationException()
    {
        var inner = new StubColumnarFormat<RecordWithNullableInt>();
        var spec = new ColumnSortSpec([("NullableInt", SortDirection.Ascending)]);

        var act = () => new ColumnarSegmentFormat<SerializableWrapper<string>, RecordWithNullableInt>(inner, spec);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Nullable*");
    }

    [Fact]
    public void Constructor_SortSpecUnknownColumn_ReflectionFallback_ThrowsArgumentException()
    {
        // RecordWithName is hand-written (no generated comparer), so this pins the reflection path.
        var inner = new CapturingColumnarFormat<RecordWithName>();
        var spec = new ColumnSortSpec([("Nmae", SortDirection.Ascending)]);

        var act = () => new ColumnarSegmentFormat<SerializableWrapper<string>, RecordWithName>(inner, spec);

        act.Should().Throw<ArgumentException>().WithMessage("*Nmae*");
    }

    // Generator-emitted IComparer<TRecord> on [ColumnarStorable] records
    [Fact]
    public void ColumnSortComparerFactory_ColumnarStorableRecord_ResolvesGeneratedComparer()
    {
        // MarketDataBar is annotated [ColumnarStorable], so the source generator must emit
        // a CreateColumnSortComparer factory the segment-format's runtime cache picks up.
        var spec = ColumnSortSpec.DateAscending;
        var comparer = ColumnSortComparerFactory<MarketDataBar>.TryCreate(spec);

        comparer.Should().NotBeNull(
            "the source generator emits CreateColumnSortComparer on every [ColumnarStorable] record");
    }

    [Fact]
    public void ColumnSortComparerFactory_HandWrittenRecord_FallsBackToReflection()
    {
        // RecordWithObjectArray is hand-written — no [ColumnarStorable], no generator emit.
        // The factory must return null so ApplySortSpec engages the reflection comparator.
        var spec = new ColumnSortSpec([("Date", SortDirection.Ascending)]);
        var comparer = ColumnSortComparerFactory<RecordWithObjectArray>.TryCreate(spec);

        comparer.Should().BeNull("hand-written records have no generator-emitted factory");
    }

    [Fact]
    public void ColumnSortComparerFactory_ColumnarStorable_MultiColumnSort_OrdersTypedProperties()
    {
        // Multi-column sort: Volume ASC, then Close ASC. With three records sharing Volume,
        // the secondary Close key must drive ordering through the typed Comparer<decimal>.Default
        // chain — no reflection on this hot path.
        var spec = new ColumnSortSpec(
        [
            ("Volume", SortDirection.Ascending),
            ("Close", SortDirection.Ascending),
        ]);
        var comparer = ColumnSortComparerFactory<MarketDataBar>.TryCreate(spec);
        comparer.Should().NotBeNull();

        var d0 = new DateOnly(2024, 1, 1);
        var bars = new[]
        {
            new MarketDataBar { Date = d0, Volume = 100, Close = 3m, Open = 3m, High = 3m, Low = 3m, SplitRatio = 1m },
            new MarketDataBar { Date = d0, Volume = 50,  Close = 5m, Open = 5m, High = 5m, Low = 5m, SplitRatio = 1m },
            new MarketDataBar { Date = d0, Volume = 100, Close = 1m, Open = 1m, High = 1m, Low = 1m, SplitRatio = 1m },
            new MarketDataBar { Date = d0, Volume = 100, Close = 2m, Open = 2m, High = 2m, Low = 2m, SplitRatio = 1m },
        };

        Array.Sort(bars, comparer!);

        bars[0].Volume.Should().Be(50);  // Volume primary
        bars[1].Close.Should().Be(1m);   // Volume tied → Close ascending
        bars[2].Close.Should().Be(2m);
        bars[3].Close.Should().Be(3m);
    }

    [Fact]
    public void ColumnSortComparerFactory_ColumnarStorable_UnknownColumn_ThrowsArgumentException()
    {
        var spec = new ColumnSortSpec([("DoesNotExist", SortDirection.Ascending)]);

        var act = () => ColumnSortComparerFactory<MarketDataBar>.TryCreate(spec);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*does not match any sortable public property*");
    }

    [Fact]
    public void ColumnSortComparerFactory_ColumnarStorable_UndefinedDirection_ThrowsAtConstruction()
    {
        // AC-2.3 (M-2): the undefined-SortDirection guard now fires in the ColumnSortSpec
        // constructor, not in ColumnSortComparerFactory.TryCreate. Same exception type
        // (ArgumentOutOfRangeException); the construction is now the action under assertion.
        var act = () => ColumnSortComparerFactory<MarketDataBar>.TryCreate(
            new ColumnSortSpec([("Date", (SortDirection)99)]));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ApplySortSpec_ColumnarStorableRecord_UsesGeneratedComparer_FollowsSpecNotInputOrder()
    {
        // End-to-end: WriteAsync routes through ApplySortSpec, which now prefers the generated
        // comparer for MarketDataBar. Input keys are strictly ascending (the writer contract),
        // so the spec's effect is observed through a Close-descending order that differs from
        // the key order: the row order handed to the inner format must follow the spec, not the
        // input. (The compressed reader re-orders by Date on read, so the capturing inner format
        // is the observation point for the post-ApplySortSpec order.)
        var inner = new CapturingColumnarFormat<MarketDataBar>();
        var sortSpec = new ColumnSortSpec([("Close", SortDirection.Descending)]);
        var format = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(inner, sortSpec);

        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", new DateOnly(2024, 3, 1), 1m),
            Kv("AAPL", new DateOnly(2024, 7, 4), 2m),
            Kv("AAPL", new DateOnly(2024, 8, 9), 3m),
            Kv("AAPL", new DateOnly(2024, 9, 15), 4m),
        };

        _ = await format.WriteAsync(_folder, "seg_gen_sort.bsct", entries).ConfigureAwait(true);

        inner.CapturedRecords.Select(r => r.Close).Should().Equal(4m, 3m, 2m, 1m);
    }

    [Fact]
    public void ColumnSortComparerFactory_GeneratedComparer_StringColumn_UsesOrdinalOrder()
    {
        // Ordinal order: 'z' (U+007A) sorts BEFORE 'É' (U+00C9). Culture-sensitive comparison
        // (string.CompareTo / Comparer<string>.Default) treats É like E and inverts the pair —
        // making on-disk row order depend on the writing machine's locale. The columnar stack
        // is ordinal everywhere else (ZoneMapComputer uses string.CompareOrdinal).
        var spec = new ColumnSortSpec([("Ticker", SortDirection.Ascending)]);
        var comparer = ColumnSortComparerFactory<ColumnarBar>.TryCreate(spec);
        comparer.Should().NotBeNull();

        comparer!.Compare(
                new ColumnarBar { Ticker = "z" },
                new ColumnarBar { Ticker = "É" })
            .Should().BeNegative("ordinal order puts 'z' (U+007A) before 'É' (U+00C9)");
    }

    [Fact]
    public async Task ApplySortSpec_ReflectionFallback_StringColumn_UsesOrdinalOrder()
    {
        // Hand-written record (no [ColumnarStorable]) → reflection comparator path. The boxed
        // IComparable.CompareTo on string is culture-sensitive; the sort must be ordinal to
        // match the rest of the columnar stack.
        var inner = new CapturingColumnarFormat<RecordWithName>();
        var spec = new ColumnSortSpec([("Name", SortDirection.Ascending)]);
        var format = new ColumnarSegmentFormat<SerializableWrapper<string>, RecordWithName>(inner, spec);

        var d0 = new DateOnly(2024, 1, 1);
        var key1 = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("X"), d0);
        var key2 = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("X"), d0.AddDays(1));
        var entries = new List<SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, RecordWithName>>
        {
            SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, RecordWithName>
                .Inline(key1, new RecordWithName { Date = d0, Name = "É" }, 0UL),
            SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, RecordWithName>
                .Inline(key2, new RecordWithName { Date = d0.AddDays(1), Name = "z" }, 1UL),
        };

        _ = await format.WriteAsync(_folder, "seg_ordinal_refl.bsct", entries).ConfigureAwait(true);

        inner.CapturedRecords.Select(r => r.Name).Should().Equal(["z", "É"],
            "ordinal order puts 'z' (U+007A) before 'É' (U+00C9)");
    }

    [Fact]
    public void Constructor_NonComparableSortColumn_ThrowsInvalidOperationException()
    {
        var inner = new StubColumnarFormat<RecordWithObjectArray>();
        var spec = new ColumnSortSpec([("Tags", SortDirection.Ascending)]);

        var act = () => new ColumnarSegmentFormat<SerializableWrapper<string>, RecordWithObjectArray>(inner, spec);

        act.Should().Throw<InvalidOperationException>().WithMessage("*IComparable*");
    }

    // ── StreamBlockEntriesAsync / StreamProjectedRangeAsync ───────────────────

    [Fact]
    public async Task StreamBlockEntriesAsync_Descending_ThrowsArgumentOutOfRangeException()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
        };
        var reader = await format.WriteAsync(_folder, "seg_blkdesc.bsct", entries).ConfigureAwait(true);

        var act = async () =>
        {
            await foreach (var _ in reader.StreamBlockEntriesAsync(direction: (ScanDirection)1)
                .ConfigureAwait(false))
            { }
        };

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task StreamProjectedRangeAsync_Descending_ThrowsArgumentOutOfRangeException()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
        };
        var reader = await format.WriteAsync(_folder, "seg_projdesc.bsct", entries).ConfigureAwait(true);
        var projectingReader = (IProjectingSegmentReader<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar, ColumnProjection>)reader;

        var start = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0);
        var end = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0);

        var act = async () =>
        {
            await foreach (var _ in projectingReader.StreamProjectedRangeAsync(
                start, end, ColumnProjection.All, direction: (ScanDirection)1)
                .ConfigureAwait(false))
            { }
        };

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task StreamProjectedRangeAsync_InvertedRange_YieldsEmpty()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
            Kv("MSFT", d0, 2m),
        };
        var reader = await format.WriteAsync(_folder, "seg_projinv.bsct", entries).ConfigureAwait(true);
        var projectingReader = (IProjectingSegmentReader<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar, ColumnProjection>)reader;

        // startKey > endKey
        var start = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("MSFT"), d0);
        var end = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0);

        var results = new List<KeyValuePair<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>>();
        await foreach (var kv in projectingReader.StreamProjectedRangeAsync(start, end, ColumnProjection.All)
            .ConfigureAwait(false))
        {
            results.Add(kv);
        }

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamProjectedRangeAsync_DatasetOutsideRange_IsSkipped()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 6, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAAA", d0, 1m),
            Kv("BBBB", d0, 2m),
            Kv("CCCC", d0, 3m),
        };
        var reader = await format.WriteAsync(_folder, "seg_projds.bsct", entries).ConfigureAwait(true);
        var projectingReader = (IProjectingSegmentReader<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar, ColumnProjection>)reader;

        var start = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("BBBB"), d0);
        var end = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("BBBB"), d0);

        var results = new List<KeyValuePair<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>>();
        await foreach (var kv in projectingReader.StreamProjectedRangeAsync(start, end, ColumnProjection.All)
            .ConfigureAwait(false))
        {
            results.Add(kv);
        }

        results.Should().HaveCount(1);
        results[0].Key.Dataset.Value.Should().Be("BBBB");
        results[0].Value.Close.Should().Be(2m);
    }

    [Fact]
    public async Task StreamProjectedRangeAsync_PartialDateRange_TrimmedByBounds()
    {
        var format = NewFormat();
        var entries = Enumerable.Range(1, 12)
            .Select(m => Kv("AAPL", new DateOnly(2024, m, 1), (decimal)m))
            .ToList();
        var reader = await format.WriteAsync(_folder, "seg_projdate.bsct", entries).ConfigureAwait(true);
        var projectingReader = (IProjectingSegmentReader<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar, ColumnProjection>)reader;

        var start = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("AAPL"), new DateOnly(2024, 4, 1));
        var end = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("AAPL"), new DateOnly(2024, 6, 1));

        var results = new List<KeyValuePair<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>>();
        await foreach (var kv in projectingReader.StreamProjectedRangeAsync(start, end, ColumnProjection.All)
            .ConfigureAwait(false))
        {
            results.Add(kv);
        }

        results.Should().HaveCount(3);
        results.Select(r => r.Key.Date).Should().Equal(
            new DateOnly(2024, 4, 1),
            new DateOnly(2024, 5, 1),
            new DateOnly(2024, 6, 1));
    }

    [Fact]
    public async Task StreamProjectedRangeAsync_ExposesStableDistinctPhysicalBlockIdentities()
    {
        var inner = new IdentityCapturingColumnarFormat();
        var format = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(inner);
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m), Kv("AAPL", d0.AddDays(1), 2m),
            Kv("MSFT", d0, 10m),
        };

        _ = await format.WriteAsync(_folder, "seg_identity_a.bsct", entries).ConfigureAwait(true);
        var reader = (IProjectingSegmentReader<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar, ColumnProjection>)
            format.Open(_folder, "seg_identity_a.bsct");

        await DrainProjectedRangeAsync(reader, "AAPL", d0, d0).ConfigureAwait(true);
        await DrainProjectedRangeAsync(reader, "AAPL", d0.AddDays(1), d0.AddDays(1)).ConfigureAwait(true);
        var reopened = (IProjectingSegmentReader<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar, ColumnProjection>)
            format.Open(_folder, "seg_identity_a.bsct");
        await DrainProjectedRangeAsync(reopened, "AAPL", d0, d0.AddDays(1)).ConfigureAwait(true);
        await DrainProjectedRangeAsync(reader, "MSFT", d0, d0).ConfigureAwait(true);

        _ = await format.WriteAsync(_folder, "seg_identity_b.bsct", entries).ConfigureAwait(true);
        var otherFile = (IProjectingSegmentReader<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar, ColumnProjection>)
            format.Open(_folder, "seg_identity_b.bsct");
        await DrainProjectedRangeAsync(otherFile, "AAPL", d0, d0).ConfigureAwait(true);

        inner.ObservedPhysicalSegmentIdentities.Should().HaveCount(5);
        inner.ObservedPhysicalSegmentIdentities[0].Should().NotBeNullOrWhiteSpace();
        inner.ObservedPhysicalSegmentIdentities[1].Should().Be(inner.ObservedPhysicalSegmentIdentities[0],
            "separate ranges over one physical block must share its identity");
        inner.ObservedPhysicalSegmentIdentities[2].Should().Be(inner.ObservedPhysicalSegmentIdentities[0],
            "reopening the same segment must preserve the physical block identity");
        inner.ObservedPhysicalSegmentIdentities[3].Should().NotBe(inner.ObservedPhysicalSegmentIdentities[0],
            "different dataset blocks in one segment must never collapse");
        inner.ObservedPhysicalSegmentIdentities[4].Should().NotBe(inner.ObservedPhysicalSegmentIdentities[0],
            "same-offset blocks in different segment files must never collapse");
    }

    [Fact]
    public async Task UnprojectedReads_ExposeStableDistinctPhysicalBlockIdentities()
    {
        var inner = new IdentityCapturingColumnarFormat();
        var format = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(inner);
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
            Kv("MSFT", d0, 10m),
        };

        var reader = await format.WriteAsync(_folder, "seg_identity_unprojected.bsct", entries).ConfigureAwait(true);
        _ = await reader.TryGetValueAsync(
            new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0)).ConfigureAwait(true);
        await DrainBlockEntriesAsync(reader).ConfigureAwait(true);
        var reopened = format.Open(_folder, "seg_identity_unprojected.bsct");
        await DrainBlockEntriesAsync(reopened).ConfigureAwait(true);

        inner.ObservedPhysicalSegmentIdentities.Should().HaveCount(5);
        inner.ObservedPhysicalSegmentIdentities[0].Should().Be(inner.ObservedPhysicalSegmentIdentities[1],
            "point reads and the production unprojected stream route must identify the same AAPL block");
        inner.ObservedPhysicalSegmentIdentities[0].Should().Be(inner.ObservedPhysicalSegmentIdentities[3],
            "reopening an unprojected segment must preserve the AAPL block identity");
        inner.ObservedPhysicalSegmentIdentities[2].Should().Be(inner.ObservedPhysicalSegmentIdentities[4],
            "reopening an unprojected segment must preserve the MSFT block identity");
        inner.ObservedPhysicalSegmentIdentities[0].Should().NotBe(inner.ObservedPhysicalSegmentIdentities[2],
            "different blocks in the same unprojected segment must not collapse");
    }

    // ── Open edge cases ───────────────────────────────────────────────────────

    [Fact]
    public void Open_FileDoesNotExist_GetAllItemsAsync_ReturnsEmpty()
    {
        var format = NewFormat();
        var reader = format.Open(_folder, "nonexistent.bsct");

        IEnumerable<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value)> act() => reader.GetAllItemsAsync().GetAwaiter().GetResult();
        var result = act();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Open_BadHeaderMagic_ThrowsInvalidSegmentException()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
        };

        _ = await format.WriteAsync(_folder, "seg_magic.bsct", entries).ConfigureAwait(true);
        var path = Path.Combine(_folder, "seg_magic.bsct");

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.Begin);
            fs.Write([0x00, 0x00, 0x00, 0x00]);
        }

        var reader = format.Open(_folder, "seg_magic.bsct");

        var act = () => reader.GetAllItemsAsync().GetAwaiter().GetResult();
        act.Should().Throw<InvalidSegmentException>();
    }

    [Fact]
    public async Task Open_ForgedFooterWithOversizedKeyLength_ThrowsCorruptSegmentException()
    {
        // A footer whose CRC has been recomputed over a forged payload (oversized/negative
        // keyLen) must still fail closed with the typed CorruptSegmentException — not leak a
        // raw ArgumentOutOfRangeException from the span reader's slice arithmetic.
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
        };
        _ = await format.WriteAsync(_folder, "seg_forged.bsct", entries).ConfigureAwait(true);

        var path = Path.Combine(_folder, "seg_forged.bsct");
        var bytes = File.ReadAllBytes(path);
        var footerLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8, 4));
        var footerStart = bytes.Length - 8 - (int)footerLength;
        var payloadLength = (int)footerLength - 4;

        // Patch the first dataset entry's keyLen (payload offset +12, after datasetCount(4) +
        // segment-level maxSeqno(8) — R2-G) to 0xFFFFFFFF (= -1 as int) and recompute the footer
        // CRC so only the span reader's own bounds discipline stands between the forged length
        // and the slice.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(footerStart + 4 + 8, 4), 0xFFFFFFFFu);
        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(
            bytes.AsSpan(footerStart, payloadLength));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(footerStart + payloadLength, 4), crc);
        File.WriteAllBytes(path, bytes);

        var reader = format.Open(_folder, "seg_forged.bsct");
        var act = () => reader.GetAllItemsAsync().GetAwaiter().GetResult();
        act.Should().Throw<CorruptSegmentException>();
    }

    [Theory]
    [InlineData(-1L, null, typeof(CorruptSegmentException))]
    [InlineData(null, 1_000_000L, typeof(TruncatedSegmentException))]
    [InlineData(null, long.MaxValue, typeof(TruncatedSegmentException))]
    public async Task StreamProjectedRangeAsync_ForgedBlockExtent_ThrowsTheSameTypedExceptionAsFullScan(
        long? forgedOffset, long? forgedLength, Type expected)
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        _ = await format.WriteAsync(_folder, "seg_forged_extent.bsct",
            new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)> { Kv("AAPL", d0, 1m) })
            .ConfigureAwait(true);
        var path = Path.Combine(_folder, "seg_forged_extent.bsct");
        ForgeFirstBlockExtent(path, forgedOffset, forgedLength);

        var projected = await CaptureAsync(() => DrainProjectedRangeAsync(
            (IProjectingSegmentReader<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar, ColumnProjection>)format.Open(_folder, "seg_forged_extent.bsct"),
            "AAPL", DateOnly.MinValue, DateOnly.MaxValue)).ConfigureAwait(true);
        var fullScan = await CaptureAsync(() => format.Open(_folder, "seg_forged_extent.bsct").GetAllItemsAsync()).ConfigureAwait(true);

        projected.Should().BeOfType(expected, "the projected read must fail with a typed segment exception");
        fullScan.Should().BeOfType(expected, "both read paths validate the same forged block extent");
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // Overwrites the first block's blockOffset / blockLength in the outer footer and repairs the footer
    // CRC. Payload layout: datasetCount(4) + maxSeqno(8) + keyLen(4) + key + blockOffset(8) + blockLength(8) …
    private static void ForgeFirstBlockExtent(string path, long? offset, long? length)
    {
        var bytes = File.ReadAllBytes(path);
        var trailerStart = bytes.Length - 8;
        var footerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(trailerStart, 4));
        var payloadStart = trailerStart - footerLength;
        var p = payloadStart + 4 + 8;
        var keyLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
        p += 4 + keyLen;
        if (offset is { } o)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(p, 8), o);
        }

        if (length is { } l)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(p + 8, 8), l);
        }

        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(bytes.AsSpan(payloadStart, footerLength - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(trailerStart - 4, 4), crc);
        File.WriteAllBytes(path, bytes);
    }

    // ── Stub inner format for tests that don't need a real columnar encoder ──

    private static async Task DrainProjectedRangeAsync(
        IProjectingSegmentReader<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar, ColumnProjection> reader,
        string dataset,
        DateOnly from,
        DateOnly to)
    {
        var start = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>(dataset), from);
        var end = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>(dataset), to);
        await foreach (var _ in reader.StreamProjectedRangeAsync(start, end, ColumnProjection.All).ConfigureAwait(true))
        {
        }
    }

    private static async Task DrainBlockEntriesAsync(
        ISegmentReader<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> reader)
    {
        await foreach (var _ in reader.StreamBlockEntriesAsync().ConfigureAwait(true))
        {
        }
    }

    private sealed class IdentityCapturingColumnarFormat : IColumnarSegmentFormat<MarketDataBar>
    {
        public List<string> ObservedPhysicalSegmentIdentities { get; } = [];

        public Task WriteAsync(Stream output, IReadOnlyList<MarketDataBar> records, CancellationToken cancellationToken = default)
        {
            output.WriteByte(0);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<MarketDataBar> ReadAsync(
            Stream input, DateOnly from, DateOnly to, ColumnProjection projection,
            ScanDirection direction = ScanDirection.Ascending,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ObservedPhysicalSegmentIdentities.Add(ReadPhysicalSegmentIdentity(input));
            yield return new MarketDataBar { Date = from };
            await Task.CompletedTask.ConfigureAwait(true);
        }

        public async IAsyncEnumerable<ColumnBatch<MarketDataBar>> ReadBatchesAsync(
            Stream input, DateOnly from, DateOnly to, ColumnProjection projection,
            ScanDirection direction = ScanDirection.Ascending,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }

        public IAsyncEnumerable<MarketDataBar> StreamWhereAsync(
            Stream input, DateOnly from, DateOnly to, ColumnProjection projection,
            IReadOnlyList<ColumnPredicate> predicates,
            ScanDirection direction = ScanDirection.Ascending,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("IdentityCapturingColumnarFormat is only for projected-range identity tests.");

        private static string ReadPhysicalSegmentIdentity(Stream stream)
        {
            var identityInterface = stream.GetType().GetInterface(
                "Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar.IPhysicalSegmentIdentity");
            identityInterface.Should().NotBeNull("the production projected-read path must supply a public physical identity");

            var property = identityInterface!.GetProperty("PhysicalSegmentIdentity");
            property.Should().NotBeNull("the identity contract must expose its descriptor");

            return property!.GetValue(stream).Should().BeOfType<string>().Subject;
        }
    }

    private sealed class StubColumnarFormat<T> : IColumnarSegmentFormat<T>
        where T : IHasDate, IColumnarRehydratable<T>, new()
    {
        public Task WriteAsync(Stream output, IReadOnlyList<T> records, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<T> ReadAsync(
            Stream input, DateOnly from, DateOnly to, ColumnProjection projection,
            ScanDirection direction = ScanDirection.Ascending,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }

        public async IAsyncEnumerable<ColumnBatch<T>> ReadBatchesAsync(
            Stream input, DateOnly from, DateOnly to, ColumnProjection projection,
            ScanDirection direction = ScanDirection.Ascending,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }

        public IAsyncEnumerable<T> StreamWhereAsync(
            Stream input, DateOnly from, DateOnly to, ColumnProjection projection,
            IReadOnlyList<ColumnPredicate> predicates,
            ScanDirection direction = ScanDirection.Ascending,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("StubColumnarFormat is not for disk I/O.");
    }

    // Capturing stub: records the per-dataset record list the envelope hands the inner
    // format, so sort-order tests can observe the post-ApplySortSpec row order without
    // a real codec round-trip.
    private sealed class CapturingColumnarFormat<T> : IColumnarSegmentFormat<T>
        where T : IHasDate, IColumnarRehydratable<T>, new()
    {
        public List<T> CapturedRecords { get; } = [];

        public Task WriteAsync(Stream output, IReadOnlyList<T> records, CancellationToken cancellationToken = default)
        {
            CapturedRecords.AddRange(records);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<T> ReadAsync(
            Stream input, DateOnly from, DateOnly to, ColumnProjection projection,
            ScanDirection direction = ScanDirection.Ascending,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }

        public async IAsyncEnumerable<ColumnBatch<T>> ReadBatchesAsync(
            Stream input, DateOnly from, DateOnly to, ColumnProjection projection,
            ScanDirection direction = ScanDirection.Ascending,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }

        public IAsyncEnumerable<T> StreamWhereAsync(
            Stream input, DateOnly from, DateOnly to, ColumnProjection projection,
            IReadOnlyList<ColumnPredicate> predicates,
            ScanDirection direction = ScanDirection.Ascending,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("CapturingColumnarFormat is not for disk I/O.");
    }

    // ── Helper record types for sort-spec error tests ─────────────────────────

    private sealed class RecordWithName
        : IHasDate,
          ISerializable<RecordWithName>,
          IColumnarRehydratable<RecordWithName>
    {
        public DateOnly Date { get; init; }
        public string Name { get; init; } = "";

        public void Serialize(Stream stream) => stream.WriteByte(0);
        public static RecordWithName Deserialize(Stream stream) { stream.ReadByte(); return new(); }
        public static RecordWithName Rehydrate(IReadOnlyDictionary<string, Array> columns, int rowIndex) => new();
    }

    private sealed class RecordWithNullableInt
        : IHasDate,
          ISerializable<RecordWithNullableInt>,
          IColumnarRehydratable<RecordWithNullableInt>
    {
        public DateOnly Date { get; init; }
        public int? NullableInt { get; init; }

        public void Serialize(Stream stream) => stream.WriteByte(0);
        public static RecordWithNullableInt Deserialize(Stream stream) { stream.ReadByte(); return new(); }
        public static RecordWithNullableInt Rehydrate(IReadOnlyDictionary<string, Array> columns, int rowIndex) => new();
    }

    private sealed class RecordWithObjectArray
        : IHasDate,
          ISerializable<RecordWithObjectArray>,
          IColumnarRehydratable<RecordWithObjectArray>
    {
        public DateOnly Date { get; init; }
        public object[] Tags { get; init; } = [];

        public void Serialize(Stream stream) => stream.WriteByte(0);
        public static RecordWithObjectArray Deserialize(Stream stream) { stream.ReadByte(); return new(); }
        public static RecordWithObjectArray Rehydrate(IReadOnlyDictionary<string, Array> columns, int rowIndex) => new();
    }
    // ───── Descending sort ─────────────────────────────────────────────────────

    [Fact]
    public void ColumnSortComparerFactory_GeneratedComparer_DescendingColumn_InvertsOrder()
    {
        var spec = new ColumnSortSpec([("Volume", SortDirection.Descending)]);
        var comparer = ColumnSortComparerFactory<ColumnarBar>.TryCreate(spec);
        comparer.Should().NotBeNull();

        comparer!.Compare(
                new ColumnarBar { Volume = 50 },
                new ColumnarBar { Volume = 100 })
            .Should().BePositive("descending order puts the larger Volume first");
    }

    [Fact]
    public void ColumnSortComparerFactory_GeneratedComparer_MixedAscendingDescending_OrdersPerColumn()
    {
        // Volume ascending primary, Close descending secondary.
        var spec = new ColumnSortSpec(
        [
            ("Volume", SortDirection.Ascending),
            ("Close", SortDirection.Descending),
        ]);
        var comparer = ColumnSortComparerFactory<ColumnarBar>.TryCreate(spec);
        comparer.Should().NotBeNull();

        var bars = new[]
        {
            new ColumnarBar { Volume = 100, Close = 1m },
            new ColumnarBar { Volume = 50, Close = 5m },
            new ColumnarBar { Volume = 100, Close = 3m },
            new ColumnarBar { Volume = 100, Close = 2m },
        };

        Array.Sort(bars, comparer!);

        bars.Select(b => (b.Volume, b.Close)).Should().Equal(
            (50L, 5m), (100L, 3m), (100L, 2m), (100L, 1m));
    }

    [Fact]
    public async Task ApplySortSpec_ReflectionFallback_DescendingStringColumn_SortsDescending()
    {
        var inner = new CapturingColumnarFormat<RecordWithName>();
        var spec = new ColumnSortSpec([("Name", SortDirection.Descending)]);
        var format = new ColumnarSegmentFormat<SerializableWrapper<string>, RecordWithName>(inner, spec);

        var d0 = new DateOnly(2024, 1, 1);
        var key1 = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("X"), d0);
        var key2 = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("X"), d0.AddDays(1));
        var entries = new List<SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, RecordWithName>>
        {
            SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, RecordWithName>
                .Inline(key1, new RecordWithName { Date = d0, Name = "a" }, 0UL),
            SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, RecordWithName>
                .Inline(key2, new RecordWithName { Date = d0.AddDays(1), Name = "b" }, 1UL),
        };

        _ = await format.WriteAsync(_folder, "seg_desc_refl.bsct", entries).ConfigureAwait(true);

        inner.CapturedRecords.Select(r => r.Name).Should().Equal(["b", "a"],
            "descending ordinal order puts 'b' before 'a'");
    }

}
