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
using Boutquin.Storage.Distributed.Interfaces.DistributedSystems;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the journal-safety properties of <see cref="JsonFileSyncReplica{TChange}"/>: whatever an
/// acknowledged mutation persists, a reopen can read back, and identifiers keep a single meaning.
/// </summary>
public sealed class SyncReplicaJournalSafetyTests
{
    private static readonly JsonSerializerOptions s_camelCaseKeys = new(JsonSerializerDefaults.Web)
    {
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task InterfaceDeclaredGCounterPayload_SurvivesReopenWithComponentCounters()
    {
        var directory = Directory.CreateTempSubdirectory("sync-igcounter-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<IGCounter>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            var counter = new GCounter();
            counter.Increment("a");
            counter.Increment("a");
            counter.Increment("b");
            var appendThenReload = async () =>
            {
                await replica.AppendLocalAsync(counter).ConfigureAwait(true);
                return (await (await JsonFileSyncReplica<IGCounter>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"))
                    .ConfigureAwait(true)).ReadAllAsync().ConfigureAwait(true)).Single().Value;
            };

            var restored = (await appendThenReload.Should().NotThrowAsync(
                "an interface-declared counter must be persisted and stay readable after reopen").ConfigureAwait(true)).Subject;
            var other = new GCounter();
            other.Increment("a");
            other.Increment("a");
            other.Increment("a");

            ((GCounter)restored.Merge(other)).Value.Should().Be(4,
                "the reloaded counter must keep per-node components (a=2, b=1), so merging a=3 yields 3 + 1");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task InterfaceDeclaredPNCounterPayload_IsRefusedBeforeTheJournalIsRewritten()
    {
        var directory = Directory.CreateTempSubdirectory("sync-ipncounter-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<IPNCounter>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            var counter = new PNCounter();
            counter.Increment("a");

            var append = () => replica.AppendLocalAsync(counter);

            await append.Should().ThrowAsync<NotSupportedException>(
                "an interface-declared PNCounter carries the same uncapturable components as the concrete type").ConfigureAwait(true);
            File.Exists(path).Should().BeFalse("a refused payload must not reach the journal");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task InterfaceDeclaredORSetPayload_SurvivesReopenWithTombstones()
    {
        var directory = Directory.CreateTempSubdirectory("sync-iorset-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<IORSet<string>>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            var set = new ORSet<string>();
            set.Add("a", "x");
            set.Add("a", "y");
            set.Remove("x");
            var appendThenReload = async () =>
            {
                await replica.AppendLocalAsync(set).ConfigureAwait(true);
                return (await (await JsonFileSyncReplica<IORSet<string>>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"))
                    .ConfigureAwait(true)).ReadAllAsync().ConfigureAwait(true)).Single().Value;
            };

            var restored = (await appendThenReload.Should().NotThrowAsync(
                "an interface-declared set must be persisted and stay readable after reopen").ConfigureAwait(true)).Subject;
            var stale = new ORSet<string>();
            stale.Add("b", "z");

            restored.Merge(stale).Value.Should().BeEquivalentTo(["y", "z"],
                "the reloaded set must keep its tombstone for 'x' so a merge cannot resurrect it");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ChangeRecordWithInterfaceDeclaredCounter_SurvivesReopenAndLaterAppends()
    {
        var directory = Directory.CreateTempSubdirectory("sync-record-igcounter-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<CounterChange>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            var counter = new GCounter();
            counter.Increment("a");
            counter.Increment("b");
            var reopenAndAppend = async () =>
            {
                await replica.AppendLocalAsync(new CounterChange("visits", counter)).ConfigureAwait(true);
                var reopened = await JsonFileSyncReplica<CounterChange>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
                await reopened.AppendLocalAsync(new CounterChange("visits", new GCounter())).ConfigureAwait(true);
                return (await reopened.ReadAllAsync().ConfigureAwait(true)).Select(change => change.Value.Counter.Value).ToArray();
            };

            var values = (await reopenAndAppend.Should().NotThrowAsync(
                "a member declared by its CRDT interface must not make the journal unopenable").ConfigureAwait(true)).Subject;
            values.Should().Equal(2, 0);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AmbiguousCrdtInterfacePayload_IsRefusedAndEarlierHistoryStaysReadable()
    {
        var directory = Directory.CreateTempSubdirectory("sync-icrdt-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<ICrdt<long>>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            var counter = new GCounter();
            counter.Increment("a");

            var append = () => replica.AppendLocalAsync(counter);

            await append.Should().ThrowAsync<NotSupportedException>(
                "ICrdt<long> cannot say which family a persisted value belongs to, so it must be refused, not stored as Value").ConfigureAwait(true);
            File.Exists(path).Should().BeFalse("a refused payload must not reach the journal");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task VectorClockPayload_SurvivesReopenWithEveryComponent()
    {
        var directory = Directory.CreateTempSubdirectory("sync-vclock-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<VectorClock>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            var clock = new VectorClock();
            clock.Increment("phone");
            clock.Increment("phone");
            clock.Increment("laptop");
            await replica.AppendLocalAsync(clock).ConfigureAwait(true);

            var reloaded = (await (await JsonFileSyncReplica<VectorClock>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"))
                .ConfigureAwait(true)).ReadAllAsync().ConfigureAwait(true)).Single().Value;

            reloaded.GetClock().Should().BeEquivalentTo(new Dictionary<string, long> { ["phone"] = 2, ["laptop"] = 1 },
                "an acknowledged clock must reload with its components, not as an empty clock");
            reloaded.CompareCausally(clock).Should().Be(VectorClockComparison.Equal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GossipProtocolPayload_IsRefusedInsteadOfPersistedAsAnEmptyInstance()
    {
        var directory = Directory.CreateTempSubdirectory("sync-gossip-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<GossipProtocol<string, string>>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"))
                .ConfigureAwait(true);
            var gossip = new GossipProtocol<string, string>();
            gossip.UpdateLocalState("phone", "online");

            var append = () => replica.AppendLocalAsync(gossip);

            await append.Should().ThrowAsync<NotSupportedException>(
                "the protocol keeps its cluster state in private fields, so persisting it would reload an empty instance").ConfigureAwait(true);
            File.Exists(path).Should().BeFalse("a refused payload must not reach the journal");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WriteOnlySerializablePayload_IsRefusedAndTheJournalStaysAuthoritative()
    {
        var directory = Directory.CreateTempSubdirectory("sync-write-only-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<LamportTimestamp>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            var append = () => replica.AppendLocalAsync(new LamportTimestamp("n1", 2));

            await append.Should().ThrowAsync<NotSupportedException>(
                "the payload serializes but cannot be read back, so the replace must be refused").ConfigureAwait(true);
            File.Exists(path).Should().BeFalse("a payload the loader cannot read must not replace the journal");
            (await replica.ReadAllAsync().ConfigureAwait(true)).Should().BeEmpty();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WriteOnlySerializablePayload_AfterAcknowledgedHistory_LeavesThatHistoryReadable()
    {
        var directory = Directory.CreateTempSubdirectory("sync-write-only-history-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<NoteChange>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            await replica.AppendLocalAsync(new NoteChange("kept", null)).ConfigureAwait(true);
            var committed = await File.ReadAllTextAsync(path).ConfigureAwait(true);

            var append = () => replica.AppendLocalAsync(new NoteChange("lossy", new UnreadablePayload(7)));

            await append.Should().ThrowAsync<NotSupportedException>(
                "the new entry serializes but cannot be read back, so the replace must be refused").ConfigureAwait(true);
            (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Be(committed);
            (await replica.ReadAllAsync().ConfigureAwait(true)).Should().ContainSingle();
            (await replica.AppendLocalAsync(new NoteChange("next", null)).ConfigureAwait(true)).ChangeId.Should().Be("dev:2");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReplicasOfDifferentChangeTypes_OverOneJournal_DoNotLoseAcknowledgedAppends()
    {
        var directory = Directory.CreateTempSubdirectory("sync-generic-gate-");
        try
        {
            var path = Path.Combine(directory.FullName, "journal.json");
            // Both open before the journal exists. Whichever type writes first owns the journal and the other is
            // refused from then on; without one shared gate both would load the empty journal and the second
            // replace would discard the first type's acknowledged appends.
            var typed = await JsonFileSyncReplica<string>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            var untyped = await JsonFileSyncReplica<object>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
            const int perSide = 60;

            static async Task<int> AppendAll(Func<int, Task> append)
            {
                var acknowledged = 0;
                for (var i = 0; i < perSide; i++)
                {
                    try
                    {
                        await append(i).ConfigureAwait(true);
                        acknowledged++;
                    }
                    catch (InvalidDataException)
                    {
                        // The journal belongs to the other change type.
                    }
                }

                return acknowledged;
            }

            var counts = await Task.WhenAll(
                Task.Run(() => AppendAll(i => typed.AppendLocalAsync($"typed-{i}"))),
                Task.Run(() => AppendAll(i => untyped.AppendLocalAsync(JsonSerializer.SerializeToElement($"untyped-{i}"))))).ConfigureAwait(true);

            counts.Should().ContainSingle(count => count == perSide, "the first type to write owns the journal")
                .And.ContainSingle(count => count == 0, "the other change type is refused instead of rewriting the journal");
            var survivors = counts[0] == perSide
                ? (await typed.ReadAllAsync().ConfigureAwait(true)).Count
                : (await untyped.ReadAllAsync().ConfigureAwait(true)).Count;
            survivors.Should().Be(perSide, "every acknowledged append to the one physical journal must survive");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RolledBackJournal_ThatReissuesAnAcknowledgedIdentifier_FailsSyncInsteadOfDiverging()
    {
        var directory = Directory.CreateTempSubdirectory("sync-rollback-");
        try
        {
            var leftPath = Path.Combine(directory.FullName, "left.json");
            var rightPath = Path.Combine(directory.FullName, "right.json");
            var snapshotPath = Path.Combine(directory.FullName, "left.snapshot");
            var left = await OpenKeyValue(leftPath, "dev-a").ConfigureAwait(true);
            var right = await OpenKeyValue(rightPath, "dev-b").ConfigureAwait(true);
            var engine = new SyncEngine<KeyValueSyncChange<string, string>, IReadOnlyDictionary<string, string>>(
                new LastWriterWinsConflictResolver<string, string>());
            await left.AppendLocalAsync(new("k1", "v1", 1, false)).ConfigureAwait(true);
            File.Copy(leftPath, snapshotPath);
            await left.AppendLocalAsync(new("k2", "old", 2, false)).ConfigureAwait(true);
            await engine.SynchronizeAsync(left, right).ConfigureAwait(true);

            // A restore from backup (or directory-entry loss) reverts the journal past a shipped change.
            File.Copy(snapshotPath, leftPath, overwrite: true);
            left = await OpenKeyValue(leftPath, "dev-a").ConfigureAwait(true);
            var reissued = await left.AppendLocalAsync(new("k3", "new", 3, false)).ConfigureAwait(true);
            reissued.ChangeId.Should().Be("dev-a:2");

            var sync = () => engine.SynchronizeAsync(left, right);

            await sync.Should().ThrowAsync<InvalidOperationException>(
                "two different changes now share 'dev-a:2'; silently keeping both versions diverges the replicas forever")
                .ConfigureAwait(true);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_KnownIdentifierWithDifferentContent_IsRejectedWithoutMutation()
    {
        var directory = Directory.CreateTempSubdirectory("sync-reissue-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await OpenKeyValue(path, "target").ConfigureAwait(true);
            await replica.ApplyAsync(new("origin", 0, 1,
                [new("origin:1", "origin", 1, new KeyValueSyncChange<string, string>("k", "first", 1, false))])).ConfigureAwait(true);
            var committed = await File.ReadAllTextAsync(path).ConfigureAwait(true);

            var apply = () => replica.ApplyAsync(new("relay", 0, 1,
                [new("origin:1", "origin", 1, new KeyValueSyncChange<string, string>("k", "second", 1, false))]));

            await apply.Should().ThrowAsync<InvalidOperationException>().WithMessage("*origin:1*").ConfigureAwait(true);
            (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Be(committed);
            (await replica.ApplyAsync(new("relay", 0, 1,
                [new("origin:1", "origin", 1, new KeyValueSyncChange<string, string>("k", "first", 1, false))])).ConfigureAwait(true))
                .Should().Be(0, "an identical redelivery stays an idempotent no-op");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DictionaryKeyPolicy_DoesNotRenameCounterComponentsAcrossReopen()
    {
        var directory = Directory.CreateTempSubdirectory("sync-keypolicy-counter-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<GCounter>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"), s_camelCaseKeys)
                .ConfigureAwait(true);
            var acknowledged = new GCounter();
            acknowledged.Increment("Phone");
            acknowledged.Increment("Phone");
            await replica.AppendLocalAsync(acknowledged).ConfigureAwait(true);

            var reloaded = (await (await JsonFileSyncReplica<GCounter>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"), s_camelCaseKeys)
                .ConfigureAwait(true)).ReadAllAsync().ConfigureAwait(true)).Single().Value;

            ((GCounter)reloaded.Merge(acknowledged)).Value.Should().Be(2,
                "the reload is the acknowledged state itself, so merging the two must be idempotent (node 'Phone' kept, not renamed 'phone')");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DictionaryKeyPolicy_DoesNotRenamePeerCheckpointsAcrossReopen()
    {
        var directory = Directory.CreateTempSubdirectory("sync-keypolicy-checkpoint-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<string>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"), s_camelCaseKeys)
                .ConfigureAwait(true);
            await replica.ApplyAsync(new("Laptop", 0, 1, [new("Laptop:1", "Laptop", 1, "remote")])).ConfigureAwait(true);

            var reopened = await JsonFileSyncReplica<string>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"), s_camelCaseKeys)
                .ConfigureAwait(true);

            (await reopened.LoadCheckpointAsync("Laptop").ConfigureAwait(true)).Position.Should().Be(1,
                "the durable checkpoint for peer 'Laptop' must survive reopen under its own name");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JournalOfAnotherCrdtType_IsRefusedInsteadOfReinterpretedAndRewritten()
    {
        var directory = Directory.CreateTempSubdirectory("sync-crosstype-crdt-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var set = new ORSet<string>();
            set.Add("dev", "apple");
            set.Add("dev", "pear");
            set.Remove("pear");
            await (await JsonFileSyncReplica<ORSet<string>>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true))
                .AppendLocalAsync(set).ConfigureAwait(true);
            var committed = await File.ReadAllTextAsync(path).ConfigureAwait(true);
            var openAndAppend = async () =>
            {
                var counterView = await JsonFileSyncReplica<GCounter>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"))
                    .ConfigureAwait(true);
                await counterView.AppendLocalAsync(new GCounter()).ConfigureAwait(true);
            };

            await openAndAppend.Should().ThrowAsync<InvalidDataException>(
                "a counter cannot represent the set's elements and tombstones, so loading it as one must fail before any rewrite")
                .ConfigureAwait(true);
            (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Be(committed);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JournalOfAnotherRecordType_IsRefusedInsteadOfReinterpretedAndRewritten()
    {
        var directory = Directory.CreateTempSubdirectory("sync-crosstype-record-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            await (await OpenKeyValue(path, "dev").ConfigureAwait(true))
                .AppendLocalAsync(new("title", "hello", 1, false)).ConfigureAwait(true);
            var committed = await File.ReadAllTextAsync(path).ConfigureAwait(true);

            var openAndAppend = async () =>
            {
                var noteView = await JsonFileSyncReplica<PlainNote>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"))
                    .ConfigureAwait(true);
                await noteView.AppendLocalAsync(new PlainNote("later")).ConfigureAwait(true);
            };

            await openAndAppend.Should().ThrowAsync<InvalidDataException>(
                "a note has no key or value, so reading the key-value journal as notes would drop acknowledged content")
                .ConfigureAwait(true);
            (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Be(committed);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // ImmutableHashSet enumerates in string-hash order, which is randomized per process, so the journal
    // another process wrote lists the same elements in another order; this rewrites the file that way.
    [Fact]
    public async Task HashOrderedSetPayload_WrittenInAnotherElementOrder_ReopensWithTheSameSet()
    {
        var directory = Directory.CreateTempSubdirectory("sync-hash-order-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var tags = System.Collections.Immutable.ImmutableHashSet.CreateRange(Enumerable.Range(0, 12).Select(i => $"tag-{i}"));
            await (await JsonFileSyncReplica<System.Collections.Immutable.ImmutableHashSet<string>>.CreateAsync(
                path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true)).AppendLocalAsync(tags).ConfigureAwait(true);
            var journal = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(true))!;
            var written = journal["entries"]![0]!["value"]!.AsArray();
            var otherProcessOrder = written.Select(node => node!.GetValue<string>()).Reverse().ToArray();
            journal["entries"]![0]!["value"] = new System.Text.Json.Nodes.JsonArray(
                otherProcessOrder.Select(tag => (System.Text.Json.Nodes.JsonNode?)tag).ToArray());
            await File.WriteAllTextAsync(path, journal.ToJsonString()).ConfigureAwait(true);

            var reopen = async () => (await (await JsonFileSyncReplica<System.Collections.Immutable.ImmutableHashSet<string>>.CreateAsync(
                path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true)).ReadAllAsync().ConfigureAwait(true)).Single().Value;

            var reloaded = (await reopen.Should().NotThrowAsync(
                "the journal holds the acknowledged set; only the serializer's per-process element order differs").ConfigureAwait(true)).Subject;
            reloaded.SetEquals(tags).Should().BeTrue();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // A local-kind DateTime persists with the writer's zone offset and re-serializes with the reader's,
    // so a device that changed zone sees the same instant spelled with another offset.
    [Fact]
    public async Task LocalTimePayload_WrittenInAnotherTimeZone_ReopensWithTheSameInstant()
    {
        var directory = Directory.CreateTempSubdirectory("sync-local-time-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var acknowledged = new DateTime(2026, 1, 15, 15, 30, 0, DateTimeKind.Local);
            await (await JsonFileSyncReplica<KeyValueSyncChange<string, DateTime>>.CreateAsync(
                path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true))
                .AppendLocalAsync(new("meeting", acknowledged, 1, false)).ConfigureAwait(true);
            var journal = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(true))!;
            var instant = new DateTimeOffset(acknowledged);
            var otherZone = instant.ToOffset(instant.Offset >= TimeSpan.FromHours(10)
                ? instant.Offset - TimeSpan.FromHours(3)
                : instant.Offset + TimeSpan.FromHours(3));
            journal["entries"]![0]!["value"]!["value"] = otherZone.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
            await File.WriteAllTextAsync(path, journal.ToJsonString()).ConfigureAwait(true);

            var reopen = async () => (await (await JsonFileSyncReplica<KeyValueSyncChange<string, DateTime>>.CreateAsync(
                path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true)).ReadAllAsync().ConfigureAwait(true)).Single().Value;

            var reloaded = (await reopen.Should().NotThrowAsync(
                "the journal was written for this change type and options; only the zone that rendered the instant differs").ConfigureAwait(true)).Subject;
            reloaded.Value.ToUniversalTime().Should().Be(acknowledged.ToUniversalTime());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ArrayJournal_ReopenedWithAnElementTypeThatLosesPrecision_IsStillRefused()
    {
        var directory = Directory.CreateTempSubdirectory("sync-lossy-array-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            await (await JsonFileSyncReplica<KeyValueSyncChange<string, long[]>>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"))
                .ConfigureAwait(true)).AppendLocalAsync(new("ids", [1, 9007199254740993, 2], 1, false)).ConfigureAwait(true);
            var committed = await File.ReadAllTextAsync(path).ConfigureAwait(true);

            var openAndAppend = async () =>
            {
                var doubleView = await JsonFileSyncReplica<KeyValueSyncChange<string, double[]>>.CreateAsync(
                    path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
                await doubleView.AppendLocalAsync(new("later", [0d], 2, false)).ConfigureAwait(true);
            };

            await openAndAppend.Should().ThrowAsync<InvalidDataException>(
                "9007199254740993 reloads as 9007199254740992; an element-order-insensitive comparison must not excuse a changed value")
                .ConfigureAwait(true);
            (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Be(committed);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // Only the zone's rendering of an unchanged instant is excused. Options whose converter reads a
    // different instant back (here, one that drops seconds) must still be refused even when the
    // persisted spelling also carries another zone's offset.
    [Fact]
    public async Task LocalTimePayload_WhoseInstantTheReloadWouldMove_IsStillRefused()
    {
        var directory = Directory.CreateTempSubdirectory("sync-moved-time-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var acknowledged = new DateTime(2026, 1, 15, 15, 30, 45, DateTimeKind.Local);
            await (await JsonFileSyncReplica<KeyValueSyncChange<string, DateTime>>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"))
                .ConfigureAwait(true)).AppendLocalAsync(new("meeting", acknowledged, 1, false)).ConfigureAwait(true);
            var instant = new DateTimeOffset(acknowledged);
            var otherZone = instant.ToOffset(instant.Offset >= TimeSpan.FromHours(10)
                ? instant.Offset - TimeSpan.FromHours(3)
                : instant.Offset + TimeSpan.FromHours(3));
            var journal = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(true))!;
            journal["entries"]![0]!["value"]!["value"] = otherZone.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
            await File.WriteAllTextAsync(path, journal.ToJsonString()).ConfigureAwait(true);
            var minuteOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new MinutePrecisionLocalTimeConverter() } };

            var reopen = async () => await (await JsonFileSyncReplica<KeyValueSyncChange<string, DateTime>>.CreateAsync(
                path, new FixedDeviceReplicaIdProvider("dev"), minuteOptions).ConfigureAwait(true)).ReadAllAsync().ConfigureAwait(true);

            await reopen.Should().ThrowAsync<InvalidDataException>(
                "these options reload 15:30:45 as 15:30:00, so a rewrite would move the acknowledged instant").ConfigureAwait(true);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static Task<JsonFileSyncReplica<KeyValueSyncChange<string, string>>> OpenKeyValue(string path, string replicaId) =>
        JsonFileSyncReplica<KeyValueSyncChange<string, string>>.CreateAsync(path, new FixedDeviceReplicaIdProvider(replicaId));

    public sealed record CounterChange(string Name, IGCounter Counter);

    public sealed record NoteChange(string Text, UnreadablePayload? Extra);

    public sealed record PlainNote(string Text);

    private sealed class MinutePrecisionLocalTimeConverter : System.Text.Json.Serialization.JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetDateTime();
            return value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMinute));
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    // Serializable (Code is a public property) but not deserializable: the constructor parameter
    // has no matching property, the same shape as an in-assembly LamportTimestamp payload.
    public sealed class UnreadablePayload
    {
        public UnreadablePayload(int seed) => Code = seed.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public string Code { get; }
    }
}
