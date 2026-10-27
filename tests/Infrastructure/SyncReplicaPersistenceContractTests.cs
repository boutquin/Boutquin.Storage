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
/// Pins how the file-backed sync journal and shard-map snapshot identify their physical file: every
/// spelling the volume resolves to one file shares one gate, and an atomic replace lands on that file.
/// </summary>
public sealed class SyncReplicaPersistenceContractTests
{
    // Each pair names one directory entry on a case-insensitive, normalization-insensitive volume
    // (APFS, HFS+): full Unicode case folding maps both spellings to the same canonical caseless form.
    [Theory]
    [InlineData("\u00DF.json", "\u1E9E.json")]
    [InlineData("\u00DF.json", "ss.json")]
    [InlineData("a\u0130.json", "ai\u0307.json")]
    [InlineData("\uFB00.json", "FF.json")]
    [InlineData("\u03C2.json", "\u03A3.json")]
    public void CoordinationKey_UnicodeCaseSpellingsOfOneFile_ShareOneKey(string first, string second)
    {
        var directory = Path.Combine(Path.GetTempPath(), "coordination-key");

        FileCoordinationKey.For(Path.Combine(directory, first)).Should().Be(
            FileCoordinationKey.For(Path.Combine(directory, second)),
            "a volume that folds case resolves both spellings to one file, and under-coordinating one file loses acknowledged writes");
    }

