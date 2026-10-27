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
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Aggregates the three interface contracts every key in a file-backed storage engine must
/// satisfy: ordered comparison (<see cref="IComparable{TSelf}"/>), value equality
/// (<see cref="IEquatable{TSelf}"/>), and on-disk serialization
/// (<see cref="ISerializable{TSelf}"/>). Consumers add <c>new()</c> at the use-site so the
/// engine can construct fresh instances during deserialization.
/// </summary>
/// <typeparam name="TSelf">The implementing key type. Curiously-recurring template pattern —
/// the type parameter is the implementer itself, which lets the comparison, equality, and
/// serialization contracts close over the concrete key type without runtime boxing.</typeparam>
/// <remarks>
/// <para>
/// <b>Why an aggregator interface.</b> Before this type, every engine signature in the public
/// API repeated <c>where TKey : ISerializable&lt;TKey&gt;, IComparable&lt;TKey&gt;,
/// IEquatable&lt;TKey&gt;, new()</c>. The repetition was load-bearing — drop one of the three
/// interface constraints and the file-backed engine cannot serialise, sort, or test for
/// equivalence — but the four-element clause obscured intent at every call-site. This
/// aggregator collapses the three interface constraints into one named contract;
/// <c>new()</c> stays at the use-site because C# does not let an interface impose a
/// parameterless-constructor constraint on its implementers transitively.
/// </para>
/// <para>
/// <b>What this is not.</b> This contract addresses keys for *file-backed* storage paths only.
/// In-memory key-value stores (<see cref="IKeyValueStore{TKey, TValue}"/>,
/// <see cref="IBPlusTree{TKey, TValue}"/>, indexing primitives) require ordered comparison
/// and sometimes equality but never on-disk serialization, so they continue to declare the
/// narrower <c>where TKey : IComparable&lt;TKey&gt;</c> form directly. Forcing every
/// in-memory keyspace through <see cref="ISerializable{TSelf}"/> would over-constrain those
/// types for no engineering benefit.
/// </para>
/// <para>
/// <b>Implementations.</b> The source generator emits <c>IStorageKey&lt;TSelf&gt;</c> on every
/// <c>[Key]</c>-annotated record struct (the three base interfaces are already part of the
/// generator output). Hand-rolled keys built on
/// <see cref="Boutquin.Storage.Domain.Helpers.SerializableWrapper{T}"/> satisfy the contract
/// the same way.
/// </para>
/// <para>
/// <b>Ordering authority is <see cref="IComparable{TSelf}.CompareTo"/>, not the serialized
/// bytes.</b> The contract that defines key order is <see cref="IComparable{TSelf}.CompareTo"/>
/// exclusively. The bytes produced by <see cref="ISerializable{TSelf}.Serialize"/> are an
/// on-disk transport encoding and are NOT required to sort byte-lexicographically in the same
/// order as <c>CompareTo</c>. SSTable binary search, range scans, and every other ordered read
/// path rely solely on <c>CompareTo</c>; they never sort by raw serialized bytes. The
/// encoding therefore need not be byte-lex-comparable (e.g. little-endian fixed-width fields, as
/// <see cref="Boutquin.Storage.Domain.ValueObjects.TimeSeries.TimeSeriesKey{TDatasetKey}"/>
/// uses). Only the source generator's <c>[Key]</c> ordered-wire encoding deliberately makes the
/// two orders coincide; that is an extra guarantee of that one path, not a requirement of this
/// interface.
/// </para>
/// <para>
/// <b>Equal keys must serialize to byte-identical output.</b> The encoding is free in its order,
/// not in its canonical form: two keys that are equal under <c>CompareTo</c> (and
/// <c>Equals</c>) must produce byte-identical <c>Serialize</c> output. The SSTable bloom filter
/// and every other byte-keyed structure hash the serialized key, so a key whose equal values
/// serialize differently (for example a case-insensitive key that writes the caller's spelling)
/// is reported absent after flush when looked up through another spelling. Canonicalise before
/// serializing — <see cref="Boutquin.Storage.Domain.Helpers.SerializableWrapper{T}"/> does so for
/// <c>-0.0</c> and NaN payloads for exactly this reason.
/// </para>
/// <para>
/// <b>Reference:</b> Kleppmann &amp; Riccomini, <i>Designing Data-Intensive Applications</i>,
/// 2nd ed. (O'Reilly, 2026), Ch. 4 p. 79–82 — SSTable key-ordering invariants.
/// </para>
/// </remarks>
public interface IStorageKey<TSelf>
    : IComparable<TSelf>, IEquatable<TSelf>, ISerializable<TSelf>
    where TSelf : IStorageKey<TSelf>
{
}
