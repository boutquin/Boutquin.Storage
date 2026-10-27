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
using System.Runtime.CompilerServices;

namespace Boutquin.Storage.Domain.ValueObjects;

/// <summary>
/// Represents a versioned value in an MVCC store, tagged with the transaction ID that created it.
/// Each write creates a new <see cref="VersionedValue{TValue}"/> rather than overwriting the
/// previous one. Readers walk the per-key version chain (newest-first) to find the latest version
/// visible under their snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a record class (reference type), not a record struct.</b> The MVCC store keeps per-key
/// chains in <see cref="System.Collections.Immutable.ImmutableList{T}"/> instances inside a
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/> so readers
/// can walk the chain lock-free. The version's <see cref="IsCommitted"/> flag must be flippable
/// from <see cref="MarkCommitted"/> on commit while existing readers walk a stable list — that
/// requires the version to be addressable by reference. A struct copy would not propagate the
/// commit flip back to the in-list version. Record class semantics + a primary constructor
/// keep the value-object architecture-test invariant happy
/// (<c>ValueObjects_ShouldBeRecordTypes</c>) while permitting the mutable commit flag.
/// </para>
/// <para>
/// <b>Equality contract.</b> The compiler-synthesised <c>Equals</c> / <c>GetHashCode</c> /
/// <c>==</c> / <c>!=</c> compare exactly the three positional members
/// (<see cref="Value"/>, <see cref="TransactionId"/>, <see cref="IsDeleted"/>) and DO NOT
/// consult <see cref="IsCommitted"/>. Two instances that share the same identity tuple but
/// differ in commit state compare equal — this is intentional. <see cref="IsCommitted"/> is
/// volatile bookkeeping that flips at most once per instance from <see langword="false"/> to
/// <see langword="true"/>; folding it into structural equality would mean an instance's hash
/// code changes mid-life, breaking any hash-based collection that already keys on it. Callers
/// that need to distinguish committed from uncommitted versions must read
/// <see cref="IsCommitted"/> explicitly rather than relying on <c>Equals</c>.
/// </para>
/// <para>
/// <b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i>, 2nd ed.
/// (O'Reilly, 2026), Ch. 8 — multi-version concurrency control keeps old versions so that
/// readers never block writers; Ch. 8 (p. 295–297) — version chain walked under a snapshot.
/// </para>
/// </remarks>
/// <typeparam name="TValue">The type of the stored value.</typeparam>
/// <param name="Value">The stored value.</param>
/// <param name="TransactionId">The transaction ID that created this version.</param>
/// <param name="IsDeleted">Whether this version represents a deletion (tombstone).</param>
public sealed record VersionedValue<TValue>(TValue Value, long TransactionId, bool IsDeleted)
{
    // The commit flag is stored in a ConditionalWeakTable rather than a plain private field
    // to keep it invisible to the compiler-synthesised record equality/hash.  Record classes
    // include ALL instance members (including private fields) in the synthesised
    // Equals(VersionedValue<TValue>?) and GetHashCode; a plain "private int _isCommitted"
    // field therefore changes the instance's hash when MarkCommitted() flips it, breaking
    // any Dictionary / HashSet that holds the instance before and after commit.
    //
    // ConditionalWeakTable associates mutable state with an instance by reference without
    // being an instance field — it is invisible to the record's synthesised equality/hash,
    // so hash codes are stable across MarkCommitted() and two instances with the same
    // identity tuple (Value, TransactionId, IsDeleted) compare equal regardless of their
    // commit state (the intentional contract documented in the <remarks> above).
    private static readonly ConditionalWeakTable<VersionedValue<TValue>, StrongBox<int>>
        s_commitFlags = new();

    /// <summary>
    /// Whether this version's writer transaction has committed. Initially <see langword="false"/>
    /// until <see cref="MarkCommitted"/> is called by the MVCC store's commit path. Read with a
    /// volatile load so concurrent readers observe the flip without a gate.
    /// </summary>
    public bool IsCommitted
    {
        get
        {
            if (!s_commitFlags.TryGetValue(this, out var box))
            {
                return false;
            }
            return Volatile.Read(ref box.Value) == 1;
        }
    }

    /// <summary>
    /// Marks this version as committed. Idempotent — multiple calls flip the flag once and
    /// thereafter are no-ops. Volatile write so concurrent readers observe the transition.
    /// </summary>
    public void MarkCommitted()
    {
        var box = s_commitFlags.GetOrCreateValue(this);
        Volatile.Write(ref box.Value, 1);
    }
}
