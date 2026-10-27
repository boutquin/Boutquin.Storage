// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using Boutquin.Storage.Domain.Interfaces.Streaming;
using Boutquin.Storage.Infrastructure.Cdc;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

namespace Boutquin.Storage.Infrastructure.Tests.Cdc;

public sealed class CdcFailureBoundaryTests
{
    [Fact]
    public async Task CompactedSaveCommittedThenFailedRequiresRestoreAndNeverReusesOffset()
    {
        var store = new CommitThenThrowCheckpoint();
        var topic = new CheckpointedLogCompactedTopic<string, string>(store, "topic");
        await topic.RestoreAsync();
        await Assert.ThrowsAsync<IOException>(() => topic.AppendAsync("a", "committed", ChangeOperation.Insert).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => topic.AppendAsync("b", "blocked", ChangeOperation.Insert).AsTask());
        await topic.RestoreAsync();
        Assert.Equal(1, await topic.AppendAsync("b", "next", ChangeOperation.Insert));
        Assert.Equal(2, (await topic.CaptureSnapshotAsync()).NextPosition);
    }

    [Fact]
    public async Task LsmApplyFailureLeavesCommittedJournalRecoverableAndBlocksNewWrites()
    {
        var engine = new Mock<ILsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();
        engine.Setup(e => e.GetAllItemsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<(SerializableWrapper<int>, SerializableWrapper<string>)>());
        engine.SetupSequence(e => e.SetAsync(It.IsAny<SerializableWrapper<int>>(), It.IsAny<SerializableWrapper<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("injected materialization failure"))
            .Returns(Task.CompletedTask)
            .Returns(Task.CompletedTask);
        var log = new InMemoryEventLog<ChangeRecord<SerializableWrapper<int>, SerializableWrapper<string>>>();
        var adapter = new LsmCdcAdapter<SerializableWrapper<int>, SerializableWrapper<string>>(engine.Object, log);
        await adapter.RecoverAsync();
        await Assert.ThrowsAsync<IOException>(() => adapter.SetAsync(new(1), new("committed")).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SetAsync(new(2), new("blocked")).AsTask());
        await adapter.RecoverAsync();
        var snapshot = await adapter.CaptureSnapshotAsync();
        Assert.Equal(1, snapshot.NextPosition);
        Assert.Equal(new SerializableWrapper<string>("committed"), Assert.Single(snapshot.Records).Value);
        Assert.Equal(1, await adapter.SetAsync(new(2), new("next")));
        engine.Verify(e => e.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("committed"), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RecoveryRejectsAnUnrelatedEngineWithoutOverwritingExistingRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cdc_foreign_" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(32, dir, "foreign");
            await engine.SetAsync(new(1), new("preserve"));
            var adapter = new LsmCdcAdapter<SerializableWrapper<int>, SerializableWrapper<string>>(engine, new InMemoryEventLog<ChangeRecord<SerializableWrapper<int>, SerializableWrapper<string>>>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.RecoverAsync().AsTask());
            Assert.Equal(new SerializableWrapper<string>("preserve"), (await engine.TryGetValueAsync(new(1))).Value);
        }
        finally { if (Directory.Exists(dir)) { Directory.Delete(dir, true); } }
    }

    [Fact]
    public async Task CancelledCompactedAppendDoesNotConsumeAnOffset()
    {
        var topic = new CheckpointedLogCompactedTopic<string, string>(new InMemoryCheckpointStore(), "topic");
        await topic.RestoreAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => topic.AppendAsync("a", "value", ChangeOperation.Insert, cancellation.Token).AsTask());
        Assert.Equal(0, await topic.AppendAsync("b", "value", ChangeOperation.Insert));
    }

    private sealed class CommitThenThrowCheckpoint : ICheckpointStore
    {
        private readonly InMemoryCheckpointStore _inner = new();
        private bool _fail = true;
        public async ValueTask SaveAsync(string processorId, long offset, byte[] state, CancellationToken ct = default)
        {
            await _inner.SaveAsync(processorId, offset, state, ct);
            if (_fail) { _fail = false; throw new IOException("committed then disconnected"); }
        }
        public ValueTask<(long Offset, byte[] State)?> LoadAsync(string processorId, CancellationToken ct = default) => _inner.LoadAsync(processorId, ct);
    }
}