    [Fact]
    public async Task UnicodeCaseAliasedJournalPaths_OnFoldingVolume_DoNotLoseAcknowledgedAppends()
    {
        var directory = Directory.CreateTempSubdirectory("sync-unicode-case-");
        try
        {
            var lower = Path.Combine(directory.FullName, "\u00DF.json");
            var upper = Path.Combine(directory.FullName, "\u1E9E.json");
            await File.WriteAllTextAsync(lower, string.Empty).ConfigureAwait(true);
            if (!File.Exists(upper))
            {
                return; // The volume keeps the two spellings as distinct files.
            }

            File.Delete(lower);
            var left = await OpenKeyValue(lower, "device-a").ConfigureAwait(true);
            var right = await OpenKeyValue(upper, "device-a").ConfigureAwait(true);
            const int perSide = 40;

            await Task.WhenAll(
                Task.Run(() => AppendMany(left, "left", perSide)),
                Task.Run(() => AppendMany(right, "right", perSide))).ConfigureAwait(true);

            (await left.ReadAllAsync().ConfigureAwait(true)).Should().HaveCount(2 * perSide,
                "both spellings address one physical journal, so every acknowledged append must survive");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AppendThroughSymlinkedJournal_ReplacesTheTargetAndKeepsTheLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating symbolic links needs elevated rights on Windows.
        }

        var directory = Directory.CreateTempSubdirectory("sync-leaf-link-");
        try
        {
            var real = Path.Combine(directory.FullName, "journal.json");
            var link = Path.Combine(directory.FullName, "journal-link.json");
            var viaReal = await OpenKeyValue(real, "device-a").ConfigureAwait(true);
            await viaReal.AppendLocalAsync(new("k1", "first", 1, false)).ConfigureAwait(true);
            File.CreateSymbolicLink(link, real);
            var viaLink = await OpenKeyValue(link, "device-a").ConfigureAwait(true);

            await viaLink.AppendLocalAsync(new("k2", "second", 2, false)).ConfigureAwait(true);

            new FileInfo(link).LinkTarget.Should().NotBeNull("an append through the link must not replace the link itself");
            (await viaReal.ReadAllAsync().ConfigureAwait(true)).Select(change => change.Value.Value)
                .Should().Equal(["first", "second"], "the link and its target name one journal");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TrySaveAsyncThroughSymlinkedSnapshot_ReplacesTheTargetAndKeepsTheLink()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating symbolic links needs elevated rights on Windows.
        }

        var directory = Directory.CreateTempSubdirectory("shard-leaf-link-");
        try
        {
            var real = Path.Combine(directory.FullName, "assignments.json");
            var link = Path.Combine(directory.FullName, "assignments-link.json");
            (await new JsonFileShardAssignmentStore(real).TrySaveAsync(
                new ShardMapSnapshot(0, [new ShardAssignment("s0", "n0")]), expectedVersion: -1).ConfigureAwait(true))
                .Should().BeTrue();
            File.CreateSymbolicLink(link, real);

            (await new JsonFileShardAssignmentStore(link).TrySaveAsync(
                new ShardMapSnapshot(1, [new ShardAssignment("s0", "n1")]), expectedVersion: 0).ConfigureAwait(true))
                .Should().BeTrue();

            new FileInfo(link).LinkTarget.Should().NotBeNull("a save through the link must not replace the link itself");
            var reloaded = await new JsonFileShardAssignmentStore(real).LoadAsync().ConfigureAwait(true);
            reloaded!.Version.Should().Be(1, "the link and its target name one snapshot");
            reloaded.Assignments.Single().NodeId.Should().Be("n1");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // The kernel applies ".." to the directory a symbolic link leads to, not to the link's own parent,
    // so a target that climbs out of a linked directory names a different file than its lexical spelling.
    [Fact]
    public async Task AppendThroughLinkWhoseAbsoluteTargetClimbsOutOfALinkedDirectory_LandsInTheFileTheKernelOpens()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating symbolic links needs elevated rights on Windows.
        }

        var directory = Directory.CreateTempSubdirectory("sync-dotdot-absolute-");
        try
        {
            var root = directory.FullName;
            var linkedDirectory = Directory.CreateDirectory(Path.Combine(root, "real", "dir")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(root, "alias"), linkedDirectory);
            var physical = Path.Combine(root, "real", "journal.json");
            var link = Path.Combine(root, "journal-link.json");
            File.CreateSymbolicLink(link, Path.Combine(root, "alias", "..", "journal.json"));
            var viaReal = await OpenKeyValue(physical, "device-a").ConfigureAwait(true);
            await viaReal.AppendLocalAsync(new("k1", "first", 1, false)).ConfigureAwait(true);
            var viaLink = await OpenKeyValue(link, "device-a").ConfigureAwait(true);

            await viaLink.AppendLocalAsync(new("k2", "second", 2, false)).ConfigureAwait(true);

            (await viaLink.ReadAllAsync().ConfigureAwait(true)).Select(change => change.Value.Value)
                .Should().Equal(["first", "second"], "the append must land in the file the kernel opens through the link");
            (await viaReal.ReadAllAsync().ConfigureAwait(true)).Should().HaveCount(2, "the link and its physical target name one journal");
            File.Exists(Path.Combine(root, "journal.json")).Should().BeFalse("a lexically collapsed spelling must not receive a stray journal");
            FileCoordinationKey.For(link).Should().Be(FileCoordinationKey.For(physical), "both spellings name one file, so they must share one gate");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AppendThroughLinkWhoseRelativeTargetClimbsOutOfALinkedDirectory_LandsInTheFileTheKernelOpens()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating symbolic links needs elevated rights on Windows.
        }

        var directory = Directory.CreateTempSubdirectory("sync-dotdot-relative-");
        try
        {
            var root = directory.FullName;
            Directory.CreateDirectory(Path.Combine(root, "a"));
            var linkedDirectory = Directory.CreateDirectory(Path.Combine(root, "elsewhere", "sub")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(root, "a", "dirlink"), linkedDirectory);
            var physical = Path.Combine(root, "elsewhere", "real.json");
            var link = Path.Combine(root, "a", "journal.json");
            File.CreateSymbolicLink(link, Path.Combine("dirlink", "..", "real.json"));
            var viaReal = await OpenKeyValue(physical, "device-a").ConfigureAwait(true);
            await viaReal.AppendLocalAsync(new("k1", "first", 1, false)).ConfigureAwait(true);
            var viaLink = await OpenKeyValue(link, "device-a").ConfigureAwait(true);

            await viaLink.AppendLocalAsync(new("k2", "second", 2, false)).ConfigureAwait(true);
            await viaLink.AppendLocalAsync(new("k3", "third", 3, false)).ConfigureAwait(true);

            (await viaLink.ReadAllAsync().ConfigureAwait(true)).Select(change => change.ChangeId)
                .Should().Equal(["device-a:1", "device-a:2", "device-a:3"],
                    "every acknowledged append must be readable through the spelling that wrote it, with distinct identifiers");
            File.Exists(Path.Combine(root, "a", "real.json")).Should().BeFalse("a lexically collapsed spelling must not receive a stray journal");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TrySaveAsyncThroughLinkWhoseTargetClimbsOutOfALinkedDirectory_IsServedByLaterLoads()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating symbolic links needs elevated rights on Windows.
        }

        var directory = Directory.CreateTempSubdirectory("shard-dotdot-");
        try
        {
            var root = directory.FullName;
            Directory.CreateDirectory(Path.Combine(root, "a"));
            var linkedDirectory = Directory.CreateDirectory(Path.Combine(root, "elsewhere", "sub")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(root, "a", "dirlink"), linkedDirectory);
            var physical = Path.Combine(root, "elsewhere", "real-assignments.json");
            var link = Path.Combine(root, "a", "assignments.json");
            File.CreateSymbolicLink(link, Path.Combine("dirlink", "..", "real-assignments.json"));
            (await new JsonFileShardAssignmentStore(physical).TrySaveAsync(
                new ShardMapSnapshot(0, [new ShardAssignment("s0", "n0")]), expectedVersion: -1).ConfigureAwait(true))
                .Should().BeTrue();
            var viaLink = new JsonFileShardAssignmentStore(link);

            (await viaLink.TrySaveAsync(
                new ShardMapSnapshot(1, [new ShardAssignment("s0", "n1")]), expectedVersion: 0).ConfigureAwait(true))
                .Should().BeTrue();

            (await viaLink.LoadAsync().ConfigureAwait(true))!.Version.Should().Be(1,
                "an acknowledged save must be what the next load through the same spelling serves");
            (await new JsonFileShardAssignmentStore(physical).LoadAsync().ConfigureAwait(true))!.Assignments.Single().NodeId
                .Should().Be("n1");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static Task<JsonFileSyncReplica<KeyValueSyncChange<string, string>>> OpenKeyValue(string path, string replicaId) =>
        JsonFileSyncReplica<KeyValueSyncChange<string, string>>.CreateAsync(path, new FixedDeviceReplicaIdProvider(replicaId));

    private static async Task AppendMany(JsonFileSyncReplica<KeyValueSyncChange<string, string>> replica, string side, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await replica.AppendLocalAsync(new($"{side}-{i}", "v", i, false)).ConfigureAwait(true);
        }
    }
}
