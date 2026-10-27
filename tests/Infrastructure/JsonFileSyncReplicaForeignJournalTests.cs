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

/// <summary>
/// Pins how <see cref="JsonFileSyncReplica{TChange}"/> treats a journal written for another change type: a
/// reader that would reorder a foreign ordered list refuses it instead of rewriting it in its own order, while
/// a set's per-process element order is still excused for the type that wrote it.
/// </summary>
public sealed class JsonFileSyncReplicaForeignJournalTests
{
    [Fact]
    public async Task OrderedListJournal_ReopenedAsSortedSet_IsRefusedAndLeftUnchanged()
    {
        var directory = Directory.CreateTempSubdirectory("sync-foreign-order-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            await (await JsonFileSyncReplica<List<int>>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"))
                .ConfigureAwait(true)).AppendLocalAsync([3, 1, 2]).ConfigureAwait(true);
            var committed = await File.ReadAllTextAsync(path).ConfigureAwait(true);

            var reopenAsSet = () => JsonFileSyncReplica<SortedSet<int>>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"));

            await reopenAsSet.Should().ThrowAsync<InvalidDataException>(
                "a sorted set reorders the list, so rewriting the journal would erase the acknowledged order").ConfigureAwait(true);
            (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Be(committed);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OrderedListMember_ReopenedAsSortedSetMember_IsRefused()
    {
        var directory = Directory.CreateTempSubdirectory("sync-foreign-member-order-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            await (await JsonFileSyncReplica<KeyValueSyncChange<string, List<string>>>.CreateAsync(
                path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true))
                .AppendLocalAsync(new("steps", ["mix", "bake", "cool"], 1, false)).ConfigureAwait(true);

            var reopenAsSet = () => JsonFileSyncReplica<KeyValueSyncChange<string, System.Collections.Immutable.ImmutableSortedSet<string>>>
                .CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"));

            await reopenAsSet.Should().ThrowAsync<InvalidDataException>(
                "the list's order is content; a sorted-set reader cannot represent it").ConfigureAwait(true);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // A grow-only set persists its elements from a hash set, whose string order is randomized per
    // process; the journal another process wrote lists them in another order and must still reopen.
    [Fact]
    public async Task GrowOnlySetJournal_WrittenInAnotherElementOrder_Reopens()
    {
        var directory = Directory.CreateTempSubdirectory("sync-gset-order-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            var set = new GSet<string>();
            foreach (var item in Enumerable.Range(0, 12).Select(i => $"item-{i}"))
            {
                set.Add(item);
            }

            await (await JsonFileSyncReplica<GSet<string>>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"))
                .ConfigureAwait(true)).AppendLocalAsync(set).ConfigureAwait(true);
            var journal = JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(true))!;
            var elements = journal["entries"]![0]!["value"]!["elements"]!.AsArray();
            var reversed = elements.Select(node => node!.GetValue<string>()).Reverse().ToArray();
            journal["entries"]![0]!["value"]!["elements"] = new JsonArray(reversed.Select(item => (JsonNode?)item).ToArray());
            await File.WriteAllTextAsync(path, journal.ToJsonString()).ConfigureAwait(true);

            var reopen = async () => (await (await JsonFileSyncReplica<GSet<string>>.CreateAsync(
                path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true)).ReadAllAsync().ConfigureAwait(true)).Single().Value;

            var reloaded = (await reopen.Should().NotThrowAsync(
                "a grow-only set's element order is a per-process rendering, not content").ConfigureAwait(true)).Subject;
            reloaded.Value.Should().BeEquivalentTo(set.Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
