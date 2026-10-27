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
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins that a <see cref="JsonFileSyncReplica{TChange}"/> journal records the change type that wrote it and
/// is refused, unchanged, when reopened with another change type: the renderings excused as per-process
/// variance (hash-set element order, a local time's zone offset) are only safe to excuse for the type that
/// produced them, so a foreign journal must never reach them.
/// </summary>
public sealed class JsonFileSyncReplicaWriterIdentityTests
{
    [Fact]
    public async Task OffsetCarryingDateTimeOffsetJournal_ReopenedAsLocalDateTime_IsRefusedAndLeftUnchanged()
    {
        await AssertForeignReopenRefused<KeyValueSyncChange<string, DateTimeOffset>, KeyValueSyncChange<string, DateTime>>(
            new("meeting", new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.FromHours(9)), 1, false),
            new("later", new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Local), 2, false),
            "the +09:00 offset is the writer's content; a local-time reader would rewrite it with this zone's offset").ConfigureAwait(true);
    }

    [Fact]
    public async Task UnspecifiedTimeJournal_ReopenedAsDateTimeOffset_IsRefusedAndLeftUnchanged()
    {
        await AssertForeignReopenRefused<KeyValueSyncChange<string, DateTime>, KeyValueSyncChange<string, DateTimeOffset>>(
            new("meeting", new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Unspecified), 1, false),
            new("later", new DateTimeOffset(2026, 3, 2, 9, 30, 0, TimeSpan.Zero), 2, false),
            "an unspecified time has no offset; an offset reader would stamp this zone's offset onto it").ConfigureAwait(true);
    }

    // The case a reader-type guard could not close: the persisted value is a string and the reader declares
    // a local DateTime, exactly the shape a same-type journal written in another zone has.
    [Fact]
    public async Task OffsetStringJournal_ReopenedAsLocalDateTime_IsRefusedAndLeftUnchanged()
    {
        var local = new DateTimeOffset(new DateTime(2026, 1, 15, 15, 30, 0, DateTimeKind.Local));
        var otherZone = local.ToOffset(local.Offset >= TimeSpan.FromHours(10)
            ? local.Offset - TimeSpan.FromHours(3)
            : local.Offset + TimeSpan.FromHours(3));
        await AssertForeignReopenRefused<KeyValueSyncChange<string, string>, KeyValueSyncChange<string, DateTime>>(
            new("meeting", otherZone.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), 1, false),
            new("later", new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Local), 2, false),
            "the string's offset is content; a local-time reader would rewrite it with this process's offset").ConfigureAwait(true);
    }

    [Fact]
    public async Task DateOnlyStringJournal_ReopenedAsDateTime_IsRefusedAndLeftUnchanged()
    {
        await AssertForeignReopenRefused<KeyValueSyncChange<string, string>, KeyValueSyncChange<string, DateTime>>(
            new("due", "2026-01-01", 1, false),
            new("later", new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Local), 2, false),
            "a date-only string would be rewritten as a date-time string the string writer never acknowledged").ConfigureAwait(true);
    }

    [Fact]
    public async Task OrderedListMemberJournal_ReopenedAsHashOrderedSetMember_IsRefusedAndLeftUnchanged()
    {
        await AssertForeignReopenRefused<KeyValueSyncChange<string, List<string>>, KeyValueSyncChange<string, ImmutableHashSet<string>>>(
            new("steps", Enumerable.Range(0, 12).Select(i => $"item-{(char)('a' + i)}").ToList(), 1, false),
            new("later", ["x"], 2, false),
            "the list's order is content; a hash-ordered set reader would rewrite it in this process's hash order").ConfigureAwait(true);
    }

    [Fact]
    public async Task OrderedListJournal_ReopenedAsHashOrderedSet_IsRefusedAndLeftUnchanged()
    {
        await AssertForeignReopenRefused<List<string>, ImmutableHashSet<string>>(
            Enumerable.Range(0, 12).Select(i => $"item-{i}").ToList(),
            ["x"],
            "the list's order is content; a hash-ordered set reader would rewrite it in this process's hash order").ConfigureAwait(true);
    }

    [Fact]
    public async Task DecimalJournal_ReopenedAsDouble_IsRefusedAndLeftUnchanged()
    {
        await AssertForeignReopenRefused<KeyValueSyncChange<string, decimal>, KeyValueSyncChange<string, double>>(
            new("price", 1.10m, 1, false),
            new("later", 2d, 2, false),
            "1.10 carries a scale the decimal writer reloads; a double reader would rewrite it as 1.1").ConfigureAwait(true);
    }

    [Fact]
    public async Task NullMemberJournal_ReopenedByATypeWithoutThatMember_IsRefusedAndLeftUnchanged()
    {
        await AssertForeignReopenRefused<TaggedDocument, UntaggedDocument>(
            new TaggedDocument { Name = "n1", Tag = null },
            new UntaggedDocument { Name = "n2" },
            "the writer's null replaces its non-null default; dropping the member would reload \"untagged\"").ConfigureAwait(true);
    }

    [Fact]
    public async Task NullMemberCounterJournal_ReopenedByATypeWithoutThatMember_IsRefusedAndLeftUnchanged()
    {
        await AssertForeignReopenRefused<NotedCounter, PlainCounter>(
            new NotedCounter { X = 1, Note = null },
            new PlainCounter { X = 2 },
            "dropping the persisted null would make the writer reload its acknowledged change as Note = \"default\"").ConfigureAwait(true);
    }

    // The writer's identity is checked before the content is deserialized, so a foreign journal the reader
    // cannot even parse as its own type is refused the same way as one it could reinterpret.
    [Fact]
    public async Task ForeignJournalTheReaderCannotDeserialize_IsRefusedWithTheTypedException()
    {
        await AssertForeignReopenRefused<KeyValueSyncChange<string, string>, KeyValueSyncChange<string, long>>(
            new("count", "not a number", 1, false),
            new("later", 2L, 2, false),
            "a journal written for another change type is refused as such, not surfaced as a parse failure").ConfigureAwait(true);
    }

    // No journal is read without knowing which change type wrote it: one lacking the record cannot be told
    // apart from a foreign journal, so it is refused rather than given the excusals of the reader's type.
    [Fact]
    public async Task JournalWithoutAWriterIdentity_IsRefusedAndLeftUnchanged()
    {
        var directory = Directory.CreateTempSubdirectory("sync-writer-identity-missing-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            await (await JsonFileSyncReplica<KeyValueSyncChange<string, string>>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"))
                .ConfigureAwait(true)).AppendLocalAsync(new("k", "v", 1, false)).ConfigureAwait(true);
            var journal = JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(true))!.AsObject();
            journal.Remove("changeType");
            await File.WriteAllTextAsync(path, journal.ToJsonString()).ConfigureAwait(true);
            var committed = await File.ReadAllTextAsync(path).ConfigureAwait(true);

            var reopen = () => JsonFileSyncReplica<KeyValueSyncChange<string, string>>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev"));

            await reopen.Should().ThrowAsync<InvalidDataException>(
                "a journal that does not name its writer's change type cannot be told apart from a foreign one").ConfigureAwait(true);
            (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Be(committed);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task AssertForeignReopenRefused<TWriter, TReader>(TWriter acknowledged, TReader later, string because)
        where TWriter : notnull
        where TReader : notnull
    {
        var directory = Directory.CreateTempSubdirectory("sync-writer-identity-");
        try
        {
            var path = Path.Combine(directory.FullName, "replica.json");
            await (await JsonFileSyncReplica<TWriter>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true))
                .AppendLocalAsync(acknowledged).ConfigureAwait(true);
            var committed = await File.ReadAllTextAsync(path).ConfigureAwait(true);

            var openAndAppend = async () =>
            {
                var foreign = await JsonFileSyncReplica<TReader>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true);
                await foreign.AppendLocalAsync(later).ConfigureAwait(true);
            };

            await openAndAppend.Should().ThrowAsync<InvalidDataException>(because).ConfigureAwait(true);
            (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Be(committed, "a refused journal must not be rewritten");
            (await (await JsonFileSyncReplica<TWriter>.CreateAsync(path, new FixedDeviceReplicaIdProvider("dev")).ConfigureAwait(true))
                .ReadAllAsync().ConfigureAwait(true)).Should().ContainSingle("the writer's own type still reopens its journal");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    public sealed class TaggedDocument
    {
        public string Name { get; set; } = string.Empty;

        public string? Tag { get; set; } = "untagged";
    }

    public sealed class UntaggedDocument
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class NotedCounter
    {
        public int X { get; set; }

        public string? Note { get; set; } = "default";
    }

    public sealed class PlainCounter
    {
        public int X { get; set; }
    }
}
