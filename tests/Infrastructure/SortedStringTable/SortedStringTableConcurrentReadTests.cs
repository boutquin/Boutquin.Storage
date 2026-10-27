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
using Boutquin.Storage.Infrastructure.Compression;

namespace Boutquin.Storage.Infrastructure.Tests.SortedStringTable;

public sealed class SortedStringTableConcurrentReadTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"SstConcurrentReads_{Guid.NewGuid():N}");

    public SortedStringTableConcurrentReadTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void PointLookup_DoesNotCloseConcurrentSegmentEnumeration()
    {
        using var table = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _directory,
            "concurrent-read.sst",
            codec: new NoCompressionCodec(),
            blockSizeBytes: 96);

        var entries = Enumerable.Range(1, 16)
            .Select(i => (
                new SerializableWrapper<int>(i),
                new SerializableWrapper<string>(new string((char)('a' + i - 1), 32)),
                (ulong)i))
            .ToArray();
        table.Write(entries);

        using var enumeration = table.EnumerateBlockEntriesFiltered().GetEnumerator();
        enumeration.MoveNext().Should().BeTrue();

        table.TryGetBlockEntry(new SerializableWrapper<int>(16), out var found)
            .Should().BeTrue();
        found.SequenceNumber.Should().Be(16UL);

        var remaining = 0;
        while (enumeration.MoveNext())
        {
            remaining++;
        }

        remaining.Should().Be(15,
            "point reads and compaction scans share an immutable segment reader");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
