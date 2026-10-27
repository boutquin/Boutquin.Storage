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
/// Phase C: observed-remove set semantics per Shapiro et al. 2011 §3.3
/// (<i>Conflict-Free Replicated Data Types</i>). These scenarios exercise the
/// tombstone-bearing Merge: elements removed on one replica after observing a specific tag
/// must remain removed after merging with a replica that still carries that tag, while a
/// concurrent Add with a fresh tag must still resurrect the element.
/// </summary>
public sealed class ORSetSemanticsTests
{
    [Fact]
    public void Remove_AfterAdd_ThenMergeWithReplicaThatNeverRemoved_ElementStaysRemoved()
    {
        var r1 = new ORSet<string>();
        var r2 = new ORSet<string>();

        r1.Add("r1", "A");           // tag T1 on r1
        var synced = r1.Merge(r2);   // r2 now carries T1 conceptually, but we model it via merge result
        r2 = (ORSet<string>)synced;  // r2 observes T1

        r1.Remove("A");              // r1 tombstones T1
        var merged = (ORSet<string>)r1.Merge(r2);

        merged.Contains("A").Should().BeFalse(
            "a tombstoned tag must not be resurrected by a replica that still carries that tag");
    }

    [Fact]
    public void Remove_ThenConcurrentAddOnOtherReplica_MergeRestoresElement()
    {
        // {add A on R1 (T1), add A on R2 (T2), merge → Contains(A) via either T1 or T2,
        //  remove A on R1 (tombstones T1+T2), add A on R2 (new tag T3), merge} → A present
        var r1 = new ORSet<string>();
        var r2 = new ORSet<string>();

        r1.Add("r1", "A");
        r2.Add("r2", "A");

        // Converge once so each replica has observed the other's tag.
        r1 = (ORSet<string>)r1.Merge(r2);
        r2 = (ORSet<string>)r2.Merge(r1);

        r1.Remove("A");              // tombstones both T1 and T2 on r1
        r2.Add("r2", "A");           // fresh tag T3 on r2

        var merged = (ORSet<string>)r1.Merge(r2);
        merged.Contains("A").Should().BeTrue(
            "a concurrent Add after the Remove produces a fresh tag that is not in the tombstone set");
    }

    [Fact]
    public void Remove_OnlyTombstonesObservedTags_NewAddAfterRemoveIsVisible()
    {
        var set = new ORSet<string>();

        set.Add("node", "A");
        set.Contains("A").Should().BeTrue();

        set.Remove("A");
        set.Contains("A").Should().BeFalse();

        // Re-adding on the same replica after a local Remove must be observable locally.
        set.Add("node", "A");
        set.Contains("A").Should().BeTrue("Add after Remove with a fresh tag resurrects the element");
    }

    [Fact]
    public void Merge_UnionsTombstonesBetweenReplicas()
    {
        var r1 = new ORSet<string>();
        var r2 = new ORSet<string>();

        r1.Add("r1", "A");
        r1 = (ORSet<string>)r1.Merge(r2);
        r2 = (ORSet<string>)r2.Merge(r1); // both observe T1

        r2.Remove("A"); // r2 tombstones T1

        // r1 has not yet observed the removal. When it merges with r2, the tombstone must
        // carry over; Contains("A") returns false because T1 is tombstoned on both sides.
        var merged = (ORSet<string>)r1.Merge(r2);
        merged.Contains("A").Should().BeFalse();

        // Symmetric: merging in the other direction yields the same state.
        var symmetric = (ORSet<string>)r2.Merge(r1);
        symmetric.Contains("A").Should().BeFalse();
    }

    [Fact]
    public void Value_ReflectsTombstoneFiltering()
    {
        var set = new ORSet<string>();
        set.Add("node", "A");
        set.Add("node", "B");

        set.Value.Should().BeEquivalentTo(["A", "B"]);

        set.Remove("A");

        set.Value.Should().BeEquivalentTo(["B"],
            "Value must not include tombstoned elements");
    }
}
