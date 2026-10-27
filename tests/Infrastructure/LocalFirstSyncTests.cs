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

public sealed class LocalFirstSyncTests
{
    [Fact]
    public async Task OfflineReplicas_RestartThenExchangeDeltasAndConverge()
    {
        var directory = Directory.CreateTempSubdirectory("sync-restart-");
        try
        {
            var leftPath = Path.Combine(directory.FullName, "left.json");
            var rightPath = Path.Combine(directory.FullName, "right.json");
            var left = await CreateReplica(leftPath, "device-a");
            var right = await CreateReplica(rightPath, "device-b");
            await left.AppendLocalAsync(new KeyValueSyncChange<string, string>("shared", "left", 10, false));
            await left.AppendLocalAsync(new KeyValueSyncChange<string, string>("left-only", "A", 11, false));
            await right.AppendLocalAsync(new KeyValueSyncChange<string, string>("shared", "right", 10, false));
            await right.AppendLocalAsync(new KeyValueSyncChange<string, string>("right-only", "B", 12, false));

            // New objects model both applications stopping while offline and restarting later.
            left = await CreateReplica(leftPath, "device-a");
            right = await CreateReplica(rightPath, "device-b");
            var engine = CreateEngine();
            var session = await engine.SynchronizeAsync(left, right);

            session.LeftSent.Should().Be(2);
            session.RightSent.Should().Be(2);
            var leftState = await engine.MaterializeAsync(left);
            var rightState = await engine.MaterializeAsync(right);
            leftState.Should().BeEquivalentTo(rightState);
            leftState.Should().Contain(new KeyValuePair<string, string>("shared", "right"));
            leftState.Should().ContainKeys("left-only", "right-only");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RestartedCheckpoints_PreventResendingAcknowledgedOrOriginatingChanges()
    {
        var directory = Directory.CreateTempSubdirectory("sync-checkpoint-");
        try
        {
            var leftPath = Path.Combine(directory.FullName, "left.json");
            var rightPath = Path.Combine(directory.FullName, "right.json");
            var left = await CreateReplica(leftPath, "device-a");
            var right = await CreateReplica(rightPath, "device-b");
            await left.AppendLocalAsync(new KeyValueSyncChange<string, string>("one", "1", 1, false));
            var engine = CreateEngine();
            await engine.SynchronizeAsync(left, right);

            left = await CreateReplica(leftPath, "device-a");
            right = await CreateReplica(rightPath, "device-b");
            var second = await engine.SynchronizeAsync(left, right);

            second.LeftSent.Should().Be(0);
            second.RightSent.Should().Be(0);
            second.AppliedToLeft.Should().Be(0);
            second.AppliedToRight.Should().Be(0);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateAndSeededReorderedDeltas_AreIdempotentAndDeterministic()
    {
        var directory = Directory.CreateTempSubdirectory("sync-reorder-");
        try
        {
            var replica = await CreateReplica(Path.Combine(directory.FullName, "target.json"), "target");
            var random = new Random(20_260_917);
            var changes = Enumerable.Range(1, 100)
                .Select(i => new SyncChange<KeyValueSyncChange<string, string>>(
                    $"origin:{i}", "origin", i,
                    new KeyValueSyncChange<string, string>($"key-{i % 7}", $"value-{i}", i, false)))
                .OrderBy(_ => random.Next())
                .ToArray();
            var delta = new ChangeSet<KeyValueSyncChange<string, string>>("origin", 0, 100, changes);

            (await replica.ApplyAsync(delta)).Should().Be(100);
            (await replica.ApplyAsync(delta)).Should().Be(0);
            (await replica.ReadAllAsync()).Should().HaveCount(100);

            var state = await CreateEngine().MaterializeAsync(replica);
            state.Should().HaveCount(7);
            state["key-2"].Should().Be("value-100");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DeltaClaimingMoreChangesThanItCarries_IsRejectedAndLaterSyncDeliversTheRest()
    {
        var directory = Directory.CreateTempSubdirectory("sync-short-delta-");
        try
        {
            var source = await CreateReplica(Path.Combine(directory.FullName, "source.json"), "source");
            var target = await CreateReplica(Path.Combine(directory.FullName, "target.json"), "target");
            for (var i = 1; i <= 4; i++)
            {
                await source.AppendLocalAsync(new KeyValueSyncChange<string, string>($"k{i}", $"v{i}", i, false));
            }

            var full = await source.ReadChangesAsync(new SyncCheckpoint("source", 0));
            var truncated = full with { Changes = [full.Changes[0]] };

            var act = () => target.ApplyAsync(truncated);

            await act.Should().ThrowAsync<ArgumentException>(
                "a delta spanning positions 0..4 must carry four changes; recording position 4 after one would skip three forever");
            (await target.LoadCheckpointAsync("source")).Position.Should().Be(0);
            await CreateEngine().SynchronizeAsync(target, source);
            (await target.ReadAllAsync()).Should().HaveCount(4, "a later session must still deliver every source change");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DeltaRepeatingOneChangeInPlaceOfAnother_IsRejectedAndLaterSyncDeliversBoth()
    {
        var directory = Directory.CreateTempSubdirectory("sync-repeat-delta-");
        try
        {
            var source = await CreateReplica(Path.Combine(directory.FullName, "source.json"), "src");
            var target = await CreateReplica(Path.Combine(directory.FullName, "target.json"), "target");
            await source.AppendLocalAsync(new KeyValueSyncChange<string, string>("k1", "v1", 1, false));
            await source.AppendLocalAsync(new KeyValueSyncChange<string, string>("k2", "v2", 2, false));
            var full = await source.ReadChangesAsync(new SyncCheckpoint("src", 0));
            var repeated = full with { Changes = [full.Changes[0], full.Changes[0]] };

            var act = () => target.ApplyAsync(repeated);

            await act.Should().ThrowAsync<ArgumentException>(
                "positions 0..2 hold two different changes; a delta that repeats src:1 never delivers src:2");
            (await target.LoadCheckpointAsync("src")).Position.Should().Be(0, "a rejected delta must not advance the checkpoint");
            await CreateEngine().SynchronizeAsync(target, source);
            (await target.ReadAllAsync()).Select(change => change.ChangeId).Should().Equal("src:1", "src:2");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CorrectlySizedDeltaOfOneRepeatedChange_IsRejectedBeforeAnyMutation()
    {
        var directory = Directory.CreateTempSubdirectory("sync-repeat-all-delta-");
        try
        {
            var source = await CreateReplica(Path.Combine(directory.FullName, "source.json"), "src");
            var target = await CreateReplica(Path.Combine(directory.FullName, "target.json"), "target");
            for (var i = 1; i <= 4; i++)
            {
                await source.AppendLocalAsync(new KeyValueSyncChange<string, string>($"k{i}", $"v{i}", i, false));
            }

            var full = await source.ReadChangesAsync(new SyncCheckpoint("src", 0));
            var repeated = full with { Changes = [full.Changes[0], full.Changes[0], full.Changes[0], full.Changes[0]] };

            var act = () => target.ApplyAsync(repeated);

            await act.Should().ThrowAsync<ArgumentException>(
                "a delta carries exactly one change per position, so four copies of src:1 cannot stand for positions 0..4");
            (await target.ReadAllAsync()).Should().BeEmpty("the rejection must precede any mutation");
            (await target.LoadCheckpointAsync("src")).Position.Should().Be(0);
            await CreateEngine().SynchronizeAsync(target, source);
            (await target.ReadAllAsync()).Should().HaveCount(4, "a later session must still deliver every source change");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FutureDeltaGap_IsRejectedWithoutAdvancingCheckpointOrAppendingChanges()
    {
        var directory = Directory.CreateTempSubdirectory("sync-gap-");
        try
        {
            var replica = await CreateReplica(Path.Combine(directory.FullName, "target.json"), "target");
            var future = new ChangeSet<KeyValueSyncChange<string, string>>(
                "origin",
                5,
                6,
                [new SyncChange<KeyValueSyncChange<string, string>>(
                    "origin:6", "origin", 6, new("missing-prefix", "unsafe", 6, false))]);

            var act = () => replica.ApplyAsync(future);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*gap*");
            (await replica.LoadCheckpointAsync("origin")).Position.Should().Be(0);
            (await replica.ReadAllAsync()).Should().BeEmpty();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileDeviceIdentity_SurvivesProviderRestart()
    {
        var directory = Directory.CreateTempSubdirectory("sync-identity-");
        try
        {
            var path = Path.Combine(directory.FullName, "device-id");
            var first = await new FileDeviceReplicaIdProvider(path).GetReplicaIdAsync();
            var second = await new FileDeviceReplicaIdProvider(path).GetReplicaIdAsync();

            second.Should().Be(first);
            first.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReopenAfterUncertainCompletion_ContinuesTheCommittedLocalSequence()
    {
        var directory = Directory.CreateTempSubdirectory("sync-sequence-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await CreateReplica(path, "device-a");
            var first = await replica.AppendLocalAsync(
                new KeyValueSyncChange<string, string>("one", "1", 1, false));

            replica = await CreateReplica(path, "device-a");
            var second = await replica.AppendLocalAsync(
                new KeyValueSyncChange<string, string>("two", "2", 2, false));

            first.ChangeId.Should().Be("device-a:1");
            second.ChangeId.Should().Be("device-a:2");
            (await replica.ReadAllAsync()).Select(change => change.ChangeId)
                .Should().Equal("device-a:1", "device-a:2");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void CrdtAdapters_MergeExistingCounterAndObservedRemoveSetStates()
    {
        var leftCounter = new GCounter();
        leftCounter.Increment("a");
        var rightCounter = new GCounter();
        rightCounter.Increment("b");
        new GCounterSyncAdapter().Resolve(
        [
            new SyncChange<GCounter>("a:1", "a", 1, leftCounter),
            new SyncChange<GCounter>("b:1", "b", 1, rightCounter),
        ]).Should().Be(2);

        var observed = new ORSet<string>();
        observed.Add("a", "kept");
        var removed = (ORSet<string>)observed.Merge(new ORSet<string>());
        removed.Remove("kept");
        var concurrent = new ORSet<string>();
        concurrent.Add("b", "concurrent");
        var result = new ORSetSyncAdapter<string>().Resolve(
        [
            new SyncChange<ORSet<string>>("a:1", "a", 1, removed),
            new SyncChange<ORSet<string>>("b:1", "b", 1, concurrent),
        ]);

        result.Should().Equal("concurrent");
    }

    private static Task<JsonFileSyncReplica<KeyValueSyncChange<string, string>>> CreateReplica(
        string path,
        string replicaId) =>
        JsonFileSyncReplica<KeyValueSyncChange<string, string>>.CreateAsync(
            path,
            new FixedDeviceReplicaIdProvider(replicaId));

    private static SyncEngine<KeyValueSyncChange<string, string>, IReadOnlyDictionary<string, string>> CreateEngine() =>
        new(new LastWriterWinsConflictResolver<string, string>());
    [Fact]
    public async Task GSetPayload_SurvivesJournalReloadAndLaterAppends()
    {
        var directory = Directory.CreateTempSubdirectory("sync-gset-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<GSet<string>>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"));
            var first = new GSet<string>();
            first.Add("alpha");
            first.Add("beta");
            await replica.AppendLocalAsync(first);

            (await replica.ReadAllAsync()).Single().Value.Value.Should().BeEquivalentTo(["alpha", "beta"],
                "the journal must round-trip the grow-only set it acknowledged");

            var second = new GSet<string>();
            second.Add("gamma");
            await replica.AppendLocalAsync(second);
            var reopened = await JsonFileSyncReplica<GSet<string>>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"));
            var values = (await reopened.ReadAllAsync()).Select(change => change.Value.Value.Order(StringComparer.Ordinal).ToArray()).ToArray();
            values.Should().HaveCount(2);
            values[0].Should().Equal("alpha", "beta");
            values[1].Should().Equal("gamma");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PNCounterPayload_IsRejectedInsteadOfPersistedAsAnEmptyCounter()
    {
        var directory = Directory.CreateTempSubdirectory("sync-pncounter-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var replica = await JsonFileSyncReplica<PNCounter>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"));
            var counter = new PNCounter();
            counter.Increment("a");
            counter.Increment("a");

            var append = () => replica.AppendLocalAsync(counter);

            await append.Should().ThrowAsync<NotSupportedException>(
                "the journal cannot persist the positive/negative components, so it must refuse rather than store Value only");
            File.Exists(path).Should().BeFalse("a refused payload must not reach the journal");
            (await replica.ReadAllAsync()).Should().BeEmpty();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileDeviceIdentity_ConcurrentFirstRunProviders_ReturnThePersistedIdentity()
    {
        var directory = Directory.CreateTempSubdirectory("sync-identity-race-");
        try
        {
            var divergent = 0;
            for (var trial = 0; trial < 50; trial++)
            {
                var path = Path.Combine(directory.FullName, $"device-id-{trial}");
                var left = new FileDeviceReplicaIdProvider(path);
                var right = new FileDeviceReplicaIdProvider(path);
                using var start = new Barrier(2);
                try
                {
                    var ids = await Task.WhenAll(
                        Task.Run(() => { start.SignalAndWait(); return left.GetReplicaIdAsync(); }),
                        Task.Run(() => { start.SignalAndWait(); return right.GetReplicaIdAsync(); }));
                    var persisted = (await File.ReadAllTextAsync(path)).Trim();
                    if (ids[0] != ids[1] || ids[0] != persisted)
                    {
                        divergent++;
                    }
                }
                catch (InvalidDataException)
                {
                    divergent++; // A caller observed the identity file before its content was complete.
                }
            }

            divergent.Should().Be(0, "every first-run caller must receive the one identity that is persisted on disk");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileDeviceIdentity_CancelledFirstRun_DoesNotBrickLaterCalls()
    {
        var directory = Directory.CreateTempSubdirectory("sync-identity-cancel-");
        try
        {
            var bricked = 0;
            for (var trial = 0; trial < 40; trial++)
            {
                var trialDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, $"t{trial}")).FullName;
                var path = Path.Combine(trialDirectory, "device-id");
                using var cancellation = new CancellationTokenSource();
                using var stop = new CancellationTokenSource();

                // Cancel the caller's token the moment the provider materializes any file, which is
                // between its create and its write.
                var spinner = Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        if (Directory.EnumerateFileSystemEntries(trialDirectory).Any())
                        {
                            cancellation.Cancel();
                            return;
                        }
                    }
                });
                string? first = null;
                try
                {
                    first = await new FileDeviceReplicaIdProvider(path).GetReplicaIdAsync(cancellation.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // An ordinary caller cancellation; the next caller must still obtain an identity.
                }

                await stop.CancelAsync().ConfigureAwait(true);
                await spinner.ConfigureAwait(true);
                try
                {
                    var later = await new FileDeviceReplicaIdProvider(path).GetReplicaIdAsync().ConfigureAwait(true);
                    if (string.IsNullOrWhiteSpace(later) || (first is not null && later != first))
                    {
                        bricked++;
                    }
                }
                catch (InvalidDataException)
                {
                    bricked++;
                }
            }

            bricked.Should().Be(0, "a cancelled first run must leave no identity file, or a complete one");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SymlinkAliasedJournalPaths_DoNotLoseAcknowledgedAppends()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating directory symlinks needs elevated rights on Windows.
        }

        var directory = Directory.CreateTempSubdirectory("sync-alias-");
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(directory.FullName, "real")).FullName;
            var alias = Path.Combine(directory.FullName, "alias");
            Directory.CreateSymbolicLink(alias, real);
            var left = await CreateReplica(Path.Combine(real, "journal.json"), "device-a");
            var right = await CreateReplica(Path.Combine(alias, "journal.json"), "device-a");

            await AppendConcurrently(left, right, perSide: 40);

            (await left.ReadAllAsync()).Should().HaveCount(80,
                "both instances address one physical journal, so every acknowledged append must survive");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CaseAliasedJournalPaths_OnCaseInsensitiveVolume_DoNotLoseAcknowledgedAppends()
    {
        var directory = Directory.CreateTempSubdirectory("sync-case-alias-");
        try
        {
            var lower = Path.Combine(directory.FullName, "journal.json");
            var upper = Path.Combine(directory.FullName, "JOURNAL.json");
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "probe"), string.Empty);
            if (!File.Exists(Path.Combine(directory.FullName, "PROBE")))
            {
                return; // Case-sensitive volume: the two spellings are distinct files.
            }

            var left = await CreateReplica(lower, "device-a");
            var right = await CreateReplica(upper, "device-a");

            await AppendConcurrently(left, right, perSide: 40);

            (await left.ReadAllAsync()).Should().HaveCount(80,
                "on a case-insensitive volume both spellings address one journal, so every acknowledged append must survive");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task AppendConcurrently(
        JsonFileSyncReplica<KeyValueSyncChange<string, string>> left,
        JsonFileSyncReplica<KeyValueSyncChange<string, string>> right,
        int perSide)
    {
        async Task Run(JsonFileSyncReplica<KeyValueSyncChange<string, string>> replica, string side)
        {
            for (var i = 0; i < perSide; i++)
            {
                await replica.AppendLocalAsync(new KeyValueSyncChange<string, string>($"{side}-{i}", "v", i, false));
            }
        }

        await Task.WhenAll(Task.Run(() => Run(left, "left")), Task.Run(() => Run(right, "right")));
    }
}
