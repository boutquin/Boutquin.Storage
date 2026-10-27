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
using Boutquin.Storage.Infrastructure.Compression;

namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Direct unit tests for <see cref="SstSegmentFormat{TKey, TValue}"/> — the Phase-H replacement
/// for <c>RowSegmentFormat</c>. The SST segment format is the sole LSM-segment format shipping
/// in v2.0.0 and routes every flush through the v1 block-structured SSTable
/// with per-entry sequence numbers.
/// </summary>
public sealed class SstSegmentFormatTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"SstSegmentFormatTests_{Guid.NewGuid():N}");

    public SstSegmentFormatTests()
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

    [Fact]
    public void FileExtension_IsDotSst()
    {
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>();

        format.FileExtension.Should().Be(".sst");
    }

    [Fact]
    public async Task WriteAsync_ThenRead_RoundTripsEntries()
    {
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>();

        var entries = new List<(SerializableWrapper<int>, SerializableWrapper<string>, ulong)>
        {
            (new SerializableWrapper<int>(1), new SerializableWrapper<string>("one"), 10UL),
            (new SerializableWrapper<int>(2), new SerializableWrapper<string>("two"), 11UL),
            (new SerializableWrapper<int>(3), new SerializableWrapper<string>("three"), 12UL),
        };

        var reader = await format.WriteAsync(_folder, "seg_000000.sst", entries);

        var all = (await reader.GetAllItemsAsync()).ToList();
        all.Should().HaveCount(3);
        all[0].Key.Value.Should().Be(1);
        all[0].Value.Value.Should().Be("one");
        all[2].Value.Value.Should().Be("three");
    }

    [Fact]
    public async Task WriteAsync_EmptyInput_CreatesReaderOverEmptySegment()
    {
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>();

        var reader = await format.WriteAsync(
            _folder,
            "seg_empty.sst",
            Array.Empty<(SerializableWrapper<int>, SerializableWrapper<string>, ulong)>());

        var all = await reader.GetAllItemsAsync();
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task TryGetValueAsync_FindsExistingKey()
    {
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>();

        var entries = new List<(SerializableWrapper<int>, SerializableWrapper<string>, ulong)>
        {
            (new SerializableWrapper<int>(42), new SerializableWrapper<string>("answer"), 99UL),
        };

        var reader = await format.WriteAsync(_folder, "seg_lookup.sst", entries);

        var (value, found) = await reader.TryGetValueAsync(new SerializableWrapper<int>(42));
        found.Should().BeTrue();
        value.Value.Should().Be("answer");
    }

    [Fact]
    public async Task TryGetValueAsync_MissingKey_ReturnsNotFound()
    {
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>();

        var entries = new List<(SerializableWrapper<int>, SerializableWrapper<string>, ulong)>
        {
            (new SerializableWrapper<int>(1), new SerializableWrapper<string>("one"), 1UL),
        };

        var reader = await format.WriteAsync(_folder, "seg_miss.sst", entries);

        var (_, found) = await reader.TryGetValueAsync(new SerializableWrapper<int>(999));
        found.Should().BeFalse();
    }

    [Fact]
    public async Task ClearAsync_DeletesUnderlyingFile()
    {
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>();

        var entries = new List<(SerializableWrapper<int>, SerializableWrapper<string>, ulong)>
        {
            (new SerializableWrapper<int>(1), new SerializableWrapper<string>("x"), 1UL),
        };

        var reader = await format.WriteAsync(_folder, "seg_clear.sst", entries);
        var path = Path.Combine(_folder, "seg_clear.sst");
        File.Exists(path).Should().BeTrue("WriteAsync must have produced the file");

        await reader.ClearAsync();

        File.Exists(path).Should().BeFalse("ClearAsync must delete the segment file");
    }

    [Fact]
    public async Task Open_ReadsExistingSegmentFromDisk()
    {
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>();

        var entries = new List<(SerializableWrapper<int>, SerializableWrapper<string>, ulong)>
        {
            (new SerializableWrapper<int>(7), new SerializableWrapper<string>("seven"), 7UL),
        };

        _ = await format.WriteAsync(_folder, "seg_open.sst", entries);

        var reopened = format.Open(_folder, "seg_open.sst");
        var (value, found) = await reopened.TryGetValueAsync(new SerializableWrapper<int>(7));
        found.Should().BeTrue();
        value.Value.Should().Be("seven");
    }

    [Fact]
    public async Task WriteAsync_PersistsSequenceNumbers_RecoverableViaSortedStringTable()
    {
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(
            codec: new NoCompressionCodec());

        var entries = new List<(SerializableWrapper<int>, SerializableWrapper<string>, ulong)>
        {
            (new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 100UL),
            (new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"), 200UL),
            (new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"), 50UL),
        };

        _ = await format.WriteAsync(_folder, "seg_seqno.sst", entries);

        // Verify seqnos round-trip by reopening via SortedStringTable directly.
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _folder, "seg_seqno.sst");
        sst.MaxSequenceNumber.Should().Be(200UL);
        sst.EnumerateInline().Select(t => t.SequenceNumber).Should().Equal(100UL, 200UL, 50UL);
    }

    [Fact]
    public void Ctor_Defaults_UseLZ4AndSortedStringTableDefaults()
    {
        // Should not throw and should yield a usable format.
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>();
        format.FileExtension.Should().Be(".sst");
    }
}
