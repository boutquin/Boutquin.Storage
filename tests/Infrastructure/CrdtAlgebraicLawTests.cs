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
/// Pins the join-semilattice laws that make a state-based CRDT correct (DDIA 2e Ch. 5 — CRDTs):
/// <c>merge</c> must be <b>commutative</b>, <b>associative</b>, and <b>idempotent</b>, so replicas
/// that exchange state in any order, any grouping, and any number of times converge to the same
/// least upper bound. The existing CRDT tests assert specific merge outcomes; these assert the
/// algebraic laws directly, which is the actual specification "the math is the spec".
/// </summary>
public sealed class CrdtAlgebraicLawTests
{
    private static GCounter GCounterWith(params (string Node, int Times)[] increments)
    {
        var counter = new GCounter();
        foreach (var (node, times) in increments)
        {
            for (var i = 0; i < times; i++)
            {
                counter.Increment(node);
            }
        }

        return counter;
    }

    private static VectorClock VectorClockWith(params (string Node, int Times)[] increments)
    {
        var clock = new VectorClock();
        foreach (var (node, times) in increments)
        {
            for (var i = 0; i < times; i++)
            {
                clock.Increment(node);
            }
        }

        return clock;
    }

    // ───── GCounter (per-node max; value = sum) ───────────────────────────────

    [Fact]
    public void GCounter_Merge_IsCommutative()
    {
        var a = GCounterWith(("x", 5), ("y", 2));
        var b = GCounterWith(("y", 3), ("z", 7));

        a.Merge(b).Value.Should().Be(b.Merge(a).Value);
    }

    [Fact]
    public void GCounter_Merge_IsAssociative()
    {
        var a = GCounterWith(("x", 5));
        var b = GCounterWith(("y", 3), ("x", 1));
        var c = GCounterWith(("x", 2), ("z", 9));

        a.Merge(b).Merge(c).Value.Should().Be(a.Merge(b.Merge(c)).Value);
    }

    [Fact]
    public void GCounter_Merge_IsIdempotent()
    {
        var a = GCounterWith(("x", 5), ("y", 2));

        a.Merge(a).Value.Should().Be(a.Value);
    }

    // ───── PNCounter (two GCounters; value = P − N) ───────────────────────────

    [Fact]
    public void PNCounter_Merge_IsCommutativeAndIdempotent()
    {
        var a = new PNCounter();
        a.Increment("x");
        a.Increment("x");
        a.Decrement("y");

        var b = new PNCounter();
        b.Increment("y");
        b.Decrement("z");

        a.Merge(b).Value.Should().Be(b.Merge(a).Value);
        a.Merge(a).Value.Should().Be(a.Value);
    }

    // ───── GSet (union) ───────────────────────────────────────────────────────

    [Fact]
    public void GSet_Merge_IsCommutativeAssociativeIdempotent()
    {
        var a = new GSet<string>();
        a.Add("apple");
        a.Add("pear");

        var b = new GSet<string>();
        b.Add("pear");
        b.Add("plum");

        var c = new GSet<string>();
        c.Add("kiwi");

        a.Merge(b).Value.Should().BeEquivalentTo(b.Merge(a).Value);
        a.Merge(b).Merge(c).Value.Should().BeEquivalentTo(a.Merge(b.Merge(c)).Value);
        a.Merge(a).Value.Should().BeEquivalentTo(a.Value);
    }

    // ───── VectorClock (per-node max) ─────────────────────────────────────────

    [Fact]
    public void VectorClock_Merge_IsCommutative()
    {
        var a = VectorClockWith(("x", 3), ("y", 1));
        var b = VectorClockWith(("y", 5), ("z", 2));

        a.Merge(b).GetClock().Should().BeEquivalentTo(b.Merge(a).GetClock());
    }

    [Fact]
    public void VectorClock_Merge_IsAssociative()
    {
        var a = VectorClockWith(("x", 3));
        var b = VectorClockWith(("y", 5), ("x", 1));
        var c = VectorClockWith(("x", 2), ("z", 4));

        a.Merge(b).Merge(c).GetClock().Should().BeEquivalentTo(a.Merge(b.Merge(c)).GetClock());
    }

    [Fact]
    public void VectorClock_Merge_IsIdempotent()
    {
        var a = VectorClockWith(("x", 3), ("y", 1));

        a.Merge(a).GetClock().Should().BeEquivalentTo(a.GetClock());
    }

    // ───── ORSet (add-wins observed-remove set; value = present elements) ─────
    // The hardest CRDT to get right (two tag maps + per-node counters), and the one the law suite
    // was missing. An associativity-breaking regression in the merge — e.g. dropping the per-node
    // counter/tag bookkeeping — is exactly what a law test catches and a scenario test can miss.

