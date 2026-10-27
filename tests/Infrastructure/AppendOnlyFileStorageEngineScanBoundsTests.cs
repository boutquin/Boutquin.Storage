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

using System.Buffers.Binary;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Bounds tests for the append-only engine's recovery read over a multi-MiB log: an interior frame
/// whose header is damaged, with intact frames ahead of it, must fail loud after reading the
/// file once — the header checksum detects the damage directly, with no forward scan over the rest
/// of the log.
/// </summary>
public sealed class AppendOnlyFileStorageEngineScanBoundsTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"ScanBoundsTests_{Guid.NewGuid():N}");
    private readonly string _testFileName = "scanbounds.dat";
    private string TestFilePath => Path.Combine(_testDirectory, _testFileName);

    private AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> NewEngine()
    {
        var storageFile = new StorageFile(_testDirectory, _testFileName);
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<int>>();
        return new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>(storageFile, serializer);
    }

    /// <summary>
    /// A multi-MiB log with an interior damaged frame header (intact frames ahead of the damage)
    /// classifies as mid-file corruption and throws, leaving the file untouched.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_MultiMiBLogWithDamagedInteriorHeader_FailsLoud()
    {
        // Arrange: write enough records that the file is multi-MiB.
        var engine = NewEngine();
        const int recordCount = 200_000;
        var batch = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<int>>>(recordCount);
        for (var i = 0; i < recordCount; i++)
        {
            batch.Add(new(new SerializableWrapper<int>(i), new SerializableWrapper<int>(i)));
        }
        await engine.SetBulkAsync(batch);

        var fileLength = new FileInfo(TestFilePath).Length;
        Assert.True(fileLength > 2 * 1024 * 1024, $"fixture must be multi-MiB; was {fileLength} bytes");

        // Damage the SECOND frame's payload-length field (offset 5 in the 17-byte header). The
        // first frame's header records its own payload length, which locates the second frame.
        var data = File.ReadAllBytes(TestFilePath);
        var secondFrameStart = 17 + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(5, 4));
        data[secondFrameStart + 5] ^= 0x40;
        File.WriteAllBytes(TestFilePath, data);

        // Act + Assert: the damaged header is detected and the durable records ahead are kept.
        var corruptEngine = NewEngine();
        await Assert.ThrowsAsync<InvalidDataException>(() => corruptEngine.GetAllItemsAsync());
        Assert.Equal(data.Length, new FileInfo(TestFilePath).Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
