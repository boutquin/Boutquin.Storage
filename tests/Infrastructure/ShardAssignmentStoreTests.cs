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

public sealed class ShardAssignmentStoreTests
{
    [Fact]
    public async Task TrySaveAsync_PersistsSnapshotAcrossStoreInstances()
    {
        var directory = Directory.CreateTempSubdirectory("shard-map-");
        var path = System.IO.Path.Combine(directory.FullName, "assignments.json");
        try
        {
            var first = new JsonFileShardAssignmentStore(path);
            var snapshot = new ShardMapSnapshot(0,
                [new ShardAssignment("shard-0000", "node-a")]);

            (await first.TrySaveAsync(snapshot, expectedVersion: -1)).Should().BeTrue();

            var reopened = new JsonFileShardAssignmentStore(path);
            var loaded = await reopened.LoadAsync();
            loaded.Should().NotBeNull();
            loaded!.Version.Should().Be(0);
            loaded.Assignments.Should().ContainSingle().Which.Should().BeEquivalentTo(snapshot.Assignments[0]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TrySaveAsync_RejectsAStaleExpectedVersion()
    {
        var directory = Directory.CreateTempSubdirectory("shard-map-");
        var path = System.IO.Path.Combine(directory.FullName, "assignments.json");
        try
        {
            var store = new JsonFileShardAssignmentStore(path);
            var original = new ShardMapSnapshot(4,
                [new ShardAssignment("shard-0000", "node-a")]);
            (await store.TrySaveAsync(original, expectedVersion: -1)).Should().BeTrue();

            var staleWrite = new ShardMapSnapshot(5,
                [new ShardAssignment("shard-0000", "node-b")]);

            (await store.TrySaveAsync(staleWrite, expectedVersion: 3)).Should().BeFalse();
            (await store.LoadAsync())!.Assignments[0].NodeId.Should().Be("node-a");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
    [Theory]
    [InlineData("negative-version", """{"version":-1,"assignments":[{"shardId":"s1","nodeId":"n1"}]}""")]
    [InlineData("null-assignments", """{"version":1,"assignments":null}""")]
    [InlineData("null-entry", """{"version":1,"assignments":[null]}""")]
    [InlineData("blank-node", """{"version":1,"assignments":[{"shardId":"s1","nodeId":" "}]}""")]
    [InlineData("missing-shard", """{"version":1,"assignments":[{"nodeId":"n1"}]}""")]
    [InlineData("duplicate-shard", """{"version":1,"assignments":[{"shardId":"s1","nodeId":"n1"},{"shardId":"s1","nodeId":"n2"}]}""")]
    public async Task LoadAsync_CorruptButParseableSnapshot_ThrowsInvalidDataException(string label, string json)
    {
        var directory = Directory.CreateTempSubdirectory("shard-map-corrupt-");
        var path = System.IO.Path.Combine(directory.FullName, "assignments.json");
        try
        {
            await File.WriteAllTextAsync(path, json);
            var store = new JsonFileShardAssignmentStore(path);

            var load = () => store.LoadAsync();
            await load.Should().ThrowAsync<InvalidDataException>(
                $"file content '{label}' is corruption, not caller argument misuse");

            var save = () => store.TrySaveAsync(
                new ShardMapSnapshot(2, [new ShardAssignment("s1", "n1")]), expectedVersion: 1);
            await save.Should().ThrowAsync<InvalidDataException>(
                $"the optimistic-concurrency read over corrupt content '{label}' must report corruption, not a malformed plan");
            (await File.ReadAllTextAsync(path)).Should().Be(json, "a corrupt snapshot must not be replaced");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public async Task TrySaveAsync_NeverAcknowledgesASnapshotThatLoadAsyncRejects()
    {
        var directory = Directory.CreateTempSubdirectory("shard-map-dup-save-");
        var path = System.IO.Path.Combine(directory.FullName, "assignments.json");
        try
        {
            var store = new JsonFileShardAssignmentStore(path);

            var save = () => store.TrySaveAsync(
                new ShardMapSnapshot(0, [new ShardAssignment("s1", "n1"), new ShardAssignment("s1", "n2")]),
                expectedVersion: -1);

            await save.Should().ThrowAsync<ArgumentException>(
                "a snapshot that assigns a shard twice is one the loader rejects, so it must never be written").ConfigureAwait(true);
            File.Exists(path).Should().BeFalse();
            (await store.LoadAsync().ConfigureAwait(true)).Should().BeNull();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TrySaveAsync_DuplicateShardSnapshotOverExistingMap_LeavesTheStoreLoadableAndWritable()
    {
        var directory = Directory.CreateTempSubdirectory("shard-map-dup-repair-");
        var path = System.IO.Path.Combine(directory.FullName, "assignments.json");
        try
        {
            var store = new JsonFileShardAssignmentStore(path);
            (await store.TrySaveAsync(new ShardMapSnapshot(0, [new ShardAssignment("s1", "n1")]), -1).ConfigureAwait(true))
                .Should().BeTrue();

            try
            {
                await store.TrySaveAsync(
                    new ShardMapSnapshot(1, [new ShardAssignment("s1", "n1"), new ShardAssignment("s1", "n2")]),
                    expectedVersion: 0).ConfigureAwait(true);
            }
            catch (ArgumentException)
            {
                // The duplicate snapshot is refused; the store must remain usable.
            }

            var load = () => store.LoadAsync();
            (await load.Should().NotThrowAsync("the store must not have persisted a snapshot it cannot load").ConfigureAwait(true))
                .Subject!.Version.Should().Be(0, "the persisted map must still be the last valid snapshot");
            (await store.TrySaveAsync(new ShardMapSnapshot(1, [new ShardAssignment("s1", "n2")]), 0).ConfigureAwait(true))
                .Should().BeTrue("the store must stay repairable through its own API");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TrySaveAsync_SymlinkAliasedPaths_ShareOneOptimisticConcurrencyGate()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating directory symlinks needs elevated rights on Windows.
        }

        var directory = Directory.CreateTempSubdirectory("shard-map-alias-");
        try
        {
            var real = Directory.CreateDirectory(System.IO.Path.Combine(directory.FullName, "real")).FullName;
            var alias = System.IO.Path.Combine(directory.FullName, "alias");
            Directory.CreateSymbolicLink(alias, real);
            var doubleWins = 0;
            for (var trial = 0; trial < 40; trial++)
            {
                var name = $"assignments-{trial}.json";
                var left = new JsonFileShardAssignmentStore(System.IO.Path.Combine(real, name));
                var right = new JsonFileShardAssignmentStore(System.IO.Path.Combine(alias, name));
                var snapshot = new ShardMapSnapshot(0, [new ShardAssignment("s1", "n1")]);
                using var start = new Barrier(2);
                var results = await Task.WhenAll(
                    Task.Run(() => { start.SignalAndWait(); return left.TrySaveAsync(snapshot, expectedVersion: -1); }),
                    Task.Run(() => { start.SignalAndWait(); return right.TrySaveAsync(snapshot, expectedVersion: -1); }));
                if (results.All(saved => saved))
                {
                    doubleWins++;
                }
            }

            doubleWins.Should().Be(0,
                "two stores bound to one physical snapshot must not both win the same expected version");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
