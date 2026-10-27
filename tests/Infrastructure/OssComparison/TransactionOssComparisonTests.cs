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

// Scenario adaptations: Copyright (c) 1996-2024, PostgreSQL Global Development Group.
// See Distributed/provenance.md and PostgreSQL-LICENSE.txt for pinned sources and differences.
namespace Boutquin.Storage.Infrastructure.Tests.OssComparison;

public sealed class TransactionOssComparisonTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("transaction-oss-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public async Task Postgres_SimpleWriteSkew_EnumeratedPointReadsAbortOneCommit()
    {
        using var store = new SnapshotIsolationStore<int, string>();
        var initial = store.BeginTransaction();
        await store.WriteAsync(5, "apple", initial).ConfigureAwait(true);
        await store.WriteAsync(7, "pear", initial).ConfigureAwait(true);
        await store.WriteAsync(11, "banana", initial).ConfigureAwait(true);
        (await store.CommitAsync(initial).ConfigureAwait(true)).Should().BeTrue();
        var first = store.BeginTransaction();
        var second = store.BeginTransaction();
        // The upstream predicates are explicitly expanded into the known point keys. This checks
        // point dependency detection and makes no claim about phantom/predicate tracking.
        foreach (var key in new[] { 5, 7, 11 })
        {
            var left = await store.ReadAsync(key, first).ConfigureAwait(true);
            var right = await store.ReadAsync(key, second).ConfigureAwait(true);
            if (left.Value == "pear") { await store.WriteAsync(key, "apple", first).ConfigureAwait(true); }
            if (right.Value == "apple") { await store.WriteAsync(key, "pear", second).ConfigureAwait(true); }
        }
        (await store.CommitAsync(first).ConfigureAwait(true)).Should().BeTrue();
        (await store.CommitAsync(second).ConfigureAwait(true)).Should().BeFalse();
        var reader = store.BeginTransaction();
        (await store.ReadAsync(5, reader).ConfigureAwait(true)).Value.Should().Be("apple");
        (await store.ReadAsync(7, reader).ConfigureAwait(true)).Value.Should().Be("apple");
        (await store.ReadAsync(11, reader).ConfigureAwait(true)).Value.Should().Be("banana");
    }

    [Fact]
    public async Task Postgres_SimpleUpgradeDeadlock_OneVictimReleasesTheSurvivor()
    {
        using var store = new StrictTwoPhaseLockingStore<string, int>();
        var first = store.BeginTransaction();
        var second = store.BeginTransaction();
        await store.ReadAsync("object", first).ConfigureAwait(true);
        await store.ReadAsync("object", second).ConfigureAwait(true);
        var firstUpgrade = store.WriteAsync("object", 1, first);
        firstUpgrade.IsCompleted.Should().BeFalse();
        var victim = await Assert.ThrowsAsync<TransactionDeadlockException>(
            () => store.WriteAsync("object", 2, second).WaitAsync(TimeSpan.FromSeconds(3))).ConfigureAwait(true);
        victim.TransactionId.Should().Be(second);
        await firstUpgrade.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        await store.CommitAsync(first).ConfigureAwait(true);
        var reader = store.BeginTransaction();
        (await store.ReadAsync("object", reader).ConfigureAwait(true)).Value.Should().Be(1);
        await store.CommitAsync(reader).ConfigureAwait(true);
    }

    [Fact]
    public async Task Postgres_PreparedTransactions_ReopenCanCommitOneAndAbortAnother()
    {
        var path = Path.Combine(_directory.FullName, "participant.log");
        var commitId = new RequestId("commit-after-reopen");
        var abortId = new RequestId("abort-after-reopen");
        using (var before = await DurableTransactionalResource.CreateAsync("p", path).ConfigureAwait(true))
        {
            (await before.PrepareAsync(commitId, new Dictionary<string, string?> { ["a"] = "kept" }).ConfigureAwait(true))
                .Should().Be(PrepareResult.Prepared);
            (await before.PrepareAsync(abortId, new Dictionary<string, string?> { ["b"] = "discarded" }).ConfigureAwait(true))
                .Should().Be(PrepareResult.Prepared);
        }
        using (var restored = await DurableTransactionalResource.CreateAsync("p", path).ConfigureAwait(true))
        {
            (await restored.ReadAsync("a").ConfigureAwait(true)).Should().BeNull();
            (await restored.ReadAsync("b").ConfigureAwait(true)).Should().BeNull();
            (await restored.CommitAsync(commitId).ConfigureAwait(true)).Should().Be(CommitResult.Committed);
            (await restored.AbortAsync(abortId).ConfigureAwait(true)).Should().Be(AbortResult.Aborted);
        }
        using var reopened = await DurableTransactionalResource.CreateAsync("p", path).ConfigureAwait(true);
        (await reopened.ReadAsync("a").ConfigureAwait(true)).Should().Be("kept");
        (await reopened.ReadAsync("b").ConfigureAwait(true)).Should().BeNull();
        (await reopened.CommitAsync(commitId).ConfigureAwait(true)).Should().Be(CommitResult.Committed);
        (await reopened.AbortAsync(abortId).ConfigureAwait(true)).Should().Be(AbortResult.Aborted);
    }
}
