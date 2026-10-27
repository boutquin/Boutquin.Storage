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

public sealed class DistributedTransactionTests
{
    [Fact]
    public async Task TwoPhaseCommit_PersistsCrossShardOutcomeAndRejectsIntentReuse()
    {
        var directory = Path.Combine(Path.GetTempPath(), "two-phase-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = await DurableTransactionalResource.CreateAsync("a", Path.Combine(directory, "a.log")).ConfigureAwait(true);
            using var second = await DurableTransactionalResource.CreateAsync("b", Path.Combine(directory, "b.log")).ConfigureAwait(true);
            using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(Path.Combine(directory, "decision.log"), [first, second]).ConfigureAwait(true);
            var id = new RequestId("transfer-1");
            TransactionIntent[] plan = [new("a", new Dictionary<string, string?> { ["balance"] = "90" }), new("b", new Dictionary<string, string?> { ["balance"] = "110" })];
            (await coordinator.ExecuteAsync(id, plan).ConfigureAwait(true)).Should().Be(CommitResult.Committed);
            (await first.ReadAsync("balance").ConfigureAwait(true)).Should().Be("90");
            (await second.ReadAsync("balance").ConfigureAwait(true)).Should().Be("110");
            (await coordinator.ExecuteAsync(id, plan).ConfigureAwait(true)).Should().Be(CommitResult.Committed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteAsync(id, [new("a", new Dictionary<string, string?> { ["balance"] = "wrong" })])).ConfigureAwait(true);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }
}
