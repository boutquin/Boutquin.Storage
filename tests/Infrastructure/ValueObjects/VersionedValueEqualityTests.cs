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

namespace Boutquin.Storage.Infrastructure.Tests.ValueObjects;

/// <summary>
/// Pins the equality contract documented in VersionedValue&lt;TValue&gt;:
/// compiler-synthesised Equals / GetHashCode compare exactly the three positional
/// members (Value, TransactionId, IsDeleted) and must NOT consult IsCommitted.
///
/// Rationale (C11): the private backing field _isCommitted was included in the
/// record-class synthesised equality because C# records include ALL instance
/// members — not just positional properties.  Folding IsCommitted into equality
/// means an instance's hash code changes after MarkCommitted(), breaking any
/// hash-based collection (Dictionary, HashSet) that holds the instance as a key
/// or element.  The fix overrides Equals / GetHashCode to use only the three
/// identity properties.
/// </summary>
public sealed class VersionedValueEqualityTests
{
    // ── Equality is independent of IsCommitted ──────────────────────────────

    [Fact]
    public void EqualsBefore_AndAfter_MarkCommitted_ReturnsSameResult()
    {
        var a = new VersionedValue<int>(42, 1L, false);
        var b = new VersionedValue<int>(42, 1L, false);

        // Before commit both equal.
        a.Equals(b).Should().BeTrue("same identity tuple, neither committed");

        // After committing one instance the equality must NOT change.
        a.MarkCommitted();
        a.Equals(b).Should().BeTrue(
            "IsCommitted must not participate in Equals — two versions with the same " +
            "Value/TransactionId/IsDeleted tuple that differ only in commit state are equal");
    }

    [Fact]
    public void GetHashCode_IsStable_AcrossMarkCommitted()
    {
        var v = new VersionedValue<string>("hello", 7L, false);
        var hashBefore = v.GetHashCode();

        v.MarkCommitted();
        var hashAfter = v.GetHashCode();

        hashAfter.Should().Be(hashBefore,
            "inserting a VersionedValue into a HashSet / Dictionary and later calling " +
            "MarkCommitted must not change its bucket; the hash must be derived only from " +
            "the immutable identity tuple");
    }

    [Fact]
    public void EqualOperator_IsIndependent_OfIsCommitted()
    {
        var a = new VersionedValue<long>(99L, 2L, true);
        var b = new VersionedValue<long>(99L, 2L, true);
        a.MarkCommitted();

        (a == b).Should().BeTrue("== must not consult IsCommitted");
        (a != b).Should().BeFalse("!= must be consistent with ==");
    }

    // ── Positive: distinct identity tuples are still unequal ───────────────

    [Fact]
    public void Equals_DifferentValue_ReturnsFalse()
    {
        var a = new VersionedValue<int>(1, 1L, false);
        var b = new VersionedValue<int>(2, 1L, false);

        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equals_DifferentTransactionId_ReturnsFalse()
    {
        var a = new VersionedValue<int>(1, 1L, false);
        var b = new VersionedValue<int>(1, 2L, false);

        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equals_DifferentIsDeleted_ReturnsFalse()
    {
        var a = new VersionedValue<int>(1, 1L, false);
        var b = new VersionedValue<int>(1, 1L, true);

        a.Equals(b).Should().BeFalse();
    }

    // ── Hash-based collection safety proof ─────────────────────────────────

    [Fact]
    public void HashSet_StaysConsistent_AfterMarkCommitted()
    {
        var v = new VersionedValue<int>(5, 3L, false);
        var set = new HashSet<VersionedValue<int>> { v };

        // Simulate what the MVCC store does: hold a reference in a collection
        // and later flip the commit flag.
        v.MarkCommitted();

        // The set must still recognise v as a member — bucket lookup by hash must work.
        set.Contains(v).Should().BeTrue(
            "MarkCommitted must not relocate the element to a different hash bucket");
    }
}