    private static ORSet<string> ORSetWith(Action<ORSet<string>> build)
    {
        var set = new ORSet<string>();
        build(set);
        return set;
    }

    [Fact]
    public void ORSet_Merge_IsCommutative()
    {
        var a = ORSetWith(s => { s.Add("n1", "x"); s.Add("n1", "y"); });
        var b = ORSetWith(s => { s.Add("n2", "y"); s.Add("n2", "z"); s.Remove("y"); });

        a.Merge(b).Value.Should().BeEquivalentTo(b.Merge(a).Value);
    }

    [Fact]
    public void ORSet_Merge_IsAssociative()
    {
        var a = ORSetWith(s => s.Add("n1", "x"));
        var b = ORSetWith(s => { s.Add("n2", "y"); s.Add("n2", "x"); });
        var c = ORSetWith(s => { s.Add("n3", "z"); s.Remove("z"); });

        a.Merge(b).Merge(c).Value.Should().BeEquivalentTo(a.Merge(b.Merge(c)).Value);
    }

    [Fact]
    public void ORSet_Merge_IsIdempotent()
    {
        var a = ORSetWith(s => { s.Add("n1", "x"); s.Add("n1", "y"); s.Remove("x"); });

        a.Merge(a).Value.Should().BeEquivalentTo(a.Value);
    }

    [Fact]
    public void ORSet_ConcurrentReAddAfterRemove_AddWins_AndConverges()
    {
        // One replica observed-removes the original add of "x"; another concurrently re-adds "x"
        // under a different node (a fresh tag not covered by the remove). Add-wins keeps "x", and
        // the result converges regardless of merge order. A merge that lost the per-node tag/counter
        // bookkeeping would let the stale remove mask the fresh add (non-convergence).
        var original = ORSetWith(s => s.Add("n1", "x"));

        var a = (ORSet<string>)original.Merge(new ORSet<string>()); // independent copy
        a.Remove("x");                                              // observed-remove of n1's add

        var b = (ORSet<string>)original.Merge(new ORSet<string>()); // independent copy
        b.Add("n2", "x");                                           // concurrent fresh re-add

        a.Merge(b).Value.Should().Contain("x", "the fresh re-add is not covered by the observed-remove");
        b.Merge(a).Value.Should().Contain("x");
        a.Merge(b).Value.Should().BeEquivalentTo(b.Merge(a).Value, "merge must converge on either order");
    }

    // ───── Coverage-gap pins for sibling laws/orders that were asserted only per-case ─────

    [Fact]
    public void PNCounter_Merge_IsAssociative()
    {
        // The combined commutative+idempotent test omitted associativity; a regression using + instead
        // of max on the N (decrement) counter would still pass those but fail associativity.
        var a = new PNCounter(); a.Increment("x"); a.Decrement("y");
        var b = new PNCounter(); b.Increment("y"); b.Increment("z");
        var c = new PNCounter(); c.Decrement("x"); c.Increment("w");

        a.Merge(b).Merge(c).Value.Should().Be(a.Merge(b.Merge(c)).Value);
    }

    [Fact]
    public void VectorClock_CausalOrder_IsTransitive()
    {
        // If A < B and B < C then A < C — happens-before is a transitive partial order (DDIA 2e
        // p. 343). The per-pair Before/After/Concurrent tests do not exercise transitivity; a
        // CompareTo that compared only a subset of nodes could pass them all yet break this.
        var a = new VectorClock(); a.Increment("n1");                                   // {n1:1}
        var b = new VectorClock(); b.Increment("n1"); b.Increment("n2");                 // {n1:1, n2:1}
        var c = new VectorClock(); c.Increment("n1"); c.Increment("n2"); c.Increment("n2"); // {n1:1, n2:2}

        a.CompareCausally(b).Should().Be(VectorClockComparison.Before);
        b.CompareCausally(c).Should().Be(VectorClockComparison.Before);
        a.CompareCausally(c).Should().Be(VectorClockComparison.Before, "A < B and B < C => A < C");
        c.CompareCausally(a).Should().Be(VectorClockComparison.After);
    }

    [Fact]
    public void LamportTimestamp_IncrementAndUpdate_AreStrictlyMonotonic()
    {
        // Every Increment()/Update() must return a value strictly greater than all previously
        // returned values (DDIA 2e p. 340 — Lamport clocks order events by strict increase). The
        // per-case tests pin the +1 formula but not the across-sequence strict-increase invariant.
        var clock = new LamportTimestamp("n1");
        var prev = long.MinValue;
        foreach (var next in new[] { clock.Increment(), clock.Update(10), clock.Increment(), clock.Update(5), clock.Increment() })
        {
            next.Should().BeGreaterThan(prev, "each Lamport tick must strictly increase");
            prev = next;
        }
    }
}
