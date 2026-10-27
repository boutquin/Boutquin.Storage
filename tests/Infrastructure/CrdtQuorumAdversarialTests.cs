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
/// Adversarial conflict-resolution tests for the distributed primitives, closing the named CRDT
/// and quorum test-coverage gaps: ORSet add-wins under a genuinely concurrent add + remove
/// (Shapiro et al. 2011 §3.3 — the defining property of an observed-remove set), and
/// QuorumReplication read-after-write under degraded (but still quorum-meeting) availability.
/// </summary>
public sealed class CrdtQuorumAdversarialTests
{
    /// <summary>
    /// ORSet add-wins on concurrent add + remove. Two replicas converge on element A. Then, with
    /// no further communication between them, R1 REMOVES A (tombstoning only the add-tag it has
    /// observed) while R2 concurrently ADDS A again (a fresh tag R1's remove never saw). After the
    /// replicas merge, A must be PRESENT — the concurrent add wins over the concurrent remove
    /// because the remove only tombstones tags it observed and the new tag is outside that set.
    /// </summary>
    [Fact]
    public void ORSet_ConcurrentAddAndRemove_AddWins()
    {
        var r1 = new ORSet<string>();
        var r2 = new ORSet<string>();

        // Both replicas observe A (tag T1 from r1).
        r1.Add("r1", "A");
        r1 = (ORSet<string>)r1.Merge(r2);
        r2 = (ORSet<string>)r2.Merge(r1);
        r1.Contains("A").Should().BeTrue();
        r2.Contains("A").Should().BeTrue();

        // CONCURRENT, no communication between the two operations:
        //   R1 removes A  → tombstones T1 (the only tag it observed).
        //   R2 adds A     → fresh tag T2 that R1's remove never saw.
        r1.Remove("A");
        r2.Add("r2", "A");

        // Locally, each replica reflects its own last op.
        r1.Contains("A").Should().BeFalse("r1 tombstoned the only tag it had observed");
        r2.Contains("A").Should().BeTrue("r2 holds a fresh, un-tombstoned tag");

        // Merge resolves the concurrent add/remove by add-wins: T2 is live on both directions.
        var mergedR1 = (ORSet<string>)r1.Merge(r2);
        var mergedR2 = (ORSet<string>)r2.Merge(r1);
        mergedR1.Contains("A").Should().BeTrue(
            "the concurrent Add produced a tag the concurrent Remove never tombstoned — add wins");
        mergedR2.Contains("A").Should().BeTrue("merge is commutative — add-wins holds either direction");
    }

    /// <summary>
    /// QuorumReplication degraded-availability read-after-write. With N = 3, W = 2, R = 2 the
    /// quorum overlap W + R &gt; N holds. Drop ONE replica (availability degraded to 2 of 3, still
    /// ≥ both W and R), write a key, then read it back: because the surviving two replicas still
    /// satisfy both quorums and the W-window overlaps the R-window, the read MUST observe the
    /// just-written value. Pins that read-after-write holds at the boundary of degraded-but-
    /// quorum-meeting availability — the regime the class's overlap guarantee actually covers.
    /// </summary>
    [Fact]
    public async Task QuorumReplication_DegradedButQuorumMeeting_ReadObservesPriorWrite()
    {
        using var quorum = new QuorumReplication<string, string>(n: 3, w: 2, r: 2);

        // Degrade availability: one replica down, two up (still ≥ W and ≥ R).
        quorum.SetReplicaAvailability("replica-2", isAvailable: false);

        await quorum.WriteAsync("k", "v1").ConfigureAwait(true);
        var (value, found, version) = await quorum.ReadAsync("k").ConfigureAwait(true);

        found.Should().BeTrue("the key was written to a meeting quorum and must be readable");
        value.Should().Be("v1",
            "with W + R > N and two surviving replicas the read window overlaps the write window, "
            + "so the just-written value is visible even under degraded availability");
        version.Should().BeGreaterThan(0);
    }
}
