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
/// Crash recovery of the append-only key-value engine (plain and indexed) after a torn
/// <c>SetBulkAsync</c>. A bulk append writes every frame of the batch and fsyncs once, so a crash
/// can land a prefix that ends inside one of the batch's frames while the extent of the rest of the
/// batch reads as zeros. Nothing of the batch was acknowledged, so recovery must serve exactly the
/// records committed before it — and a nonzero byte anywhere after the damaged frame still fails
/// loud, because that is no longer the shape of a single interrupted write.
/// </summary>
public sealed class AppendLogBulkTearRecoveryTests : IDisposable
{
    private const int FrameHeaderSize = 17;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"AppendLogBulkTear_{Guid.NewGuid():N}");

    private string LogPath => Path.Combine(_dir, "log.dat");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> NewEngine(bool indexed)
    {
        var storageFile = new StorageFile(_dir, "log.dat");
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        return indexed
            ? new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
                storageFile, serializer, new InMemoryFileIndex<SerializableWrapper<int>>())
            : new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, serializer);
    }

    private static KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>> Item(int key)
        => new(new SerializableWrapper<int>(key), new SerializableWrapper<string>($"value-{key}-{new string('v', 40)}"));

    /// <summary>
    /// Three acknowledged single appends, then one two-frame bulk append. Returns the offset at
    /// which the bulk batch starts (the end of the committed prefix).
    /// </summary>
    private async Task<long> WriteCommittedThenBulkAsync(bool indexed)
    {
        var engine = NewEngine(indexed);
        for (var k = 1; k <= 3; k++)
        {
            await engine.SetAsync(Item(k).Key, Item(k).Value);
        }

        var batchStart = new FileInfo(LogPath).Length;
        await engine.SetBulkAsync([Item(4), Item(5)]);
        return batchStart;
    }

    /// <summary>
    /// The bulk batch's first frame landed its header and <paramref name="landedPayloadBytes"/>
    /// payload bytes; every later byte of the batch (the rest of that payload and the whole second
    /// frame) reads as zeros. Recovery serves the three committed records, the next append lands
    /// cleanly behind them, and an indexed point read of a committed key still resolves.
    /// </summary>
    [Theory]
    [InlineData(false, 2)]
    [InlineData(false, 10)]
    [InlineData(true, 2)]
    [InlineData(true, 10)]
    public async Task BulkWriteTornInsideAFramePayload_WithZerosThroughTheBatch_IsATornTail(bool indexed, int landedPayloadBytes)
    {
        var batchStart = await WriteCommittedThenBulkAsync(indexed);
        var image = await File.ReadAllBytesAsync(LogPath);
        Array.Clear(image, (int)batchStart + FrameHeaderSize + landedPayloadBytes, image.Length - ((int)batchStart + FrameHeaderSize + landedPayloadBytes));
        await File.WriteAllBytesAsync(LogPath, image);

        List<int> recovered = null;
        var recoveryError = await Record.ExceptionAsync(async () =>
            recovered = (await NewEngine(indexed).GetAllItemsAsync()).Select(e => e.Key.Value).ToList());
        Assert.Null(recoveryError);
        Assert.Equal([1, 2, 3], recovered);

        var engine = NewEngine(indexed);
        await engine.SetAsync(Item(9).Key, Item(9).Value);
        Assert.Equal([1, 2, 3, 9], (await NewEngine(indexed).GetAllItemsAsync()).Select(e => e.Key.Value).ToList());
        Assert.Equal(batchStart + AppendOnlyFrames.Encode(
            new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>(), Item(9).Key, Item(9).Value).Length,
            new FileInfo(LogPath).Length);

        var (value, found) = await NewEngine(indexed).TryGetValueAsync(new SerializableWrapper<int>(2));
        Assert.True(found);
        Assert.Equal(Item(2).Value.Value, value.Value);
    }

    /// <summary>
    /// Must-fail-loud: the same torn payload, but a nonzero byte sits in the zero run after the
    /// damaged frame. That is not the shape of one interrupted write, so recovery and the next
    /// append throw and the image is never truncated.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkWriteTornPayload_WithANonZeroByteAfterIt_FailsLoud(bool indexed)
    {
        var batchStart = await WriteCommittedThenBulkAsync(indexed);
        var image = await File.ReadAllBytesAsync(LogPath);
        var landed = (int)batchStart + FrameHeaderSize + 4;
        Array.Clear(image, landed, image.Length - landed);
        image[^3] = 0x5A;
        await File.WriteAllBytesAsync(LogPath, image);

        await Assert.ThrowsAsync<InvalidDataException>(() => NewEngine(indexed).GetAllItemsAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => NewEngine(indexed).SetAsync(Item(9).Key, Item(9).Value));
        Assert.Equal(image, await File.ReadAllBytesAsync(LogPath));
    }
}
