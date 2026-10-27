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
using Boutquin.Storage.Domain.Helpers;

namespace Boutquin.Storage.Domain.ValueObjects.TimeSeries;

/// <summary>
/// Composite key for a time-series record: dataset first, date second.
/// </summary>
/// <param name="Dataset">Per-entity partition key (e.g. provider:granularity:ticker).</param>
/// <param name="Date">The record's sort-defining date.</param>
/// <typeparam name="TDatasetKey">
/// The dataset key type. Must be comparable (for ordering) and serialisable (for persistence).
/// The composite serialiser writes <paramref name="Dataset"/> first then a 4-byte day number.
/// Its encoding must fully deserialize to a comparer-equal dataset and reproduce identical bytes;
/// <see cref="DatasetKeyEncoding"/> rejects encodings that lose identity instead of merging datasets.
/// </typeparam>
/// <remarks>
/// <para><b>Why dataset-first:</b> DDIA Ch. 7 (Kleppmann, pp 202–203) warns that a
/// timestamp-first key produces write hotspots at the "head" of the timeline. Prefixing
/// with the dataset distributes writes across the key space while preserving date-order
/// within each dataset, so a range scan over <c>(dataset, from)..(dataset, to)</c> returns
/// exactly the requested window without cross-dataset interleaving.</para>
/// <para><b>Why a record struct:</b> value-semantics pair with no identity; cheap to copy
/// and compare by value. Record structs auto-synthesise the equality operators and
/// deconstruction.</para>
/// <para><b>One key identity.</b> <see cref="CompareTo"/>, <see cref="Equals(TimeSeriesKey{TDatasetKey})"/>,
/// <see cref="GetHashCode"/> and <see cref="Serialize"/> agree: two keys are equal exactly when their
/// dates are equal and their datasets compare equal and serialize to identical bytes. The dataset key's
/// own <c>Equals</c> is not consulted, so a dataset key with reference equality (or an equality that
/// disagrees with its serialized form) still yields a key that the engine's hash-keyed tombstone sets,
/// its comparer-ordered memtable and segments, and its bloom filter over the serialized bytes all
/// resolve to the same record. Datasets that compare equal but serialize differently are distinct
/// datasets: <see cref="CompareDatasets"/> orders them by their serialized bytes, and that dataset order
/// comes before the date, so every byte-distinct dataset occupies one contiguous key range and the
/// bounds <c>(dataset, from)..(dataset, to)</c> never admit another dataset's rows.</para>
/// </remarks>
public readonly record struct TimeSeriesKey<TDatasetKey>(TDatasetKey Dataset, DateOnly Date)
    : Boutquin.Storage.Domain.Interfaces.IStorageKey<TimeSeriesKey<TDatasetKey>>
    where TDatasetKey : IComparable<TDatasetKey>, ISerializable<TDatasetKey>, new()
{
    /// <summary>
    /// Compares by dataset first (see <see cref="CompareDatasets"/>), then by <see cref="Date"/>, so a
    /// zero result means equal dates and datasets that serialize identically.
    /// </summary>
    public int CompareTo(TimeSeriesKey<TDatasetKey> other)
    {
        var byDataset = CompareDatasets(Dataset, other.Dataset);
        return byDataset != 0 ? byDataset : Date.CompareTo(other.Date);
    }

    /// <summary>
    /// The dataset component of the key order: the dataset comparer first, then — only for
    /// comparer-equal datasets — their serialized bytes. Zero means the datasets are one dataset: they
    /// compare equal and serialize identically. Every structure that groups or matches keys by dataset
    /// uses this relation, so it agrees with <see cref="CompareTo"/> and <see cref="Equals(TimeSeriesKey{TDatasetKey})"/>.
    /// </summary>
    /// <param name="left">The first dataset.</param>
    /// <param name="right">The second dataset.</param>
    /// <returns>A negative value, zero, or a positive value as <paramref name="left"/> orders before,
    /// is the same dataset as, or orders after <paramref name="right"/>.</returns>
    public static int CompareDatasets(TDatasetKey left, TDatasetKey right)
    {
        var byComparer = Comparer<TDatasetKey>.Default.Compare(left, right);
        if (byComparer != 0)
        {
            return byComparer;
        }

        // One instance serializes identically to itself; skip the serialization on the common
        // same-instance comparison.
        return typeof(TDatasetKey).IsValueType || !ReferenceEquals(left, right)
            ? CompareDatasetBytes(left, right)
            : 0;
    }

    /// <summary>
    /// Returns <see langword="true"/> exactly when <see cref="CompareTo"/> returns zero: equal dates,
    /// comparer-equal datasets and identical dataset bytes.
    /// </summary>
    public bool Equals(TimeSeriesKey<TDatasetKey> other) => CompareTo(other) == 0;

    /// <summary>
    /// Hashes the date and the dataset's serialized bytes — the components every equal key shares.
    /// </summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Date);
        if (Dataset is not null)
        {
            hash.AddBytes(DatasetBytes(Dataset));
        }

        return hash.ToHashCode();
    }

    // Byte identity of the dataset: what Serialize writes, and so what the bloom filter and every
    // on-disk structure see. A null dataset (default(TimeSeriesKey)) has no bytes; the comparer has
    // already ordered null against non-null, so only the both-null case reaches here.
    private static int CompareDatasetBytes(TDatasetKey left, TDatasetKey right)
    {
        if (left is null || right is null)
        {
            return 0;
        }

        return DatasetBytes(left).AsSpan().SequenceCompareTo(DatasetBytes(right));
    }

    private static byte[] DatasetBytes(TDatasetKey dataset) => DatasetKeyEncoding.SerializeValidated(dataset);

    /// <summary>
    /// Writes the dataset via its own <see cref="ISerializable{T}.Serialize"/> then a 4-byte
    /// little-endian day number for the date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Encoding:</b> the date component is serialized as a 4-byte little-endian
    /// <see cref="DateOnly.DayNumber"/> integer, following the project-wide convention of
    /// using <c>BinaryPrimitives.WriteInt32LittleEndian</c> for on-disk integer fields.
    /// </para>
    /// <para>
    /// <b>Not byte-lex comparable.</b> The little-endian layout means the encoded bytes do NOT
    /// sort in the same order as the key's numeric value. SSTable binary search and range scans
    /// rely exclusively on <see cref="IComparable{T}.CompareTo"/> — never on raw byte
    /// comparison of the serialized form. Do not use the serialized bytes for ordering.
    /// </para>
    /// <para>Dataset validation completes before any bytes reach <paramref name="stream"/>.
    /// A lossy dataset encoding raises <see cref="ArgumentException"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The dataset encoding does not preserve comparison
    /// identity and identical bytes across a complete serialize/deserialize cycle.</exception>
    public void Serialize(Stream stream)
    {
        stream.Write(DatasetBytes(Dataset));
        Span<byte> buf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf, Date.DayNumber);
        stream.Write(buf);
    }

    /// <summary>
    /// Reads the dataset via <typeparamref name="TDatasetKey"/>'s <c>Deserialize</c>, then a
    /// 4-byte little-endian day number.
    /// </summary>
    public static TimeSeriesKey<TDatasetKey> Deserialize(Stream stream)
    {
        var dataset = TDatasetKey.Deserialize(stream);
        Span<byte> buf = stackalloc byte[4];
        // Stream.Read may return fewer bytes than requested before end of stream; read fully and
        // treat only a genuine end of stream as truncation.
        VarintHelpers.ReadExact(stream, buf);

        var dayNumber = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buf);
        // A forged/bit-rotted day number outside the DateOnly range must surface a typed corruption
        // exception, not a raw ArgumentOutOfRangeException from FromDayNumber. ToDateOnly range-checks
        // first and throws InvalidDataException (the decode-corruption convention).
        return new TimeSeriesKey<TDatasetKey>(dataset, CorruptionGuards.ToDateOnly(dayNumber));
    }
}
