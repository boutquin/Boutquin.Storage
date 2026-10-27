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
/// Prune-safety properties of <see cref="MvccStore{TKey, TValue}"/> and the
/// <see cref="SnapshotIsolationStore{TKey, TValue}"/> wrapper that forwards its watermark:
/// a legal prune must never change what any retained transaction handle reads, and must never
/// make a later transaction see a commit that happened after it began.
/// </summary>
public sealed class MvccPruneSafetyTests
{
    // An idle store accepts any watermark below long.MaxValue. A watermark above the highest
    // transaction id allocated so far must not fold ids that do not exist yet into the
    // always-visible committed floor: those future transactions have not committed before
    // anyone began, so snapshot isolation must still hide a concurrent writer from a reader.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IdlePrune_AboveHighestAllocatedId_KeepsLaterConcurrentCommitInvisible(bool useMinActiveRecipe)
    {
        using var store = new MvccStore<string, int>();
        var seed = store.BeginTransaction();
        await store.WriteAsync("k", 1, seed).ConfigureAwait(true);
        (await store.CommitAsync(seed).ConfigureAwait(true)).Should().BeTrue();

        var watermark = useMinActiveRecipe ? store.MinActiveTransactionId - 1 : seed + 1;
        await store.PruneBelowAsync(watermark).ConfigureAwait(true);

        var writer = store.BeginTransaction();
        var reader = store.BeginTransaction();
        (await store.ReadAsync("k", reader).ConfigureAwait(true)).Should().Be((1, true));

        await store.WriteAsync("k", 42, writer).ConfigureAwait(true);
        (await store.CommitAsync(writer).ConfigureAwait(true)).Should().BeTrue();

        (await store.ReadAsync("k", reader).ConfigureAwait(true)).Should().Be((1, true),
            "the writer committed after the reader began, so the reader's snapshot must not see it");
    }

    [Fact]
    public async Task SnapshotIsolationStore_IdlePruneAtMinActiveRecipe_KeepsReadsRepeatable()
    {
        using var store = new SnapshotIsolationStore<string, int>();
        var seed = store.BeginTransaction();
        await store.WriteAsync("k", 1, seed).ConfigureAwait(true);
        (await store.CommitAsync(seed).ConfigureAwait(true)).Should().BeTrue();

        await store.PruneCommittedBelowAsync(store.MinActiveCommitTimestamp - 1).ConfigureAwait(true);

        var writer = store.BeginTransaction();
        var reader = store.BeginTransaction();
        (await store.ReadAsync("k", reader).ConfigureAwait(true)).Should().Be((1, true));
        await store.WriteAsync("k", 2, writer).ConfigureAwait(true);
        (await store.CommitAsync(writer).ConfigureAwait(true)).Should().BeTrue();

        (await store.ReadAsync("k", reader).ConfigureAwait(true)).Should().Be((1, true),
            "snapshot isolation forbids a non-repeatable read inside one transaction");
    }

    // A committed handle whose id is above the watermark keeps its snapshot and stays readable,
    // so the version it reads must survive the prune. Here the backdrop (newest committed id at
    // or below the watermark) is a newer write the handle's snapshot excludes.
    [Fact]
    public async Task Prune_KeepsVersionReadByCommittedHandle_WhenBackdropIsNewerWrite()
    {
        using var store = new MvccStore<string, string>();
        var seed = store.BeginTransaction();
        await store.WriteAsync("k", "old", seed).ConfigureAwait(true);
        await store.CommitAsync(seed).ConfigureAwait(true);

        var writer = store.BeginTransaction();
        var handle = store.BeginTransaction();
        (await store.ReadAsync("k", handle).ConfigureAwait(true)).Should().Be(("old", true));
        await store.WriteAsync("k", "new", writer).ConfigureAwait(true);
        await store.CommitAsync(writer).ConfigureAwait(true);
        await store.CommitAsync(handle).ConfigureAwait(true);
        (await store.ReadAsync("k", handle).ConfigureAwait(true)).Should().Be(("old", true));

        store.MinActiveTransactionId.Should().Be(long.MaxValue);
        await store.PruneBelowAsync(writer).ConfigureAwait(true);

        (await store.ReadAsync("k", handle).ConfigureAwait(true)).Should().Be(("old", true),
            "a committed handle above the watermark keeps its begin-time view across a prune");
    }

    // Same contract, other chain shape: a low-id transaction commits late, so its version is the
    // chain head and the backdrop, while the committed handle reads an older-id version beneath it.
    [Fact]
    public async Task Prune_KeepsVersionReadByCommittedHandle_WhenLateLowIdCommitIsBackdrop()
    {
        using var store = new MvccStore<string, string>();
        var late = store.BeginTransaction();
        var early = store.BeginTransaction();
        await store.WriteAsync("k", "v2", early).ConfigureAwait(true);
        await store.CommitAsync(early).ConfigureAwait(true);

        var handle = store.BeginTransaction();
        await store.WriteAsync("k", "vLate", late).ConfigureAwait(true);
        await store.CommitAsync(late).ConfigureAwait(true);
        (await store.ReadAsync("k", handle).ConfigureAwait(true)).Should().Be(("v2", true));
        await store.CommitAsync(handle).ConfigureAwait(true);

        store.MinActiveTransactionId.Should().Be(long.MaxValue);
        await store.PruneBelowAsync(early).ConfigureAwait(true);

        (await store.ReadAsync("k", handle).ConfigureAwait(true)).Should().Be(("v2", true),
            "the key existed in every committed state the handle could observe");
    }
}
