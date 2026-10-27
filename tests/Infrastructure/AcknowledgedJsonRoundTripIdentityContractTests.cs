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
using System.Text.Json;
using System.Text.Json.Serialization;

using Boutquin.Storage.Distributed.Interfaces.Sync;
using Boutquin.Storage.Infrastructure.Cdc;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.Schemas;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the contract every JSON-persisting store shares: a write is acknowledged only when the bytes it
/// persists read back as the value that was written. System.Text.Json rewrites an unpaired surrogate as
/// U+FFFD, and option transforms (key policies, ignore conditions) can change content on write while the
/// reload stays self-consistent, so a check that only asks "does it decode" or "does the reload
/// re-serialize to the file" acknowledges altered content.
/// </summary>
public sealed class AcknowledgedJsonRoundTripIdentityContractTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("json-roundtrip-identity-");

    public void Dispose() => _directory.Delete(recursive: true);

    private string PathFor(string name) => Path.Combine(_directory.FullName, name);

    // ---------------------------------------------------------------- AppendOnlyEventLog

    [Fact]
    public async Task EventLog_LoneSurrogateEvent_IsRefusedBeforeAnyPositionIsAcknowledged()
    {
        using var log = await AppendOnlyEventLog<string>.CreateAsync(PathFor("events.log")).ConfigureAwait(true);

        var high = () => log.AppendAsync("key\uD800").AsTask();
        var low = () => log.AppendAsync("key\uDBFF").AsTask();

        await high.Should().ThrowAsync<ArgumentException>("JSON would replay the event as key�").ConfigureAwait(true);
        await low.Should().ThrowAsync<ArgumentException>("two distinct keys would replay as one").ConfigureAwait(true);
        (await log.AppendAsync("key😀").ConfigureAwait(true)).Should().Be(0,
            "a well-formed surrogate pair round-trips and takes the first position, since the refusals consumed none");
    }

    // ---------------------------------------------------------------- JsonVersionedSerializer

    [Fact]
    public async Task JsonVersionedSerializer_LoneSurrogate_IsRefusedAndWritesNothing()
    {
        var serializer = new JsonVersionedSerializer<string>(requireExtensionData: false);
        using var stream = new MemoryStream();

        var write = () => serializer.SerializeAsync(stream, "a\uD800b", new SchemaVersion(1)).AsTask();

        await write.Should().ThrowAsync<ArgumentException>("the body would read back as a�b").ConfigureAwait(true);
        stream.Length.Should().Be(0);
    }

    // ---------------------------------------------------------------- JsonFileSyncReplica

    public sealed record ObjectSetEnvelope(string Label, HashSet<object> Items);

    public sealed record ReadOnlyCollections(IReadOnlyList<string> Names, IReadOnlyDictionary<string, int> Counts);

    public sealed record CountedMap(Dictionary<string, int> Counts);

    public sealed record WithDefault(string Label, int Count = 5);

    public sealed class GetOnlySecret
    {
        public GetOnlySecret()
        {
        }

        public GetOnlySecret(string label, int secret)
        {
            Label = label;
            Secret = secret;
        }

        public string Label { get; } = string.Empty;

        public int Secret { get; }
    }

    private Task<JsonFileSyncReplica<T>> CreateReplicaAsync<T>(JsonSerializerOptions? options = null) =>
        JsonFileSyncReplica<T>.CreateAsync(PathFor("replica.json"), new FixedDeviceReplicaIdProvider("dev"), options);

    [Fact]
    public async Task Sync_GCounterInsideAHashSetOfObject_IsRefused()
    {
        var replica = await CreateReplicaAsync<ObjectSetEnvelope>().ConfigureAwait(true);
        var counter = new GCounter();
        counter.Increment("a");

        var append = () => replica.AppendLocalAsync(new ObjectSetEnvelope("set", [counter]));

        await append.Should().ThrowAsync<NotSupportedException>(
            "a set element declared as object reloads as a bare JSON document").ConfigureAwait(true);
        File.Exists(PathFor("replica.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Sync_LamportTimestampInsideAHashSetOfObject_IsRefused()
    {
        var replica = await CreateReplicaAsync<ObjectSetEnvelope>().ConfigureAwait(true);
        await replica.AppendLocalAsync(new ObjectSetEnvelope("empty", [])).ConfigureAwait(true);

        var append = () => replica.AppendLocalAsync(new ObjectSetEnvelope("clock", [new LamportTimestamp("node-a", 42)]));

        await append.Should().ThrowAsync<NotSupportedException>(
            "the timestamp's private counter would be lost on reload").ConfigureAwait(true);
        (await replica.ReadAllAsync().ConfigureAwait(true)).Should().ContainSingle("the earlier empty set remains intact");
    }

    [Fact]
    public async Task Sync_ScalarInsideAHashSetOfObject_IsRefused()
    {
        var replica = await CreateReplicaAsync<ObjectSetEnvelope>().ConfigureAwait(true);

        var append = () => replica.AppendLocalAsync(new ObjectSetEnvelope("scalars", ["a", 7L]));

        await append.Should().ThrowAsync<NotSupportedException>(
            "object-declared scalar elements reload as JsonElement, changing the CLR values").ConfigureAwait(true);
        File.Exists(PathFor("replica.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Sync_InterfaceDeclaredCollectionsBuiltFromOtherImplementations_AreAcknowledged()
    {
        var replica = await CreateReplicaAsync<ReadOnlyCollections>().ConfigureAwait(true);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["b"] = 2, ["a"] = 1 };

        await replica.AppendLocalAsync(new ReadOnlyCollections(new[] { "a", "b" }, counts)).ConfigureAwait(true);

        var reloaded = (await replica.ReadAllAsync().ConfigureAwait(true)).Should().ContainSingle().Subject.Value;
        reloaded.Names.Should().Equal("a", "b");
        reloaded.Counts.Should().BeEquivalentTo(counts);
    }

    [Fact]
    public async Task Sync_LoneSurrogatePayload_IsRefused()
    {
        var replica = await CreateReplicaAsync<string>().ConfigureAwait(true);

        var append = () => replica.AppendLocalAsync("x\uD800");

        await append.Should().ThrowAsync<NotSupportedException>("the change would reload as x�").ConfigureAwait(true);
        File.Exists(PathFor("replica.json")).Should().BeFalse();
    }

    public static TheoryData<string> WriteSideTransforms => new() { "dictionary-key-policy", "when-writing-default", "ignore-read-only" };

    [Theory]
    [MemberData(nameof(WriteSideTransforms))]
    public async Task Sync_WriteSideOptionTransform_ThatChangesContent_IsRefused(string transform)
    {
        Func<Task> append;
        switch (transform)
        {
            case "dictionary-key-policy":
                {
                    var replica = await CreateReplicaAsync<CountedMap>(
                        new JsonSerializerOptions { DictionaryKeyPolicy = JsonNamingPolicy.CamelCase }).ConfigureAwait(true);
                    append = () => replica.AppendLocalAsync(new CountedMap(new Dictionary<string, int> { ["Phone"] = 1 }));
                    break;
                }

            case "when-writing-default":
                {
                    var replica = await CreateReplicaAsync<WithDefault>(
                        new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault }).ConfigureAwait(true);
                    append = () => replica.AppendLocalAsync(new WithDefault("zero", 0));
                    break;
                }

            default:
                {
                    var replica = await CreateReplicaAsync<GetOnlySecret>(
                        new JsonSerializerOptions { IgnoreReadOnlyProperties = true }).ConfigureAwait(true);
                    append = () => replica.AppendLocalAsync(new GetOnlySecret("label", 42));
                    break;
                }
        }

        await append.Should().ThrowAsync<NotSupportedException>(
            $"the {transform} transform changes the acknowledged content on write").ConfigureAwait(true);
        File.Exists(PathFor("replica.json")).Should().BeFalse();
    }

    private sealed class ReturningProvider(string id) : IDeviceReplicaIdProvider
    {
        public Task<string> GetReplicaIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(id);
    }

    [Fact]
    public async Task Sync_CreateAsync_RefusesAReplicaIdentifierJsonCannotCarry()
    {
        var create = () => JsonFileSyncReplica<string>.CreateAsync(PathFor("replica.json"), new ReturningProvider("ph\uD800"));

        await create.Should().ThrowAsync<ArgumentException>(
            "the identifier would persist as ph� and no later load could match it").ConfigureAwait(true);
    }

    [Fact]
    public void Sync_FixedDeviceReplicaIdProvider_RefusesAnIdentifierJsonCannotCarry()
    {
        var create = () => new FixedDeviceReplicaIdProvider("ph\uDC00");

        create.Should().Throw<ArgumentException>();
    }

    // ---------------------------------------------------------------- JsonFileShardAssignmentStore

    [Fact]
    public async Task Shard_SnapshotJsonCannotCarry_IsRefusedAndTheStoreStaysUsable()
    {
        var store = new JsonFileShardAssignmentStore(PathFor("assignments.json"));
        var snapshot = new ShardMapSnapshot(0, [new ShardAssignment("s\uD800", "n1"), new ShardAssignment("s\uDBFF", "n2")]);

        var save = () => store.TrySaveAsync(snapshot, expectedVersion: -1);

        await save.Should().ThrowAsync<ArgumentException>("both shard ids would persist as s�").ConfigureAwait(true);
        (await store.LoadAsync().ConfigureAwait(true)).Should().BeNull();
        (await store.TrySaveAsync(new ShardMapSnapshot(0, [new ShardAssignment("s1", "n1")]), -1).ConfigureAwait(true))
            .Should().BeTrue("a refused save must not wedge the store");
    }

    // ---------------------------------------------------------------- CheckpointedLogCompactedTopic

    [Theory]
    [InlineData("number")]
    [InlineData("string")]
    [InlineData("boolean")]
    public async Task CompactedTopic_ObjectDeclaredScalar_IsRefusedBeforeSaving(string kind)
    {
        var store = new InMemoryCheckpointStore();
        var topic = new CheckpointedLogCompactedTopic<string, object>(store, "scalar");
        await topic.RestoreAsync().ConfigureAwait(true);
        object scalar = kind switch { "number" => 5, "string" => "value", _ => true };

        var append = () => topic.AppendAsync("key", scalar, ChangeOperation.Insert).AsTask();

        await append.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        (await store.LoadAsync("scalar").ConfigureAwait(true)).Should().BeNull();
        var restored = new CheckpointedLogCompactedTopic<string, object>(store, "scalar");
        await restored.RestoreAsync().ConfigureAwait(true);
        (await restored.CaptureSnapshotAsync().ConfigureAwait(true)).Records.Should().BeEmpty();
    }

    [Fact]
    public async Task JsonVersionedSerializer_ObjectDeclaredScalar_IsRefusedBeforeWriting()
    {
        var serializer = new JsonVersionedSerializer<object>(requireExtensionData: false);
        using var stream = new MemoryStream();

        var write = () => serializer.SerializeAsync(stream, 5, new SchemaVersion(1)).AsTask();

        await write.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        stream.Length.Should().Be(0);
    }

    [Fact]
    public async Task CompactedTopic_KeysJsonCannotCarry_AreRefusedAndTheTopicStaysRestorable()
    {
        var store = new InMemoryCheckpointStore();
        var topic = new CheckpointedLogCompactedTopic<string, string>(store, "topic");
        await topic.RestoreAsync().ConfigureAwait(true);
        await topic.AppendAsync("plain", "v", ChangeOperation.Insert).ConfigureAwait(true);

        var badKey = () => topic.AppendAsync("a\uD800", "v", ChangeOperation.Insert).AsTask();
        var badValue = () => topic.AppendAsync("k", "v\uDC00", ChangeOperation.Insert).AsTask();

        await badKey.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        await badValue.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        var reopened = new CheckpointedLogCompactedTopic<string, string>(store, "topic");
        await reopened.RestoreAsync().ConfigureAwait(true);
        (await reopened.CaptureSnapshotAsync().ConfigureAwait(true)).Records.Should().ContainSingle()
            .Which.Key.Should().Be("plain");
    }

    public sealed record LossyKey(string Id)
    {
        [JsonIgnore]
        public int Shard { get; init; }
    }

    [Fact]
    public async Task CompactedTopic_DistinctKeysThatCollapseOnDisk_AreRefused()
    {
        var store = new InMemoryCheckpointStore();
        var topic = new CheckpointedLogCompactedTopic<LossyKey, string>(store, "topic");
        await topic.RestoreAsync().ConfigureAwait(true);
        await topic.AppendAsync(new LossyKey("k") { Shard = 1 }, "v1", ChangeOperation.Insert).ConfigureAwait(true);

        var collide = () => topic.AppendAsync(new LossyKey("k") { Shard = 2 }, "v2", ChangeOperation.Insert).AsTask();

        await collide.Should().ThrowAsync<ArgumentException>(
            "the two keys persist identically, so the checkpoint would hold a duplicate key RestoreAsync refuses").ConfigureAwait(true);
        var reopened = new CheckpointedLogCompactedTopic<LossyKey, string>(store, "topic");
        await reopened.RestoreAsync().ConfigureAwait(true);
        (await reopened.CaptureSnapshotAsync().ConfigureAwait(true)).Records.Should().ContainSingle();
    }
}
