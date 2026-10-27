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

public sealed class TransactionalMessageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "txn-message-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "messages.log");
    [Fact]
    public async Task OutboxAndInbox_CommitStateAndIdentityTogether_AndReplayDuplicates()
    {
        using var store = await TransactionalMessageStore.CreateAsync(FilePath).ConfigureAwait(true);
        var id = new RequestId("order");
        var mutation = new MessageMutation(new Dictionary<string, string?> { ["order"] = "paid" }, [new OutboxMessage(new RequestId("event-1"), "orders", "paid")], "accepted");
        (await store.CommitAsync(id, "pay-order", mutation).ConfigureAwait(true)).Should().Be("accepted");
        (await store.ReadAsync("order").ConfigureAwait(true)).Should().Be("paid");
        (await store.PendingMessagesAsync().ConfigureAwait(true)).Should().HaveCount(1);
        var calls = 0;
        MessageMutation Handle(IReadOnlyDictionary<string, string> state)
        {
            calls++;
            return new MessageMutation(new Dictionary<string, string?> { ["received"] = "1" }, [], "processed");
        }
        (await store.ProcessAsync(new RequestId("event-1"), "paid", Handle).ConfigureAwait(true)).Should().Be("processed");
        (await store.ProcessAsync(new RequestId("event-1"), "paid", Handle).ConfigureAwait(true)).Should().Be("processed");
        calls.Should().Be(1);
        await store.AcknowledgeAsync(new RequestId("event-1")).ConfigureAwait(true);
        (await store.PendingMessagesAsync().ConfigureAwait(true)).Should().BeEmpty();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostMutationAcknowledgement_ReplaysStateMessagesAndCachedResponseTogether(bool after)
    {
        var id = new RequestId("durable-order");
        var mutation = new MessageMutation(new Dictionary<string, string?> { ["balance"] = "90" }, [new OutboxMessage(new RequestId("emit"), "orders", "paid")], "ok");
        using (var failed = await TransactionalMessageStore.CreateAsync(FilePath).ConfigureAwait(true))
        {
            failed.FaultInjector = (stage, persisted) => { if (stage == "Mutation" && persisted == after) { throw new IOException("crash"); } };
            await Assert.ThrowsAsync<IOException>(() => failed.CommitAsync(id, "debit", mutation)).ConfigureAwait(true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => failed.ReadAsync("balance")).ConfigureAwait(true);
        }
        using var recovered = await TransactionalMessageStore.CreateAsync(FilePath).ConfigureAwait(true);
        (await recovered.ReadAsync("balance").ConfigureAwait(true)).Should().Be(after ? "90" : null);
        (await recovered.PendingMessagesAsync().ConfigureAwait(true)).Should().HaveCount(after ? 1 : 0);
        (await recovered.CommitAsync(id, "debit", mutation).ConfigureAwait(true)).Should().Be("ok");
        (await recovered.PendingMessagesAsync().ConfigureAwait(true)).Should().ContainSingle().Which.Id.Should().Be(new RequestId("emit"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovered.CommitAsync(id, "different", mutation)).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboxCrash_DeduplicatesProcessingAcrossReopen(bool after)
    {
        static MessageMutation Increment(IReadOnlyDictionary<string, string> state)
        {
            var count = int.Parse(state.GetValueOrDefault("count", "0"), System.Globalization.CultureInfo.InvariantCulture) + 1;
            return new MessageMutation(new Dictionary<string, string?> { ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) }, [], "done");
        }
        var id = new RequestId("consume");
        using (var store = await TransactionalMessageStore.CreateAsync(FilePath).ConfigureAwait(true))
        {
            store.FaultInjector = (stage, persisted) => { if (stage == "Mutation" && persisted == after) { throw new IOException("crash"); } };
            await Assert.ThrowsAsync<IOException>(() => store.ProcessAsync(id, "payload-v1", Increment)).ConfigureAwait(true);
        }
        using var recovered = await TransactionalMessageStore.CreateAsync(FilePath).ConfigureAwait(true);
        await recovered.ProcessAsync(id, "payload-v1", Increment).ConfigureAwait(true);
        await recovered.ProcessAsync(id, "payload-v1", Increment).ConfigureAwait(true);
        (await recovered.ReadAsync("count").ConfigureAwait(true)).Should().Be("1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovered.ProcessAsync(id, "changed", Increment)).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutboxAcknowledgementCrash_DoesNotUndoCommittedBusinessState(bool after)
    {
        var message = new OutboxMessage(new RequestId("message"), "topic", "data");
        using (var store = await TransactionalMessageStore.CreateAsync(FilePath).ConfigureAwait(true))
        {
            await store.CommitAsync(new RequestId("produce"), "intent", new MessageMutation(new Dictionary<string, string?> { ["x"] = "committed" }, [message], "ok")).ConfigureAwait(true);
            store.FaultInjector = (stage, persisted) => { if (stage == "Acknowledge" && persisted == after) { throw new IOException("ack lost"); } };
            await Assert.ThrowsAsync<IOException>(() => store.AcknowledgeAsync(message.Id)).ConfigureAwait(true);
        }
        using var recovered = await TransactionalMessageStore.CreateAsync(FilePath).ConfigureAwait(true);
        (await recovered.ReadAsync("x").ConfigureAwait(true)).Should().Be("committed");
        (await recovered.PendingMessagesAsync().ConfigureAwait(true)).Should().HaveCount(after ? 0 : 1);
        await recovered.AcknowledgeAsync(message.Id).ConfigureAwait(true);
    }

    [Fact]
    public async Task Idempotency_ConcurrentDuplicatesBuildAndCommitOneLocalEffect()
    {
        using var store = await TransactionalMessageStore.CreateAsync(FilePath).ConfigureAwait(true);
        var calls = 0;
        MessageMutation Handler(IReadOnlyDictionary<string, string> state)
        {
            calls++;
            return new MessageMutation(new Dictionary<string, string?> { ["effect"] = "once" }, [], "response");
        }
        var operations = Enumerable.Range(0, 10).Select(_ => store.ExecuteAsync(new RequestId("request"), "v1", Handler)).ToArray();
        (await Task.WhenAll(operations).ConfigureAwait(true)).Should().OnlyContain(value => value == "response");
        calls.Should().Be(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ProcessAsync(new RequestId("request"), "v1", Handler)).ConfigureAwait(true);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ExecuteAsync(new RequestId("canceled"), "v1", Handler, cancellation.Token)).ConfigureAwait(true);
        calls.Should().Be(1);
    }

    [Fact]
    public void DurableText_RejectsMalformedUtf16BeforeJsonCanChangeIntent()
    {
        var malformed = new string((char)0xD800, 1);
        System.Text.Json.JsonSerializer.Serialize(malformed).Should().Be(System.Text.Json.JsonSerializer.Serialize("\uFFFD"));
        new RequestId("valid-\uD83D\uDE00").Value.Should().Be("valid-\uD83D\uDE00");
        // JSON's replacement fallback must not silently alias distinct intent or alter business data.
        Action identity = () => _ = new RequestId(malformed);
        Action resource = () => _ = new TransactionIntent(malformed, new Dictionary<string, string?>());
        Action value = () => _ = new MessageMutation(new Dictionary<string, string?> { ["key"] = malformed }, [], "ok");
        Action payload = () => _ = new MessageMutation(new Dictionary<string, string?>(), [new OutboxMessage(new RequestId("message"), "topic", malformed)], "ok");
        identity.Should().Throw<ArgumentException>(); resource.Should().Throw<ArgumentException>();
        value.Should().Throw<ArgumentException>(); payload.Should().Throw<ArgumentException>();
    }

    public void Dispose() { if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); } }
}
