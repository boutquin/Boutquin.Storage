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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Multi-writer enforcement tests for the <see cref="StorageFile"/>-backed append path used by
/// <see cref="AppendOnlyFileStorageEngine{TKey, TValue}"/> and
/// <see cref="AppendOnlyFileStorageEngineWithIndex{TKey, TValue}"/>. Sibling of
/// <c>EventLogMultiWriterCoordinationTests</c> — same multi-writer stale-offset corruption
/// class (two live writer handles over one path, each pinning an independent append offset),
/// different remedy: these engines open a fresh append stream per write (so every write
/// re-derives the true end of file), and <see cref="StorageFile.Open"/> hands that stream out
/// under an exclusive <see cref="FileShare.None"/> lock so OVERLAPPING write windows fail fast
/// instead of overwriting each other's committed bytes.
/// </summary>
public sealed class AppendOnlyStorageMultiWriterEnforcementTests : IDisposable
{
    private readonly string _dir;
    private const string FileName = "multiwriter.dat";

    public AppendOnlyStorageMultiWriterEnforcementTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "append-multiwriter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Open_Append_WhileAnotherInstanceHoldsAnAppendStream_FailsFast()
    {
        // Two StorageFile instances over one path — the shape a second DI container produces.
        using var storageA = new StorageFile(_dir, FileName);
        using var storageB = new StorageFile(_dir, FileName);

        using (storageA.Open(FileMode.Append))
        {
            // While A's writer handle is live, B opening its own append stream would capture
            // the same end offset and the two writers would overwrite each other's committed
            // bytes. The exclusive lock turns that into a fail-fast sharing violation.
            var openB = () => storageB.Open(FileMode.Append);
            openB.Should().Throw<IOException>(
                "a second live append handle over the same path is the stale-offset corruption topology");
        }

        // A's handle is gone — B may now open, and its fresh stream starts at the live end.
        using (var streamB = storageB.Open(FileMode.Append))
        {
            streamB.CanWrite.Should().BeTrue();
        }
    }

    [Fact]
    public async Task InterleavedWrites_FromTwoEngineInstances_PreserveEveryEntry()
    {
        // Non-overlapping interleave: each engine's write opens a FRESH append stream, which
        // re-derives the true end of file — so no write lands at a stale offset even though a
        // second live engine violates the documented single-mutator contract. (Overlapping
        // windows are covered by the fail-fast test above.)
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var storageA = new StorageFile(_dir, FileName);
        using var storageB = new StorageFile(_dir, FileName);
        var engineA = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageA, serializer);
        var engineB = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageB, serializer);

        await engineA.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a-1"));
        await engineB.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b-2-with-a-longer-payload"));
        await engineA.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("a-3"));
        await engineB.SetAsync(new SerializableWrapper<int>(4), new SerializableWrapper<string>("b-4"));

        // A fresh reader must see every acknowledged entry — nothing buried, nothing sheared.
        using var readerFile = new StorageFile(_dir, FileName);
        var reader = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(readerFile, serializer);
        var items = (await reader.GetAllItemsAsync()).ToList();

        items.Select(i => i.Key.Value).Should().Equal(1, 2, 3, 4);
        items.Select(i => i.Value.Value).Should().Equal("a-1", "b-2-with-a-longer-payload", "a-3", "b-4");
    }

    [Fact]
    public async Task InterleavedWrites_FromTwoIndexedEngineInstances_PreserveEveryEntryOnDisk()
    {
        // Same shape for the indexed engine: cross-instance interleave stales each instance's
        // in-memory index (a documented contract violation), but the LOG itself must stay
        // intact — a fresh engine rebuilding its index from the file sees every entry.
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var storageA = new StorageFile(_dir, FileName);
        using var storageB = new StorageFile(_dir, FileName);
        var engineA = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
            storageA, serializer, new InMemoryFileIndex<SerializableWrapper<int>>());
        var engineB = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
            storageB, serializer, new InMemoryFileIndex<SerializableWrapper<int>>());

        await engineA.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a-1"));
        await engineB.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b-2"));
        await engineA.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("a-3"));

        using var readerFile = new StorageFile(_dir, FileName);
        var reader = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
            readerFile, serializer, new InMemoryFileIndex<SerializableWrapper<int>>());

        var items = (await reader.GetAllItemsAsync()).ToList();
        items.Select(i => i.Key.Value).Should().Equal(1, 2, 3);

        // The rebuilt index serves point reads for every writer's entries.
        var (value, found) = await reader.TryGetValueAsync(new SerializableWrapper<int>(2));
        found.Should().BeTrue();
        value.Value.Should().Be("b-2");
    }
}
