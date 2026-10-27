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
using System.Text.Json.Nodes;

namespace Boutquin.Storage.Infrastructure.Tests;

public sealed class SyncPersistenceRegressionTests
{
    [Fact]
    public async Task ForeignChangeCannotReserveTheNextLocalIdentifier()
    {
        var directory = Directory.CreateTempSubdirectory("sync-id-");
        try
        {
            var replica = await Open<string>(directory, "target").ConfigureAwait(true);
            var apply = () => replica.ApplyAsync(new ChangeSet<string>("peer", 0, 1,
                [new("target:1", "peer", 1, "remote")]));
            await apply.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
            (await replica.LoadCheckpointAsync("peer").ConfigureAwait(true)).Position.Should().Be(0);
            (await replica.AppendLocalAsync("local").ConfigureAwait(true)).ChangeId.Should().Be("target:1");
            (await (await Open<string>(directory, "target").ConfigureAwait(true)).ReadAllAsync().ConfigureAwait(true))
                .Should().ContainSingle().Which.Value.Should().Be("local");
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("checkpoint")]
    [InlineData("blank-peer")]
    [InlineData("identifier")]
    [InlineData("null-entries")]
    [InlineData("null-checkpoints")]
    public async Task InvalidPersistedMetadataFailsAtOpen(string corruption)
    {
        var directory = Directory.CreateTempSubdirectory("sync-corrupt-");
        try
        {
            var replica = await Open<string>(directory, "target").ConfigureAwait(true);
            await replica.AppendLocalAsync("own").ConfigureAwait(true);
            var path = Path.Combine(directory.FullName, "target.json");
            var state = JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(true))!;
            switch (corruption)
            {
                case "checkpoint": state["checkpoints"]!["peer"] = -1; break;
                case "blank-peer": state["checkpoints"]![" "] = 0; break;
                case "identifier": state["entries"]![0]!["changeId"] = "peer:1"; break;
                case "null-entries": state["entries"] = null; break;
                case "null-checkpoints": state["checkpoints"] = null; break;
            }
            await File.WriteAllTextAsync(path, state.ToJsonString()).ConfigureAwait(true);
            var reopen = () => Open<string>(directory, "target");
            await reopen.Should().ThrowAsync<InvalidDataException>().ConfigureAwait(true);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task CounterEnvelopeSurvivesColdReopenAndRepeatedExchangeWithPerNodeComponents()
    {
        var directory = Directory.CreateTempSubdirectory("sync-counter-");
        try
        {
            var a = new GCounter(); a.Increment("a"); a.Increment("a");
            var b = new GCounter(); b.Increment("b");
            await (await Open<GCounter>(directory, "left").ConfigureAwait(true)).AppendLocalAsync(a).ConfigureAwait(true);
            await (await Open<GCounter>(directory, "right").ConfigureAwait(true)).AppendLocalAsync(b).ConfigureAwait(true);
            var left = await Open<GCounter>(directory, "left").ConfigureAwait(true);
            var right = await Open<GCounter>(directory, "right").ConfigureAwait(true);
            var engine = new SyncEngine<GCounter, long>(new GCounterSyncAdapter());
            await engine.SynchronizeAsync(left, right).ConfigureAwait(true);
            await engine.SynchronizeAsync(right, left).ConfigureAwait(true);
            (await engine.MaterializeAsync(await Open<GCounter>(directory, "left").ConfigureAwait(true)).ConfigureAwait(true)).Should().Be(3);
            (await engine.MaterializeAsync(await Open<GCounter>(directory, "right").ConfigureAwait(true)).ConfigureAwait(true)).Should().Be(3);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task SetEnvelopeSurvivesColdReopenWithObservedTagsAndTombstones()
    {
        var directory = Directory.CreateTempSubdirectory("sync-set-");
        try
        {
            var before = new ORSet<string>(); before.Add("a", "removed"); before.Add("a", "kept");
            var after = (ORSet<string>)before.Merge(new ORSet<string>()); after.Remove("removed");
            var concurrent = new ORSet<string>(); concurrent.Add("b", "concurrent");
            var left = await Open<ORSet<string>>(directory, "left").ConfigureAwait(true);
            await left.AppendLocalAsync(before).ConfigureAwait(true);
            await left.AppendLocalAsync(after).ConfigureAwait(true);
            var right = await Open<ORSet<string>>(directory, "right").ConfigureAwait(true);
            await right.AppendLocalAsync(concurrent).ConfigureAwait(true);
            left = await Open<ORSet<string>>(directory, "left").ConfigureAwait(true);
            right = await Open<ORSet<string>>(directory, "right").ConfigureAwait(true);
            var engine = new SyncEngine<ORSet<string>, IReadOnlySet<string>>(new ORSetSyncAdapter<string>());
            await engine.SynchronizeAsync(left, right).ConfigureAwait(true);
            await engine.SynchronizeAsync(right, left).ConfigureAwait(true);
            (await engine.MaterializeAsync(await Open<ORSet<string>>(directory, "right").ConfigureAwait(true)).ConfigureAwait(true))
                .Should().BeEquivalentTo(["kept", "concurrent"]);
            var recovered = (await right.ReadAllAsync().ConfigureAwait(true))
                .Single(change => change.OriginReplicaId == "left" && change.OriginSequence == 2).Value;
            recovered.Add("a", "removed");
            await right.AppendLocalAsync(recovered).ConfigureAwait(true);
            await engine.SynchronizeAsync(left, right).ConfigureAwait(true);
            (await engine.MaterializeAsync(await Open<ORSet<string>>(directory, "left").ConfigureAwait(true)).ConfigureAwait(true))
                .Should().BeEquivalentTo(["kept", "concurrent", "removed"]);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task LocalRetryAfterLostAcknowledgementCreatesANewEnvelopeButExchangeReplayDoesNot()
    {
        var directory = Directory.CreateTempSubdirectory("sync-retry-");
        try
        {
            await (await Open<int>(directory, "source").ConfigureAwait(true)).AppendLocalAsync(1).ConfigureAwait(true);
            var reopened = await Open<int>(directory, "source").ConfigureAwait(true);
            await reopened.AppendLocalAsync(1).ConfigureAwait(true);
            var delta = await reopened.ReadChangesAsync(new("source", 0)).ConfigureAwait(true);
            var target = await Open<int>(directory, "target").ConfigureAwait(true);
            (await target.ApplyAsync(delta).ConfigureAwait(true)).Should().Be(2);
            (await target.ApplyAsync(delta).ConfigureAwait(true)).Should().Be(0);
            (await target.ReadAllAsync().ConfigureAwait(true)).Select(change => change.Value).Sum().Should().Be(2);
        }
        finally { directory.Delete(true); }
    }

    private static Task<JsonFileSyncReplica<T>> Open<T>(DirectoryInfo directory, string id) =>
        JsonFileSyncReplica<T>.CreateAsync(Path.Combine(directory.FullName, id + ".json"), new FixedDeviceReplicaIdProvider(id));
}
