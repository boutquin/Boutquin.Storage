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
using System.Reflection;
using Boutquin.Storage.Domain.Attributes;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

/// <summary>
/// Production columnar segment format. Consumes the source-generator-emitted
/// <c>ColumnarFieldPlan</c> on <typeparamref name="TRecord"/> and dispatches each column to
/// the Phase-3 codec named by its <see cref="ColumnEncoding"/> entry.
/// </summary>
/// <typeparam name="TRecord">
/// A dated record annotated with <c>[ColumnarStorable]</c>. The source generator must have
/// emitted a <c>ColumnarFieldPlan</c> static property on the type; see
/// <c>ColumnarSourceGenerator</c>.
/// </typeparam>
/// <remarks>
/// <para><b>Codec dispatch:</b> each field's <see cref="ColumnEncoding"/> maps 1-to-1 to a
/// Phase-3 codec (or, for <see cref="ColumnEncoding.RawDouble"/>, to inline IEEE-754 raw
/// bytes). The on-disk <see cref="ColumnEncodingTag"/> byte stored in the footer matches the
/// codec that wrote the bytes, and every read decodes with that footer codec, so a reader whose
/// <see cref="ColumnEncoding"/> annotation differs from the writer's still returns the written
/// values. The property's runtime type fixes the decoded CLR shape; a footer field type that
/// disagrees with it is rejected with <see cref="CorruptSegmentException"/>.</para>
/// <para><b>Row order:</b> <see cref="WriteAsync"/> does not sort, and <see cref="ReadAsync"/>,
/// <see cref="StreamWhereAsync"/> and <see cref="ReadBatchesAsync"/> do not either: rows come back
/// in stored order, row group by row group and row by row. That is date-ascending only when the
/// writer was given date-ascending records. A caller that needs a date-ordered stream must sort;
/// the columnar segment envelope sorts each block's records by date before yielding them.</para>
/// <para><b>Row-group planning:</b> bounded by <c>rowGroupMaxRows</c> and
/// <c>rowGroupMaxDateRange</c>, whichever triggers first. The segment footer describes at most
/// 65,535 row groups; a block those bounds would split into more is planned with evenly widened
/// groups (at least <c>ceil(rows / 65,535)</c> rows each) rather than refused at flush.</para>
/// <para><b>Projection:</b> <see cref="ColumnProjection"/> is strict-by-default on missing
/// columns; opt in to permissive fills via <c>AllowMissing = true</c>. <see cref="ReadAsync"/>
/// and <see cref="StreamWhereAsync"/> filter rows by date, so they always decode and materialise
/// the column whose property implements <see cref="IHasDate.Date"/> in addition to the projected
/// fields; when <see cref="IHasDate.Date"/> is implemented by other code (for example an explicit
/// implementation over a differently-named column) they decode every column. Predicate columns are
/// validated against the segment like projected columns: an undeclared one raises
/// <see cref="ColumnNotFoundException"/> unless the projection sets <c>AllowMissing</c>.</para>
/// <para><b>Decimal contract:</b> decimal columns round-trip <i>value</i>-preserving, not
/// representation-preserving. Dictionary encoding keys distinct values by decimal equality
/// (scale-insensitive), so value-equal inputs at different scales (<c>1m</c> vs <c>1.0m</c>)
/// deterministically collapse to the first-encountered representation;
/// <see cref="ColumnEncoding.ScaledIntegerBitPacked"/> re-scales every value to the column's
/// common (max) scale on decode. Per-row-group CRCs scale-normalize decimals on both the
/// write and read builds, so this value-preserving re-scaling never trips quarantine.</para>
/// <para><b>Error contract:</b> the reader rejects malformed input — magic/trailer mismatch, CRC corruption, truncation, unknown format
/// version, unknown per-column encoding tag, unknown per-column codec version, strict-mode
/// missing columns, and per-column decode failures via <see cref="ColumnDecodeException"/>.
/// <see cref="ReadAsync"/>, <see cref="ReadBatchesAsync"/> and <see cref="StreamWhereAsync"/>
/// verify each row group's CRC32C whenever the read decodes all of its columns and throw
/// <see cref="CorruptSegmentException"/> on a mismatch; a projection to a strict column subset
/// cannot evaluate the all-column CRC and is not verified. <see cref="ReadRowGroupsAsync"/>
/// quarantines a mismatching row group instead of throwing.</para>
/// </remarks>
public sealed class CompressedColumnarSegmentFormat<TRecord> : IColumnarSegmentFormat<TRecord>,
    IColumnarRecordAdmission<TRecord>
    where TRecord : IHasDate, IColumnarRehydratable<TRecord>, new()
{
    private const string PlanPropertyName = "ColumnarFieldPlan";

    /// <summary>
    /// Read-side absolute ceiling on the number of rows a single row group may declare, used as
    /// the allocation-before-bound DoS guard for the restartable run-length codecs (DeltaVlq,
    /// BitmapRle, DeltaOfDeltaVlq). Every read path decodes one row group at a time, so this cap
    /// bounds each decode allocation; it is never scaled by the footer's row-group count. The footer does NOT record the writer's <c>rowGroupMaxRows</c>
    /// (see <see cref="Footer.RowGroupDescriptor"/>), so the reader cannot recover the exact write-
    /// time cap; it uses this generous absolute constant instead. The value is chosen to sit
    /// comfortably above any plausible writer configuration — the default
    /// <c>rowGroupMaxRows = 1 &lt;&lt; 17</c> (131 072), and any hand-raised value into the low
    /// millions — yet far below OOM territory: a forged <c>rg.RowCount</c> near
    /// <see cref="int.MaxValue"/> (≈2.1 billion) exceeds it and is rejected before the
    /// <c>new T[count]</c> allocation, whereas a legitimate row group (which a run-length codec
    /// compresses into a handful of bytes regardless of row count) passes. Bounding the SEGMENT
    /// BYTE LENGTH here would be wrong — a run-length codec decodes many rows from few bytes, so
    /// "N bytes ⇒ ≤ N rows" is false and false-rejects valid compressible columns (F-W3-regress-01).
    /// 1 &lt;&lt; 26 = 67 108 864 rows ⇒ at most ≈512 MiB for one row group's <c>long[]</c> — a
    /// catchable, bounded allocation, never an uncatchable OOM.
    /// </summary>
    internal const long MaxDecodableRowsPerRowGroup = 1L << 26;

    private readonly int _rowGroupMaxRows;
    private readonly TimeSpan _rowGroupMaxDateRange;
    private readonly IReadOnlyList<ColumnInfo> _columns;
    // O(1) name-to-ColumnInfo lookup for ReadRowGroupsAsync and ScanRowGroupsAsync.
    // Without this, every per-row-group column decode called FirstOrDefault over _columns —
    // O(columns × row_groups) scan instead of O(1) dictionary hit.
    private readonly Dictionary<string, ColumnInfo> _columnsByName;
    // The column whose property getter implements IHasDate.Date, which the date filter reads. Null
    // when IHasDate is implemented by other code (e.g. an explicit implementation returning a
    // differently-named column); date-filtered reads then decode every column.
    private readonly string? _dateColumnName;
    // String columns, each flagged when it carries a [BitmapIndex]. Write admission checks every
    // one before the engine acknowledges a record: the codecs encode neither a null nor an unpaired
    // surrogate, and a [BitmapIndex] value becomes a footer key whose UTF-8 length is capped.
    private readonly (ColumnInfo Column, bool BitmapIndexed)[] _stringColumns;

    /// <summary>
    /// Creates a compressed columnar format for <typeparamref name="TRecord"/>. The type
    /// must expose a static <see cref="IReadOnlyList{ColumnarFieldDescriptor}"/> property
    /// named <c>ColumnarFieldPlan</c> — emitted by the Phase-4 source generator when the
    /// record is annotated with <c>[ColumnarStorable]</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TRecord"/> does not expose a <c>ColumnarFieldPlan</c>, or an entry
    /// in the plan names a property that does not exist on the type.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A plan entry names a <see cref="Nullable{T}"/> property, or requests an encoding that has no
    /// codec for its property type (for example <c>DeltaVlq</c> on a <see langword="decimal"/>).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="rowGroupMaxRows"/> is not positive, or exceeds the read-side ceiling of
    /// 2²⁶ (67 108 864) rows per row group that every read path enforces.
    /// </exception>
    public CompressedColumnarSegmentFormat(
        int rowGroupMaxRows = 1 << 17,
        TimeSpan? rowGroupMaxDateRange = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowGroupMaxRows);
        // The reader refuses any row group above MaxDecodableRowsPerRowGroup as corrupt, so a
        // writer configured above it would acknowledge segments no read can ever decode.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowGroupMaxRows, (int)MaxDecodableRowsPerRowGroup);
        _rowGroupMaxRows = rowGroupMaxRows;
        _rowGroupMaxDateRange = rowGroupMaxDateRange ?? TimeSpan.FromDays(365);
        _columns = DiscoverColumns();
        _columnsByName = _columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        _dateColumnName = ResolveDateColumnName(_columns);
        _stringColumns = _columns
            .Where(c => c.Property.PropertyType == typeof(string))
            .Select(c => (c, HasBitmapIndex(c)))
            .ToArray();
    }

    /// <summary>
    /// Refuses a record this format could never write, so the refusal happens before the engine
    /// acknowledges it rather than at flush. The string codecs encode neither a null nor an unpaired
    /// surrogate (strict UTF-8), and a <c>[BitmapIndex]</c> string becomes a footer key whose length
    /// field is 16 bits wide; any of these would fail every flush of the record's segment.
    /// The other column codecs encode every value of their type (a ScaledIntegerBitPacked decimal
    /// column that cannot be packed is written raw), and the constructor has already refused any plan
    /// entry whose encoding cannot encode its property type, so no other column needs a check here.
    /// </summary>
    void IColumnarRecordAdmission<TRecord>.ValidateRecord(TRecord record)
    {
        foreach (var (column, bitmapIndexed) in _stringColumns)
        {
            if (column.Property.GetValue(record) is not string value)
            {
                throw new ArgumentException(
                    $"Columnar property '{column.Name}' is null. Columnar codecs do not encode null values; "
                    + "coerce to a non-null sentinel (e.g. string.Empty) before writing.",
                    nameof(record));
            }

            int byteCount;
            try
            {
                byteCount = s_strictUtf8.GetByteCount(value);
            }
            catch (EncoderFallbackException ex)
            {
                throw new ArgumentException(
                    $"Columnar property '{column.Name}' is not well-formed UTF-16: an unpaired surrogate cannot "
                    + "be encoded in UTF-8, so the record could never be flushed.",
                    nameof(record),
                    ex);
            }

            if (bitmapIndexed && byteCount > FooterWriter.MaxBitmapKeyBytes)
            {
                throw new ArgumentException(
                    $"Column '{column.Name}' carries a [BitmapIndex] value of {byteCount} UTF-8 bytes; the "
                    + $"segment footer caps a bitmap-index key at {FooterWriter.MaxBitmapKeyBytes} bytes, so the "
                    + "record could never be flushed. Shorten the value or remove [BitmapIndex] from the column.",
                    nameof(record));
            }
        }
    }

    // Strict, like the column codecs: the default encoder would count U+FFFD for an unpaired surrogate.
    private static readonly UTF8Encoding s_strictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static bool HasBitmapIndex(ColumnInfo column)
        => column.Property.GetCustomAttributes(typeof(BitmapIndexAttribute), false).Length > 0;

    private static string? ResolveDateColumnName(IReadOnlyList<ColumnInfo> columns)
    {
        var map = typeof(TRecord).GetInterfaceMap(typeof(IHasDate));
        var dateGetter = map.TargetMethods[0];
        foreach (var column in columns)
        {
            if (column.Property.GetMethod is { } getter && getter.MethodHandle == dateGetter.MethodHandle)
            {
                return column.Name;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task WriteAsync(
        Stream output,
        IReadOnlyList<TRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        // Header
        Span<byte> header = stackalloc byte[SegmentMagic.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], SegmentMagic.AsUInt32);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], SegmentMagic.CurrentFormatVersion);
        await output.WriteAsync(header.ToArray(), cancellationToken).ConfigureAwait(false);

        var rowGroups = PlanRowGroups(records);

        var descriptors = new ColumnDescriptor[_columns.Count];
        var typedColumnArrays = new Array[_columns.Count];
        Dictionary<string, IReadOnlyList<int>>? rowGroupByteOffsets = null;

        for (var c = 0; c < _columns.Count; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var column = _columns[c];
            var byteOffset = output.Position;
            var typedValues = BuildTypedValues(column, records, rowGroups);
            typedColumnArrays[c] = typedValues;

            byte[] encoded;
            if (IsNonRestartable(column.Encoding) && rowGroups.Count > 0)
            {
                var (blob, offsets) = EncodeColumnPerRowGroup(column, typedValues, rowGroups);
                encoded = blob;
                rowGroupByteOffsets ??= new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
                rowGroupByteOffsets[column.Name] = offsets;
            }
            else
            {
                encoded = EncodeColumnFromTypedValues(column, typedValues);
            }

            await output.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
            descriptors[c] = new ColumnDescriptor(
                column.Name,
                byteOffset,
                encoded.Length,
                column.EncodingTag,
                column.CodecVersion,
                MapTypeToCode(column.Property.PropertyType));
        }

        // per-row-group per-column zone-map statistics.
        var zoneMaps = ComputeZoneMaps(descriptors, typedColumnArrays, rowGroups);

        // cross-row-group bitmap indexes for [BitmapIndex] columns.
        var bitmapIndexes = ComputeBitmapIndexes(descriptors, typedColumnArrays);

        // per-row-group CRC32C for quarantine-aware reads.
        var rowGroupCrcs = ComputeRowGroupCrcs(descriptors, typedColumnArrays, rowGroups);

        var footer = new SegmentFooter(
            SegmentMagic.CurrentFormatVersion,
            descriptors,
            rowGroups,
            zoneMaps,
            bitmapIndexes,
            rowGroupCrcs,
            rowGroupByteOffsets);
        FooterWriter.Write(output, footer);
    }

    /// <summary>
    /// Reads just the per-(row-group, column) zone-map statistics from a columnar segment,
    /// without decoding any column data. Returns <see langword="null"/> if the segment was
    /// written without zone maps (no <c>ZoneMaps</c> extension in the footer).
    /// </summary>
    /// <param name="input">A readable, seekable stream positioned anywhere; caller owns lifecycle.</param>
    /// <returns>
    /// <c>[rowGroupIndex][columnIndex]</c>-indexed stats or <see langword="null"/>. The column
    /// order matches the order the segment was written, which is the order returned by the
    /// generator-emitted <c>ColumnarFieldPlan</c>.
    /// </returns>
    public IReadOnlyList<IReadOnlyList<ColumnRowGroupStats>>? ReadZoneMaps(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return FooterReader.Read(input).ZoneMaps;
    }

    /// <summary>
    /// Reads just the cross-row-group bitmap indexes from a columnar segment, without decoding
    /// any column data. Returns <see langword="null"/> if the segment was written without
    /// bitmap indexes.
    /// </summary>
    /// <param name="input">A readable, seekable stream positioned anywhere; caller owns lifecycle.</param>
    /// <returns>One entry per <c>[BitmapIndex]</c>-annotated column present at write time.</returns>
    public IReadOnlyList<BitmapIndexEntry>? ReadBitmapIndexes(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return FooterReader.Read(input).BitmapIndexes;
    }

    /// <summary>
    /// Reads records row-group-by-row-group with integrity validation. For each row group
    /// that intersects <c>[<paramref name="from"/>, <paramref name="to"/>]</c>, yields a
    /// <see cref="RowGroupReadResult{TRecord}"/>: <c>Ok</c> with the decoded rows when the
    /// per-row-group CRC32C matches, otherwise <c>Quarantined</c> carrying both CRCs so the
    /// caller can decide whether to fail, skip, or re-read from a replica. Segments written
    /// without per-row-group CRCs (no <c>RowGroupCrcs</c> footer extension) always yield
    /// <c>Ok</c>.
    /// </summary>
    /// <remarks>
    /// Symmetric with <see cref="ReadAsync"/> and <see cref="ReadBatchesAsync"/>: accepts a
    /// <see cref="ScanDirection"/> for forward extensibility (P2.12). v2.0.0 ships only
    /// <see cref="ScanDirection.Ascending"/>; any other value is rejected with
    /// <see cref="ArgumentOutOfRangeException"/> so descending scans cannot silently fall
    /// back to ascending iteration when they land in a future release.
    /// </remarks>
    public async IAsyncEnumerable<RowGroupReadResult<TRecord>> ReadRowGroupsAsync(
        Stream input,
        DateOnly from,
        DateOnly to,
        ScanDirection direction = ScanDirection.Ascending,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (direction != ScanDirection.Ascending)
        {
            throw new ArgumentOutOfRangeException(nameof(direction),
                $"ScanDirection {direction} is not supported in v2.0.0; only Ascending is implemented.");
        }
        if (from > to)
        {
            yield break;
        }

        var footer = FooterReader.Read(input);
        EnsureUniqueColumnNames(footer);

        // Every column is decoded — required so we can compute both (a) the row-group canonical
        // bytes for CRC validation and (b) the decoded rows to return on Ok. Each column's bytes
        // are read once, but decoding happens one row group at a time: a whole-column decode sizes
        // its array by the footer's summed row count, which a forged footer inflates by declaring
        // more row groups, whereas a single row group's decode is bounded by the per-row-group cap.
        var descriptors = footer.Columns;
        foreach (var desc in descriptors)
        {
            ValidateEncodingSupport(desc);
        }

        var decodeColumns = new ColumnInfo[descriptors.Count];
        var columnBytes = new byte[descriptors.Count][];
        for (var c = 0; c < descriptors.Count; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var desc = descriptors[c];
            if (!_columnsByName.TryGetValue(desc.Name, out var columnInfo))
            {
                // The footer declares a column that this format instance does not recognise —
                // this indicates a schema mismatch (e.g. segment written by a newer version
                // of the type). Fail with a typed exception naming the available columns so
                // the caller can diagnose the mismatch without opening the segment file.
                throw new ColumnNotFoundException(
                    desc.Name,
                    footer.FormatVersion,
                    descriptors.Select(d => d.Name).ToList());
            }

            decodeColumns[c] = ResolveDecodeColumn(columnInfo, desc);
            ValidateColumnExtent(desc, input.Length);
            var bytes = new byte[desc.ByteLength];
            input.Seek(desc.ByteOffset, SeekOrigin.Begin);
            ReadExactly(input, bytes);
            columnBytes[c] = bytes;
        }

        for (var rgIndex = 0; rgIndex < footer.RowGroups.Count; rgIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rg = footer.RowGroups[rgIndex];
            if (rg.MaxDate < from || rg.MinDate > to)
            {
                continue;
            }

            var decoded = new Array[descriptors.Count];
            for (var c = 0; c < descriptors.Count; c++)
            {
                var desc = descriptors[c];
                try
                {
                    decoded[c] = DecodeRowGroupSlice(decodeColumns[c], desc, columnBytes[c], footer, rgIndex, boundSliceRowCount: true);
                }
                catch (Exception ex) when (ex is not ColumnDecodeException
                    and not OperationCanceledException
                    and not CorruptSegmentException
                    and not TruncatedSegmentException)
                {
                    throw new ColumnDecodeException(
                        desc.Name, rgIndex, desc.ByteOffset,
                        $"Codec {desc.EncodingTag} (v{desc.CodecVersion}) failed to decode column '{desc.Name}' in row group {rgIndex}: {ex.Message}",
                        ex);
                }
            }

            // Validate per-row-group CRC if present in the footer. The arrays hold this row group
            // only, so the canonical bytes are built at row offset 0.
            if (footer.RowGroupCrcs is { } crcs && rgIndex < crcs.Count)
            {
                EnsureDeclaredFieldTypes(descriptors, decoded);
                var canonical = RowGroupCanonicalBytes.Build(descriptors, decoded, rg with { FirstRowIndex = 0 });
                var actual = Crc32C.HashToUInt32(canonical);
                var expected = crcs[rgIndex];
                if (actual != expected)
                {
                    yield return RowGroupReadResult<TRecord>.Quarantined(rgIndex, expected, actual);
                    continue;
                }
            }

            var namedDecoded = new Dictionary<string, Array>(descriptors.Count, StringComparer.Ordinal);
            for (var c = 0; c < descriptors.Count; c++)
            {
                namedDecoded[descriptors[c].Name] = decoded[c];
            }

            var rows = new List<TRecord>(rg.RowCount);
            for (var i = 0; i < rg.RowCount; i++)
            {
                var record = TRecord.Rehydrate(namedDecoded, i);
                if (record.Date < from || record.Date > to)
                {
                    continue;
                }
                rows.Add(record);
            }

            yield return RowGroupReadResult<TRecord>.Ok(rgIndex, rows);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ColumnBatch<TRecord>> ReadBatchesAsync(
        Stream input,
        DateOnly from,
        DateOnly to,
        ColumnProjection projection,
        ScanDirection direction = ScanDirection.Ascending,
        CancellationToken cancellationToken = default)
        => ReadBatchesCoreAsync(input, from, to, projection, predicates: null, decodeEveryColumn: false, direction, cancellationToken);

    // Shared row-group iteration for ReadBatchesAsync and the predicate-pushdown StreamWhereAsync.
    // When predicates are supplied, a row group whose zone maps / bitmap indexes prove it cannot
    // contain a match is skipped before any column bytes are decoded (DDIA 2e Ch. 4 data skipping;
    // finding #8). The decode cost of a skipped row group is fully avoided.
    private async IAsyncEnumerable<ColumnBatch<TRecord>> ReadBatchesCoreAsync(
        Stream input,
        DateOnly from,
        DateOnly to,
        ColumnProjection projection,
        IReadOnlyList<ColumnPredicate>? predicates,
        bool decodeEveryColumn,
        ScanDirection direction,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (direction != ScanDirection.Ascending)
        {
            // v2.0.0 ships only ScanDirection.Ascending. Adding Descending later is an
            // additive enum-member change (post-GA scope); reject any other value at the
            // runtime boundary so a future enum extension cannot silently no-op the request.
            throw new ArgumentOutOfRangeException(nameof(direction),
                $"ScanDirection {direction} is not supported in v2.0.0; only Ascending is implemented.");
        }
        if (from > to)
        {
            yield break;
        }

        var footer = FooterReader.Read(input);
        EnsureUniqueColumnNames(footer);
        var (descriptorsToProject, _) = ResolveProjection(projection, footer);
        if (decodeEveryColumn)
        {
            // The caller's names were validated above; the date filter needs a column this format
            // cannot name, so every on-disk column is decoded.
            descriptorsToProject = footer.Columns;
        }

        // A predicate is evaluated against a decoded column. One naming a column the segment does
        // not declare is refused under the same strictness as a projected name — including under
        // ColumnProjection.All, whose resolution above never sees the predicate names.
        if (predicates is { Count: > 0 } && !projection.AllowMissing)
        {
            foreach (var predicate in predicates)
            {
                if (!footer.Columns.Any(c => string.Equals(c.Name, predicate.ColumnName, StringComparison.Ordinal)))
                {
                    throw new ColumnNotFoundException(
                        predicate.ColumnName,
                        footer.FormatVersion,
                        footer.Columns.Select(c => c.Name).ToArray());
                }
            }
        }

        // The per-row-group CRC32C is computed over every column's values, so it can be checked
        // whenever this read decodes all of them — point reads, full scans and compaction all do.
        // A strict column subset cannot evaluate it without decoding the excluded columns, which
        // would undo the projection's IO skip; such reads are not CRC-verified.
        var verifyRowGroupCrcs = footer.RowGroupCrcs is not null
            && ProjectsEveryColumn(descriptorsToProject, footer.Columns);

        foreach (var desc in descriptorsToProject)
        {
            ValidateEncodingSupport(desc);
        }

        // Pre-load each projected column's raw bytes exactly once (P3.2: one seek+read per
        // column instead of one per row-group × column).
        var columnData = new Dictionary<string, byte[]>(descriptorsToProject.Count, StringComparer.Ordinal);
        foreach (var desc in descriptorsToProject)
        {
            ValidateColumnExtent(desc, input.Length);
            var bytes = new byte[desc.ByteLength];
            input.Seek(desc.ByteOffset, SeekOrigin.Begin);
            ReadExactly(input, bytes);
            columnData[desc.Name] = bytes;
        }

        for (var rgIndex = 0; rgIndex < footer.RowGroups.Count; rgIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rg = footer.RowGroups[rgIndex];
            if (rg.MaxDate < from || rg.MinDate > to)
            {
                continue;
            }

            // Value-predicate row-group skipping (finding #8): if the row group's zone maps /
            // bitmap indexes prove it cannot match the conjunction, skip it before decoding any
            // column bytes. Correctness is preserved by the row-level filter in StreamWhereAsync.
            if (predicates is { Count: > 0 } && CanSkipRowGroup(footer, rgIndex, predicates))
            {
                continue;
            }

            var columns = new Dictionary<string, Array>(descriptorsToProject.Count, StringComparer.Ordinal);
            foreach (var desc in descriptorsToProject)
            {
                if (!_columnsByName.TryGetValue(desc.Name, out var columnInfo))
                {
                    // The footer declares a column that this format instance does not
                    // recognise — schema mismatch. Throw a typed exception so the caller
                    // can diagnose without opening the segment file. Silently skipping the
                    // column would produce incomplete batches with no signal.
                    throw new ColumnNotFoundException(
                        desc.Name,
                        footer.FormatVersion,
                        descriptorsToProject.Select(d => d.Name).ToList());
                }

                var decodeColumn = ResolveDecodeColumn(columnInfo, desc);
                Array slice;
                try
                {
                    slice = DecodeRowGroupSlice(decodeColumn, desc, columnData[desc.Name], footer, rgIndex);
                }
                catch (Exception ex) when (ex is not ColumnDecodeException and not OperationCanceledException)
                {
                    throw new ColumnDecodeException(
                        desc.Name,
                        rgIndex,
                        desc.ByteOffset,
                        $"Codec {desc.EncodingTag} (v{desc.CodecVersion}) failed to decode column '{desc.Name}' in row group {rgIndex}: {ex.Message}",
                        ex);
                }

                columns[desc.Name] = slice;
            }

            if (verifyRowGroupCrcs && rgIndex < footer.RowGroupCrcs!.Count)
            {
                VerifyRowGroupCrc(footer, rgIndex, columns);
            }

            yield return new ColumnBatch<TRecord>(rg.RowCount, columns, rg.MinDate, rg.MaxDate);
        }
    }

    private static bool ProjectsEveryColumn(
        IReadOnlyList<ColumnDescriptor> projected, IReadOnlyList<ColumnDescriptor> all)
    {
        var projectedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var desc in projected)
        {
            projectedNames.Add(desc.Name);
        }

        foreach (var desc in all)
        {
            if (!projectedNames.Contains(desc.Name))
            {
                return false;
            }
        }

        return true;
    }

    // Fail closed: a row group whose decoded values no longer hash to the CRC the writer stamped is
    // corrupt, and serving it would hand the caller (and compaction, which rewrites what it reads
    // under a fresh CRC) silently wrong values. The columns are this row group's slices, so the
    // canonical bytes are built at row offset 0, in footer column order as the writer built them.
    private static void VerifyRowGroupCrc(
        SegmentFooter footer, int rgIndex, IReadOnlyDictionary<string, Array> rowGroupColumns)
    {
        var ordered = new Array[footer.Columns.Count];
        for (var c = 0; c < footer.Columns.Count; c++)
        {
            ordered[c] = rowGroupColumns[footer.Columns[c].Name];
        }

        EnsureDeclaredFieldTypes(footer.Columns, ordered);
        var rg = footer.RowGroups[rgIndex];
        var canonical = RowGroupCanonicalBytes.Build(footer.Columns, ordered, rg with { FirstRowIndex = 0 });
        var actual = Crc32C.HashToUInt32(canonical);
        var expected = footer.RowGroupCrcs![rgIndex];
        if (actual != expected)
        {
            throw new CorruptSegmentException(
                $"Columnar segment row group {rgIndex} failed its CRC32C check: the footer declares "
                + $"0x{expected:X8} but the decoded values hash to 0x{actual:X8}; segment is corrupt.",
                offset: 0);
        }
    }

    // The canonical CRC bytes are built by each column's footer-declared field type. A CRC-valid
    // forged footer that misdeclares one would otherwise surface a raw cast failure from the
    // hashing step instead of the typed corruption signal the rest of the reader raises.
    private static void EnsureDeclaredFieldTypes(IReadOnlyList<ColumnDescriptor> descriptors, Array[] decoded)
    {
        for (var c = 0; c < descriptors.Count; c++)
        {
            var elementType = decoded[c].GetType().GetElementType()!;
            if (MapTypeToCode(elementType) != descriptors[c].FieldType)
            {
                throw new CorruptSegmentException(
                    $"Column '{descriptors[c].Name}' is declared as {descriptors[c].FieldType} in the footer "
                    + $"but decodes as {elementType.Name}; segment is corrupt.",
                    descriptors[c].ByteOffset);
            }
        }
    }

    /// <inheritdoc cref="IColumnarSegmentFormat{TRecord}.StreamWhereAsync"/>
    /// <remarks>
    /// Rows are yielded in stored order (row group by row group, row by row), which is date-ascending
    /// only when the segment was written from date-ascending records; this method does not sort.
    /// </remarks>
    public async IAsyncEnumerable<TRecord> StreamWhereAsync(
        Stream input,
        DateOnly from,
        DateOnly to,
        ColumnProjection projection,
        IReadOnlyList<ColumnPredicate> predicates,
        ScanDirection direction = ScanDirection.Ascending,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(predicates);

        if (predicates.Count == 0)
        {
            // No predicate — degenerate to a plain range read.
            await foreach (var r in ReadAsync(input, from, to, projection, direction, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return r;
            }
            yield break;
        }

        if (from > to)
        {
            yield break;
        }

        // The predicate columns must be decoded so the row-level filter can evaluate them, even if
        // the caller did not project them. The returned records therefore materialise the
        // projection's columns plus any predicate columns.
        var augmented = AugmentProjectionWithPredicateColumns(EnsureDateProjected(projection), predicates);

        await foreach (var batch in ReadBatchesCoreAsync(input, from, to, augmented, predicates, DecodesEveryColumn(projection), direction, cancellationToken)
            .ConfigureAwait(false))
        {
            for (var i = 0; i < batch.RowCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = TRecord.Rehydrate(batch.Columns, i);
                if (record.Date < from || record.Date > to)
                {
                    continue;
                }

                // Exact row-level filter on the decoded column cells — guarantees correct results
                // regardless of the row-group skip granularity (a surviving row group may still
                // contain non-matching rows).
                var matched = true;
                foreach (var predicate in predicates)
                {
                    if (!batch.Columns.TryGetValue(predicate.ColumnName, out var column)
                        || !predicate.Matches(column.GetValue(i)))
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    yield return record;
                }
            }
        }
    }

    /// <summary>
    /// Decides whether row group <paramref name="rgIndex"/> can be skipped entirely for the
    /// conjunction <paramref name="predicates"/> using only footer metadata (zone maps + bitmap
    /// indexes), without decoding any column bytes. Because the predicates are AND-combined, the
    /// row group is skippable as soon as any single predicate proves it disjoint.
    /// </summary>
    internal static bool CanSkipRowGroup(
        SegmentFooter footer, int rgIndex, IReadOnlyList<ColumnPredicate> predicates)
    {
        foreach (var predicate in predicates)
        {
            var colIndex = -1;
            for (var c = 0; c < footer.Columns.Count; c++)
            {
                if (string.Equals(footer.Columns[c].Name, predicate.ColumnName, StringComparison.Ordinal))
                {
                    colIndex = c;
                    break;
                }
            }
            if (colIndex < 0)
            {
                continue; // predicate references an unknown column — handled at the row level.
            }
            var fieldType = footer.Columns[colIndex].FieldType;

            // Zone-map skip: row group's [min, max] is provably disjoint from the predicate.
            if (footer.ZoneMaps is { } zoneMaps
                && rgIndex < zoneMaps.Count
                && colIndex < zoneMaps[rgIndex].Count
                && ColumnarPredicatePushdown.CanSkipByZoneMap(
                    zoneMaps[rgIndex][colIndex], predicate.Operator, predicate.Value, fieldType))
            {
                return true;
            }

            // Bitmap-index skip for equality: the value never appears in the segment, or appears
            // nowhere in this row group's row range.
            if (predicate.Operator == ComparisonOperator.Equal && footer.BitmapIndexes is { } bitmaps)
            {
                BitmapIndexEntry? entry = null;
                foreach (var candidate in bitmaps)
                {
                    if (string.Equals(candidate.ColumnName, predicate.ColumnName, StringComparison.Ordinal))
                    {
                        entry = candidate;
                        break;
                    }
                }
                if (entry is not null)
                {
                    var key = ColumnarPredicatePushdown.EncodeValue(predicate.Value, fieldType);
                    if (key is not null)
                    {
                        BitmapIndexValue? match = null;
                        foreach (var value in entry.ValueToBitmap)
                        {
                            if (value.Value.AsSpan().SequenceEqual(key))
                            {
                                match = value;
                                break;
                            }
                        }
                        if (match is null)
                        {
                            return true; // value absent from the entire segment.
                        }
                        var rg = footer.RowGroups[rgIndex];
                        // Thread the per-row-group allocation ceiling (F-W2-01): a forged footer
                        // rg.RowCount near int.MaxValue must not drive a multi-GB new bool[count]
                        // on this public read path. The bitmap is a saturating run-length payload,
                        // so its per-payload ceiling is vacuous — the per-row-group cap is the only
                        // bound that discriminates. A valid rg.RowCount (<= rowGroupMaxRows) passes
                        // regardless of rg.FirstRowIndex, because the bound caps `count`, not the
                        // absolute extent.
                        // This runs OUTSIDE the per-column decode try/catch, so a bitmap payload that
                        // cannot cover the row group's range must be converted here, like the zone-map
                        // bound check, instead of leaking the codec's raw exception from the query API.
                        bool[] slice;
                        try
                        {
                            slice = BitmapRleCodec.DecodeSlice(
                                match.Bitmap, rg.FirstRowIndex, rg.RowCount, MaxDecodableRowsPerRowGroup);
                        }
                        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                        {
                            throw new CorruptSegmentException(
                                $"Bitmap index for column '{predicate.ColumnName}' cannot cover row group {rgIndex} "
                                + $"(rows {rg.FirstRowIndex} to {(long)rg.FirstRowIndex + rg.RowCount - 1}): {ex.Message}",
                                offset: 0,
                                ex);
                        }
                        var anySet = false;
                        foreach (var bit in slice)
                        {
                            if (bit)
                            {
                                anySet = true;
                                break;
                            }
                        }
                        if (!anySet)
                        {
                            return true; // value absent from this row group.
                        }
                    }
                }
            }
        }

        return false;
    }

    // ReadAsync and StreamWhereAsync filter rehydrated rows by record.Date. A projection that omits
    // the column backing IHasDate.Date would rehydrate every row at default(DateOnly) and silently
    // drop them all, so that column is always decoded and materialised alongside the caller's
    // fields. When no column's getter implements IHasDate.Date, the date is computed from columns
    // this format cannot identify, so the read decodes every column instead (DecodesEveryColumn).
    private ColumnProjection EnsureDateProjected(ColumnProjection projection)
    {
        if (projection.FieldNames.Count == 0
            || _dateColumnName is null
            || projection.FieldNames.Contains(_dateColumnName, StringComparer.Ordinal))
        {
            return projection;
        }

        return new ColumnProjection([.. projection.FieldNames, _dateColumnName], projection.AllowMissing);
    }

    private bool DecodesEveryColumn(ColumnProjection projection)
        => _dateColumnName is null && projection.FieldNames.Count > 0;

    private static ColumnProjection AugmentProjectionWithPredicateColumns(
        ColumnProjection projection, IReadOnlyList<ColumnPredicate> predicates)
    {
        // An empty FieldNames list is ColumnProjection.All — every column is already decoded.
        if (projection.FieldNames.Count == 0)
        {
            return projection;
        }

        var fields = new List<string>(projection.FieldNames);
        foreach (var predicate in predicates)
        {
            if (!fields.Contains(predicate.ColumnName))
            {
                fields.Add(predicate.ColumnName);
            }
        }
        return new ColumnProjection(fields, projection.AllowMissing);
    }

    /// <inheritdoc cref="IColumnarSegmentFormat{TRecord}.ReadAsync"/>
    /// <remarks>
    /// Rows are yielded in stored order (row group by row group, row by row), which is date-ascending
    /// only when the segment was written from date-ascending records; this method does not sort.
    /// </remarks>
    public async IAsyncEnumerable<TRecord> ReadAsync(
        Stream input,
        DateOnly from,
        DateOnly to,
        ColumnProjection projection,
        ScanDirection direction = ScanDirection.Ascending,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (direction != ScanDirection.Ascending)
        {
            throw new ArgumentOutOfRangeException(nameof(direction),
                $"ScanDirection {direction} is not supported in v2.0.0; only Ascending is implemented.");
        }
        if (from > to)
        {
            yield break;
        }

        await foreach (var batch in ReadBatchesCoreAsync(input, from, to, EnsureDateProjected(projection), predicates: null, DecodesEveryColumn(projection), direction, cancellationToken)
            .ConfigureAwait(false))
        {
            for (var i = 0; i < batch.RowCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = TRecord.Rehydrate(batch.Columns, i);

                if (record.Date < from || record.Date > to)
                {
                    continue;
                }

                yield return record;
            }
        }
    }

    // ───── Column discovery (from generator-emitted plan) ─────────────

    private sealed record ColumnInfo(
        string Name,
        PropertyInfo Property,
        ColumnEncoding Encoding,
        ColumnEncodingTag EncodingTag,
        byte CodecVersion);

    private static IReadOnlyList<ColumnInfo> DiscoverColumns()
    {
        var planProp = typeof(TRecord).GetProperty(
            PlanPropertyName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy) ?? throw new InvalidOperationException(
                $"Type '{typeof(TRecord).FullName}' does not expose a static '{PlanPropertyName}' property. "
                + "Apply [ColumnarStorable] and rebuild so the source generator can emit the partial declaration.");
        if (planProp.GetValue(null) is not IReadOnlyList<ColumnarFieldDescriptor> plan)
        {
            throw new InvalidOperationException(
                $"Type '{typeof(TRecord).FullName}'.{PlanPropertyName} did not return an IReadOnlyList<ColumnarFieldDescriptor>.");
        }

        var columns = new List<ColumnInfo>(plan.Count);
        foreach (var descriptor in plan)
        {
            var prop = typeof(TRecord).GetProperty(descriptor.Name,
                BindingFlags.Public | BindingFlags.Instance);
            if (prop is null || !prop.CanRead)
            {
                throw new InvalidOperationException(
                    $"ColumnarFieldPlan on '{typeof(TRecord).FullName}' names property '{descriptor.Name}' "
                    + "but the type has no matching public readable property. Regenerate source.");
            }

            // Reject Nullable<T> column property types at construction time. The codec layer
            // does not encode null payloads (BuildTypedValues' rejectNulls catch fires per-write,
            // and MapTypeToCode also rejects Nullable<T> at descriptor-build time AFTER bytes
            // have already been written for prior columns) — hoisting the check here means a
            // misconfigured record fails fast at engine construction rather than partway
            // through a multi-column write. Reference types (e.g., string) carry their own
            // null-rejection at write time because null is a value-level signal there, not a
            // property-shape signal.
            var propType = prop.PropertyType;
            if (propType.IsGenericType && propType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                throw new NotSupportedException(
                    $"Column '{descriptor.Name}' on '{typeof(TRecord).FullName}' has type "
                    + $"'{propType.FullName}'. Nullable<T> column properties are not supported in "
                    + "v2.0.0 — columnar codecs do not encode null payloads. Drop the Nullable "
                    + "wrapper and coerce nulls to a non-null sentinel at the writer boundary.");
            }

            // A hand-written plan (the format accepts any record exposing ColumnarFieldPlan, not only
            // generator-emitted ones) can pair an encoding with a property type no codec encodes. The
            // writer would only discover that at flush, after the engine had acknowledged the records,
            // so every flush of their segment would fail; the pairing is refused here instead, against
            // the same matrix the generator applies at compile time and the codec dispatch implements.
            if (!IsSupportedPairing(descriptor.Encoding, propType))
            {
                throw new NotSupportedException(
                    $"Column '{descriptor.Name}' on '{typeof(TRecord).FullName}' requests encoding "
                    + $"{descriptor.Encoding} for type {propType.FullName}, which has no codec pairing. "
                    + "Choose an encoding that supports the property type (the [ColumnarStorable] generator "
                    + "reports the supported pairs).");
            }

            var (tag, version) = MapEncodingToTag(descriptor.Encoding);
            columns.Add(new ColumnInfo(descriptor.Name, prop, descriptor.Encoding, tag, version));
        }

        return columns;
    }

    // The (encoding, property type) pairs EncodeColumnFromTypedValues and DecodeColumnSlice both
    // implement; each pair's type also has a field-type code in MapTypeToCode.
    private static bool IsSupportedPairing(ColumnEncoding encoding, Type type) => encoding switch
    {
        ColumnEncoding.Dictionary => type == typeof(string) || type == typeof(decimal),
        ColumnEncoding.BitmapRle => type == typeof(bool),
        ColumnEncoding.DeltaVlq => type == typeof(int) || type == typeof(long) || type.IsEnum,
        ColumnEncoding.DeltaOfDeltaVlq => type == typeof(DateOnly),
        ColumnEncoding.GorillaXor => type == typeof(double) || type == typeof(float),
        ColumnEncoding.RawDouble => type == typeof(double),
        ColumnEncoding.ScaledIntegerBitPacked => type == typeof(decimal),
        _ => false,
    };

    private static (ColumnEncodingTag Tag, byte Version) MapEncodingToTag(ColumnEncoding encoding) => encoding switch
    {
        ColumnEncoding.Dictionary => (ColumnEncodingTag.Dictionary, DictionaryCodec.Version),
        ColumnEncoding.BitmapRle => (ColumnEncodingTag.BitmapRle, BitmapRleCodec.Version),
        ColumnEncoding.DeltaVlq => (ColumnEncodingTag.DeltaVlq, DeltaVlqCodec.Version),
        ColumnEncoding.DeltaOfDeltaVlq => (ColumnEncodingTag.DeltaOfDeltaVlq, DeltaOfDeltaVlqCodec.Version),
        ColumnEncoding.GorillaXor => (ColumnEncodingTag.GorillaXor, GorillaXorCodec.Version),
        ColumnEncoding.RawDouble => (ColumnEncodingTag.PlainRawBytes, 0),
        ColumnEncoding.ScaledIntegerBitPacked => (ColumnEncodingTag.ScaledIntegerBitPacked, ScaledIntegerBitPackedCodec.Version),
        _ => throw new NotSupportedException($"Unsupported ColumnEncoding value {encoding}."),
    };

    private static ColumnFieldTypeCode MapTypeToCode(Type t) => t switch
    {
        not null when t == typeof(DateOnly) => ColumnFieldTypeCode.DateOnly,
        not null when t == typeof(int) => ColumnFieldTypeCode.Int32,
        not null when t == typeof(long) => ColumnFieldTypeCode.Int64,
        not null when t == typeof(decimal) => ColumnFieldTypeCode.Decimal,
        not null when t == typeof(double) => ColumnFieldTypeCode.Double,
        not null when t == typeof(float) => ColumnFieldTypeCode.Single,
        not null when t == typeof(bool) => ColumnFieldTypeCode.Boolean,
        not null when t == typeof(string) => ColumnFieldTypeCode.String,
        not null when t.IsEnum => ColumnFieldTypeCode.Enum,
        _ => throw new NotSupportedException($"Type {t?.FullName ?? "<null>"} has no columnar field-type code."),
    };

    // ───── Row-group planning (identical to Phase 2) ──────────────────

    // The footer describes at most FooterWriter.MaxRowGroups row groups, and the block reaching this
    // writer was already acknowledged, so a block the configured knobs would split further is planned
    // with wider groups instead of being refused: each group then holds at least
    // ceil(rows / MaxRowGroups) rows, a date-range break only cuts a group that already holds that
    // many, and the knobs remain upper bounds for every block the footer can describe as configured.
    private IReadOnlyList<RowGroupDescriptor> PlanRowGroups(IReadOnlyList<TRecord> records)
    {
        var groups = PlanRowGroups(records, _rowGroupMaxRows, minRowsBeforeDateCut: 1);
        if (groups.Count <= FooterWriter.MaxRowGroups)
        {
            return groups;
        }

        var minRows = (int)(((long)records.Count + FooterWriter.MaxRowGroups - 1) / FooterWriter.MaxRowGroups);
        return PlanRowGroups(records, Math.Max(_rowGroupMaxRows, minRows), minRows);
    }

    private IReadOnlyList<RowGroupDescriptor> PlanRowGroups(
        IReadOnlyList<TRecord> records, int maxRows, int minRowsBeforeDateCut)
    {
        var groups = new List<RowGroupDescriptor>();
        if (records.Count == 0)
        {
            return groups;
        }

        var start = 0;
        while (start < records.Count)
        {
            // Row-group planning is order-independent: callers may sort the records by a
            // non-date column (ColumnSortSpec permits any sort key), so the input array is NOT
            // guaranteed date-ordered. We therefore track the group's running min/max date and
            // (a) cut the group once admitting the next row would make the group's actual date
            // span exceed _rowGroupMaxDateRange, and (b) record the true min/max — never the
            // positional first/last record's date. A positional span would be wrong (frequently
            // inverted, MinDate > MaxDate) under a non-date sort, and the read-path date filter
            // (rg.MaxDate < from || rg.MinDate > to) would then silently skip groups that
            // contain matching dates.
            var minDate = records[start].Date;
            var maxDate = records[start].Date;
            var end = start + 1;
            while (end < records.Count
                   && end - start < maxRows)
            {
                var candidate = records[end].Date;
                var candidateMin = candidate < minDate ? candidate : minDate;
                var candidateMax = candidate > maxDate ? candidate : maxDate;
                if (candidateMax.DayNumber - candidateMin.DayNumber > _rowGroupMaxDateRange.Days
                    && end - start >= minRowsBeforeDateCut)
                {
                    break;
                }

                minDate = candidateMin;
                maxDate = candidateMax;
                end++;
            }

            var count = end - start;
            groups.Add(new RowGroupDescriptor(
                MinDate: minDate,
                MaxDate: maxDate,
                RowCount: count,
                FirstRowIndex: start));
            start = end;
        }

        return groups;
    }

    // ───── Projection resolution ──────────────────────────────────────

    private static (IReadOnlyList<ColumnDescriptor> Projected, IReadOnlyList<string> Missing)
        ResolveProjection(ColumnProjection projection, SegmentFooter footer)
    {
        if (projection.FieldNames.Count == 0)
        {
            return (footer.Columns, Array.Empty<string>());
        }

        var available = footer.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var projected = new List<ColumnDescriptor>();
        var missing = new List<string>();
        foreach (var name in projection.FieldNames)
        {
            if (available.TryGetValue(name, out var desc))
            {
                projected.Add(desc);
            }
            else if (projection.AllowMissing)
            {
                missing.Add(name);
            }
            else
            {
                throw new ColumnNotFoundException(
                    name,
                    footer.FormatVersion,
                    footer.Columns.Select(c => c.Name).ToArray());
            }
        }

        return (projected, missing);
    }

    // ───── Footer-driven decode resolution ────────────

    // Every read path keys columns by name. A CRC-valid forged footer that lists a name twice would
    // otherwise surface a raw ArgumentException from a dictionary build, or silently let one
    // descriptor shadow the other.
    private static void EnsureUniqueColumnNames(SegmentFooter footer)
    {
        var seen = new HashSet<string>(footer.Columns.Count, StringComparer.Ordinal);
        foreach (var desc in footer.Columns)
        {
            if (!seen.Add(desc.Name))
            {
                throw new CorruptSegmentException(
                    $"Footer lists column '{desc.Name}' more than once; segment is corrupt.",
                    desc.ByteOffset);
            }
        }
    }

    // The footer records which codec wrote a column and which field type it encoded. Decoding
    // follows the footer, not TRecord's current [ColumnEncoding] annotation: a reader whose
    // annotation (or the generator's default) differs from the writer's would otherwise run the
    // wrong codec over intact bytes and, on a projected read that skips the row-group CRC, return
    // unrelated values without any error. The record's property type still fixes the CLR shape, so a
    // footer field type that disagrees with it cannot be decoded faithfully and is rejected.
    private static ColumnInfo ResolveDecodeColumn(ColumnInfo column, ColumnDescriptor desc)
    {
        var propertyCode = MapTypeToCode(column.Property.PropertyType);
        if (propertyCode != desc.FieldType)
        {
            throw new CorruptSegmentException(
                $"Column '{desc.Name}' is declared as {desc.FieldType} in the footer but the reading record "
                + $"declares it as {propertyCode}; segment is corrupt.",
                desc.ByteOffset);
        }

        var encoding = MapTagToEncoding(desc);
        return encoding == column.Encoding
            ? column
            : column with { Encoding = encoding, EncodingTag = desc.EncodingTag, CodecVersion = desc.CodecVersion };
    }

    private static ColumnEncoding MapTagToEncoding(ColumnDescriptor desc) => desc.EncodingTag switch
    {
        ColumnEncodingTag.Dictionary => ColumnEncoding.Dictionary,
        ColumnEncodingTag.BitmapRle => ColumnEncoding.BitmapRle,
        ColumnEncodingTag.DeltaVlq => ColumnEncoding.DeltaVlq,
        ColumnEncodingTag.DeltaOfDeltaVlq => ColumnEncoding.DeltaOfDeltaVlq,
        ColumnEncodingTag.GorillaXor => ColumnEncoding.GorillaXor,
        ColumnEncodingTag.PlainRawBytes => ColumnEncoding.RawDouble,
        ColumnEncodingTag.ScaledIntegerBitPacked => ColumnEncoding.ScaledIntegerBitPacked,
        _ => throw new UnsupportedEncodingException(
            desc.Name,
            (byte)desc.EncodingTag,
            desc.CodecVersion,
            $"Column '{desc.Name}' references unknown encoding tag 0x{(byte)desc.EncodingTag:X2}."),
    };

    // Decodes one row group of one column. Non-restartable codecs were written per row group, so
    // the footer's byte offsets select this row group's slice; restartable codecs seek within the
    // whole-column encoding. Either way the decode count is this row group's row count, so the
    // allocation ceiling is the per-row-group cap — NOT the segment byte length, which a run-length
    // codec under-counts and would false-reject a valid column (F-W3-regress-01).
    private static Array DecodeRowGroupSlice(
        ColumnInfo column, ColumnDescriptor desc, byte[] allBytes, SegmentFooter footer, int rgIndex, bool boundSliceRowCount = false)
    {
        var rg = footer.RowGroups[rgIndex];
        if (footer.RowGroupByteOffsets?.TryGetValue(desc.Name, out var rgOffsets) == true)
        {
            var start = rgOffsets[rgIndex];
            var end = (rgIndex + 1 < rgOffsets.Count) ? rgOffsets[rgIndex + 1] : allBytes.Length;
            if (boundSliceRowCount)
            {
                ValidateRowCountAgainstPayload(desc.Name, rg.RowCount, end - start);
            }

            return DecodeColumnSlice(column, allBytes.AsSpan(start, end - start), 0, rg.RowCount, MaxDecodableRowsPerRowGroup);
        }

        // The writer always records per-row-group offsets for a non-restartable column that has rows;
        // without them its codec state cannot be resumed at rg.FirstRowIndex.
        if (IsNonRestartable(column.Encoding))
        {
            throw new CorruptSegmentException(
                $"Column '{desc.Name}' uses non-restartable encoding {column.Encoding} but the footer carries "
                + "no per-row-group byte offsets for it; segment is corrupt.",
                desc.ByteOffset);
        }

        return DecodeColumnSlice(column, allBytes, rg.FirstRowIndex, rg.RowCount, MaxDecodableRowsPerRowGroup);
    }

    // ───── Encoding-support validation ────────────────

    private static void ValidateEncodingSupport(ColumnDescriptor desc)
    {
        if (!CodecRegistry.TryGet(desc.EncodingTag, out var registered))
        {
            throw new UnsupportedEncodingException(
                desc.Name,
                (byte)desc.EncodingTag,
                desc.CodecVersion,
                $"Column '{desc.Name}' references unknown encoding tag 0x{(byte)desc.EncodingTag:X2}. "
                + "The segment was written by a newer Boutquin.Storage release; upgrade to decode it.");
        }

        if (!registered.IsImplemented)
        {
            throw new UnsupportedEncodingException(
                desc.Name,
                (byte)desc.EncodingTag,
                desc.CodecVersion,
                $"Column '{desc.Name}' uses encoding {registered.Name} (tag 0x{(byte)desc.EncodingTag:X2}), which is reserved but not implemented in this release.");
        }

        if (desc.CodecVersion > registered.CurrentVersion)
        {
            throw new UnsupportedEncodingException(
                desc.Name,
                (byte)desc.EncodingTag,
                desc.CodecVersion,
                $"Column '{desc.Name}' uses encoding {registered.Name} codec version {desc.CodecVersion}; "
                + $"this reader supports up to version {registered.CurrentVersion}. Upgrade Boutquin.Storage.");
        }
    }

    // ───── Non-restartable codec helpers (P3.2) ───────────────────────

    /// <summary>
    /// Returns <see langword="true"/> for codecs whose decoder must process values in order
    /// from the beginning of the stream (XOR and delta-of-delta state is not re-initialised
    /// mid-column). These columns are encoded per-row-group on disk so that each row group's
    /// bytes are independently decodable.
    /// </summary>
    private static bool IsNonRestartable(ColumnEncoding encoding) =>
        encoding is ColumnEncoding.GorillaXor or ColumnEncoding.DeltaOfDeltaVlq;

    /// <summary>
    /// Encodes a non-restartable column per-row-group: each row group's values are encoded
    /// independently and the results concatenated into one blob. Returns the blob and the byte
    /// offset of each row group's start within that blob.
    /// </summary>
    private static (byte[] Blob, int[] Offsets) EncodeColumnPerRowGroup(
        ColumnInfo column,
        Array typedValues,
        IReadOnlyList<RowGroupDescriptor> rowGroups)
    {
        var offsets = new int[rowGroups.Count];
        using var ms = new MemoryStream();

        for (var i = 0; i < rowGroups.Count; i++)
        {
            var rg = rowGroups[i];
            offsets[i] = (int)ms.Position;

            // Build a typed slice for this row group
            var rgSlice = Array.CreateInstance(column.Property.PropertyType, rg.RowCount);
            Array.Copy(typedValues, rg.FirstRowIndex, rgSlice, 0, rg.RowCount);

            var encoded = EncodeColumnFromTypedValues(column, rgSlice);
            ms.Write(encoded, 0, encoded.Length);
        }

        return (ms.ToArray(), offsets);
    }

    // ───── Encode / decode dispatch ───────────────────────────────────

    private static Array BuildTypedValues(
        ColumnInfo column,
        IReadOnlyList<TRecord> records,
        IReadOnlyList<RowGroupDescriptor> rowGroups)
    {
        // Build the full-column typed array (row-group-concatenated) via the property getter.
        var total = 0;
        foreach (var rg in rowGroups)
        {
            total += rg.RowCount;
        }

        // Reject null reference values at the writer boundary instead of letting them flow
        // into the codec layer (DictionaryCodec.EncodeString and friends would otherwise NRE
        // mid-encode, surfacing as opaque codec failures with no field attribution). Mirrors
        // the row-format writer guard in TaggedWireEmitter on string properties. The reader
        // side defaults missing-tag non-nullable strings to string.Empty; writers
        // that need that behaviour must coerce explicitly before calling.
        var propType = column.Property.PropertyType;
        var rejectNulls = !propType.IsValueType ||
            (propType.IsGenericType && propType.GetGenericTypeDefinition() == typeof(Nullable<>));

        var values = Array.CreateInstance(propType, total);
        var cursor = 0;
        foreach (var rg in rowGroups)
        {
            for (var i = 0; i < rg.RowCount; i++)
            {
                var record = records[rg.FirstRowIndex + i];
                var value = column.Property.GetValue(record);
                if (rejectNulls && value is null)
                {
                    throw new InvalidOperationException(
                        $"Columnar property '{column.Name}' on row {rg.FirstRowIndex + i} is null. " +
                        $"Columnar codecs do not encode null reference/Nullable<T> values; coerce " +
                        $"to a non-null sentinel (e.g. string.Empty) before flushing or change the " +
                        $"property type.");
                }
                values.SetValue(value, cursor++);
            }
        }

        return values;
    }

    private static byte[] EncodeColumnFromTypedValues(ColumnInfo column, Array values)
    {
        return column.Encoding switch
        {
            ColumnEncoding.Dictionary when column.Property.PropertyType == typeof(string)
                => DictionaryCodec.EncodeString((string[])values),
            ColumnEncoding.Dictionary when column.Property.PropertyType == typeof(decimal)
                => DictionaryCodec.EncodeDecimal((decimal[])values),
            ColumnEncoding.BitmapRle
                => BitmapRleCodec.Encode((bool[])values),
            ColumnEncoding.DeltaVlq when column.Property.PropertyType == typeof(int)
                => DeltaVlqCodec.EncodeInt32((int[])values),
            ColumnEncoding.DeltaVlq when column.Property.PropertyType == typeof(long)
                => DeltaVlqCodec.EncodeInt64((long[])values),
            ColumnEncoding.DeltaVlq when column.Property.PropertyType.IsEnum
                => DeltaVlqCodec.EncodeInt64(WidenEnumValues(values)),
            ColumnEncoding.DeltaOfDeltaVlq
                => DeltaOfDeltaVlqCodec.Encode((DateOnly[])values),
            ColumnEncoding.GorillaXor when column.Property.PropertyType == typeof(float)
                => GorillaXorCodec.Encode(WidenFloatBits((float[])values)),
            ColumnEncoding.GorillaXor
                => GorillaXorCodec.Encode((double[])values),
            ColumnEncoding.RawDouble
                => EncodeRawDoubles((double[])values),
            ColumnEncoding.ScaledIntegerBitPacked
                => ScaledIntegerBitPackedCodec.EncodeDecimal((decimal[])values),
            _ => throw new NotSupportedException(
                $"Column '{column.Name}' requests encoding {column.Encoding} for type "
                + $"{column.Property.PropertyType.FullName}, which has no codec pairing."),
        };
    }

    // Whole-column decode, retained for the unit tests that pin its summed-row-count and routing
    // guards by reflection. No read path calls it: ReadRowGroupsAsync, ReadBatchesAsync, ReadAsync and
    // StreamWhereAsync all decode one row group at a time through DecodeRowGroupSlice, because this
    // method's allocation scales with the footer's summed row count (the ceiling below grows with the
    // forgeable row-group count). Do not route a read path through it.
    private static Array DecodeColumn(ColumnInfo column, byte[] payload, SegmentFooter footer)
    {
        // Accumulate in a long so a forged footer cannot wrap the sum past int.MaxValue and mask
        // the bound below — a negative wrap would bypass the per-codec count guard (-N > remaining
        // is false) and a small-positive wrap would yield a short decoded array that the rehydrate
        // loop (outside the per-column try/catch) indexes past, leaking a raw IndexOutOfRangeException.
        // FooterReader already rejected rowCount <= 0; it does NOT bound the summed total. Mirrors the
        // non-restartable assembly path's long accumulation.
        long totalRows = 0;
        foreach (var rg in footer.RowGroups)
        {
            totalRows += rg.RowCount;
        }

        // Whole-column restartable decode: `totalRows` is the segment-wide row count, the sum across
        // every row group. The allocation-before-bound ceiling is the per-row-group cap scaled by the
        // number of row groups — never the segment byte length, which a run-length codec under-counts
        // (F-W3-regress-01). Computed in long so the product cannot overflow.
        var maxDecodableRows = (long)footer.RowGroups.Count * MaxDecodableRowsPerRowGroup;

        // The footer's summed rowCount is attacker-controllable (CRC is forgeable). Reject any total
        // exceeding int.MaxValue BEFORE narrowing — the flat decoded array is int-indexed, so a forged
        // multi-row-group footer whose counts sum past int.MaxValue would otherwise wrap the cast: a
        // negative wrap bypasses the per-codec count guard (-N > remaining is false → raw OverflowException
        // on the negative-size array), and a small-positive wrap yields a short decoded array the rehydrate
        // loop (outside the per-column try/catch) indexes past → a raw IndexOutOfRangeException escapes the
        // read API. Fail closed with the typed corruption exception the rest of this reader uses. Totals
        // within int range stay bounded by each codec's own per-row-group ceiling (maxDecodableRows) below.
        if (totalRows > int.MaxValue)
        {
            throw new TruncatedSegmentException(
                $"Column '{column.Name}' footer declares {totalRows} summed rows, which exceeds the "
                + $"{int.MaxValue} rows a single column's flat array can address; segment is corrupt.",
                payload.Length,
                totalRows);
        }

        var total = (int)totalRows;

        return column.Encoding switch
        {
            ColumnEncoding.Dictionary when column.Property.PropertyType == typeof(string)
                => DictionaryCodec.DecodeString(payload, total),
            ColumnEncoding.Dictionary when column.Property.PropertyType == typeof(decimal)
                => DictionaryCodec.DecodeDecimal(payload, total),
            ColumnEncoding.BitmapRle
                => BitmapRleCodec.Decode(payload, total, maxDecodableRows),
            ColumnEncoding.DeltaVlq when column.Property.PropertyType == typeof(int)
                => DeltaVlqCodec.DecodeInt32(payload, total, maxDecodableRows),
            ColumnEncoding.DeltaVlq when column.Property.PropertyType == typeof(long)
                => DeltaVlqCodec.DecodeInt64(payload, total, maxDecodableRows),
            ColumnEncoding.DeltaVlq when column.Property.PropertyType.IsEnum
                => NarrowEnumValues(column.Property.PropertyType, DeltaVlqCodec.DecodeInt64(payload, total, maxDecodableRows)),
            ColumnEncoding.RawDouble
                => DecodeRawDoubles(payload, total),
            ColumnEncoding.ScaledIntegerBitPacked
                => ScaledIntegerBitPackedCodec.DecodeDecimal(payload, total, maxDecodableRows),
            // Non-restartable encodings (GorillaXor, DeltaOfDeltaVlq) are stored per-row-group
            // via EncodeColumnPerRowGroup and decoded through DecodeColumnSlice on each
            // row-group slice. Reaching DecodeColumn for them indicates a routing-invariant
            // violation — most likely IsNonRestartable and the encoder dispatch have drifted.
            // Fail loudly so the bug surfaces at the first affected read rather than silently
            // running a codec against the wrong byte format.
            ColumnEncoding.GorillaXor or ColumnEncoding.DeltaOfDeltaVlq
                => throw new InvalidOperationException(
                    $"Column '{column.Name}' uses non-restartable encoding {column.Encoding} but "
                    + "reached the full-column DecodeColumn path. Non-restartable codecs must "
                    + "be routed through EncodeColumnPerRowGroup + DecodeColumnSlice. This "
                    + "indicates a drift between IsNonRestartable and the encoder's per-row-group "
                    + "dispatch."),
            _ => throw new NotSupportedException(
                $"Column '{column.Name}' uses encoding {column.Encoding} for type "
                + $"{column.Property.PropertyType.FullName}, which has no codec pairing."),
        };
    }

    /// <summary>
    /// Decodes exactly <paramref name="count"/> values starting at <paramref name="firstRowIndex"/>
    /// from <paramref name="payload"/>. For non-restartable codecs (GorillaXor, DeltaOfDeltaVlq)
    /// the caller supplies a per-row-group slice so <paramref name="firstRowIndex"/> is 0;
    /// for restartable codecs the caller supplies the whole-column payload.
    /// </summary>
    private static Array DecodeColumnSlice(ColumnInfo column, ReadOnlySpan<byte> payload, int firstRowIndex, int count, long maxDecodableRows)
    {
        return column.Encoding switch
        {
            // Non-restartable: payload is already the per-RG slice; firstRowIndex always 0.
            ColumnEncoding.DeltaOfDeltaVlq when column.Property.PropertyType == typeof(DateOnly)
                => DeltaOfDeltaVlqCodec.Decode(payload, count, maxDecodableRows),
            ColumnEncoding.GorillaXor when column.Property.PropertyType == typeof(float)
                => NarrowFloatBits(GorillaXorCodec.Decode(payload, count, maxDecodableRows)),
            ColumnEncoding.GorillaXor when column.Property.PropertyType == typeof(double)
                => GorillaXorCodec.Decode(payload, count, maxDecodableRows),
            // Restartable — fixed-width, directly bit-seekable.
            ColumnEncoding.ScaledIntegerBitPacked when column.Property.PropertyType == typeof(decimal)
                => ScaledIntegerBitPackedCodec.DecodeDecimalSlice(payload, firstRowIndex, count, maxDecodableRows),
            ColumnEncoding.RawDouble when column.Property.PropertyType == typeof(double)
                => DecodeRawDoublesSlice(payload, firstRowIndex, count),
            // Restartable — scan-skip via decode-full-then-slice.
            ColumnEncoding.DeltaVlq when column.Property.PropertyType == typeof(int)
                => DeltaVlqCodec.DecodeSliceInt32(payload, firstRowIndex, count, maxDecodableRows),
            ColumnEncoding.DeltaVlq when column.Property.PropertyType == typeof(long)
                => DeltaVlqCodec.DecodeSliceInt64(payload, firstRowIndex, count, maxDecodableRows),
            ColumnEncoding.DeltaVlq when column.Property.PropertyType.IsEnum
                => NarrowEnumValues(column.Property.PropertyType, DeltaVlqCodec.DecodeSliceInt64(payload, firstRowIndex, count, maxDecodableRows)),
            ColumnEncoding.Dictionary when column.Property.PropertyType == typeof(string)
                => DictionaryCodec.DecodeStringSlice(payload, firstRowIndex, count),
            ColumnEncoding.Dictionary when column.Property.PropertyType == typeof(decimal)
                => DictionaryCodec.DecodeDecimalSlice(payload, firstRowIndex, count),
            ColumnEncoding.BitmapRle when column.Property.PropertyType == typeof(bool)
                => BitmapRleCodec.DecodeSlice(payload, firstRowIndex, count, maxDecodableRows),
            _ => throw new NotSupportedException(
                $"Column '{column.Name}' uses encoding {column.Encoding} for type "
                + $"{column.Property.PropertyType.FullName}, which has no DecodeSlice pairing."),
        };
    }

    // float columns ride the GorillaXor stream as zero-extended raw float bits. Bit
    // transport — NOT an FP widening conversion, which can quiet a signaling NaN on
    // hardware convert instructions — keeps the round-trip bit-exact for every payload.
    // Similar float values share high bits, so the container doubles XOR with >= 32
    // leading zeros and compress well under Gorilla's leading-zero clamp.
    private static double[] WidenFloatBits(float[] values)
    {
        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = BitConverter.Int64BitsToDouble(BitConverter.SingleToUInt32Bits(values[i]));
        }

        return result;
    }

    private static float[] NarrowFloatBits(double[] values)
    {
        var result = new float[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = BitConverter.UInt32BitsToSingle((uint)BitConverter.DoubleToUInt64Bits(values[i]));
        }

        return result;
    }

    // Enum columns ride the DeltaVlq stream as the underlying value widened to int64 —
    // bit transport for ulong underlyings (two's-complement), Convert for the rest. The
    // concrete enum type is recovered from the plan's property at decode.
    private static long[] WidenEnumValues(Array values)
    {
        var result = new long[values.Length];
        var underlyingIsUInt64 = Enum.GetUnderlyingType(values.GetType().GetElementType()!) == typeof(ulong);
        for (var i = 0; i < values.Length; i++)
        {
            var boxed = values.GetValue(i)!;
            result[i] = underlyingIsUInt64
                ? unchecked((long)Convert.ToUInt64(boxed, System.Globalization.CultureInfo.InvariantCulture))
                : Convert.ToInt64(boxed, System.Globalization.CultureInfo.InvariantCulture);
        }

        return result;
    }

    private static Array NarrowEnumValues(Type enumType, long[] widened)
    {
        var result = Array.CreateInstance(enumType, widened.Length);
        for (var i = 0; i < widened.Length; i++)
        {
            result.SetValue(Enum.ToObject(enumType, widened[i]), i);
        }

        return result;
    }

    private static byte[] EncodeRawDoubles(double[] values)
    {
        var output = new byte[values.Length * 8];
        var span = output.AsSpan();
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(i * 8, 8), values[i]);
        }

        return output;
    }

    private static double[] DecodeRawDoubles(ReadOnlySpan<byte> payload, int count)
    {
        // RawDouble does not self-bound from its payload (every double is a fixed 8 bytes with no
        // length prefix). The codecs that genuinely self-bound from their own payload are Dictionary
        // (>= 1 id byte per row) and the bitWidth>0 arm of ScaledIntegerBitPacked (fixed bits per
        // row). The run-length codecs (DeltaVlq, BitmapRle) and the constant-column (bitWidth==0)
        // arm of ScaledIntegerBitPacked CANNOT self-bound: a single RLE run — or a ~0-byte constant
        // column — can legitimately express an arbitrary count from a tiny payload, so their
        // per-payload ceiling saturates and they instead bound the count against the caller-threaded
        // segment-level ceiling (the segment byte length). Do NOT assume "the codec self-bounds" here:
        // RawDouble needs its own explicit bound. `count` is the footer-supplied
        // summed rowCount (CRC is body-only and forgeable), so bound it BEFORE the allocation:
        // each raw double is exactly 8 bytes, so `count` values need exactly `count * 8` payload
        // bytes. A `count` exceeding payload.Length / 8 cannot have been produced by this payload
        // and is corruption — reject it with the same TruncatedSegmentException the overrun
        // contract uses, rather than over-allocating and then indexing past the payload.
        ValidateRawDoubleRange(0, count, payload.Length);
        var result = new double[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(i * 8, 8));
        }

        return result;
    }

    private static double[] DecodeRawDoublesSlice(ReadOnlySpan<byte> payload, int firstRowIndex, int count)
    {
        // RawDouble does not self-bound (fixed 8 bytes per value, no length prefix) and `count` is
        // footer-supplied, so bound it before the allocation: the read window starts at
        // firstRowIndex, so the payload must hold (firstRowIndex + count) * 8 bytes before the
        // new double[count] allocation.
        ValidateRawDoubleRange(firstRowIndex, count, payload.Length);
        var result = new double[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice((firstRowIndex + i) * 8, 8));
        }

        return result;
    }

    // Each raw double occupies exactly 8 bytes. A read of `count` values starting at
    // `firstRowIndex` needs (firstRowIndex + count) * 8 payload bytes. The footer-supplied count
    // is attacker-controllable (CRC is forgeable), so a count whose 8-byte stride overruns the
    // payload cannot have been encoded by it and is corruption. Compute the required byte count in
    // long arithmetic so a colossal forged count never overflows the multiply before the check.
    private static void ValidateRawDoubleRange(int firstRowIndex, int count, long payloadByteLength)
    {
        var requiredBytes = ((long)firstRowIndex + count) * 8;
        if (requiredBytes > payloadByteLength)
        {
            throw new TruncatedSegmentException(
                $"RawDouble column declares {count} values starting at row {firstRowIndex}, requiring "
                + $"{requiredBytes} payload bytes, but only {payloadByteLength} are present; segment is corrupt.",
                payloadByteLength,
                requiredBytes);
        }
    }

    // A footer-supplied column extent (ByteOffset/ByteLength) is attacker-controllable on a
    // corrupt or forged segment. Validate it against the actual stream length BEFORE the
    // new byte[desc.ByteLength] allocation — otherwise a huge declared length surfaces an
    // uncaught OverflowException (the allocation sits outside the per-column decode guard)
    // or drives an unbounded allocation. Mirrors the alloc-before-bound guards added to the
    // Phase-3 codecs and the footer row-group validation.
    private static void ValidateColumnExtent(ColumnDescriptor desc, long streamLength)
    {
        if (desc.ByteOffset < 0 || desc.ByteLength < 0)
        {
            throw new CorruptSegmentException(
                $"Column '{desc.Name}' declares a negative extent (offset {desc.ByteOffset}, length {desc.ByteLength}); segment is corrupt.",
                desc.ByteOffset);
        }

        // An overrun (column claims more bytes than the file holds) is the truncation contract
        // ReadExactly already enforces — but ReadExactly only fires AFTER new byte[ByteLength],
        // so a colossal declared length (e.g. long.MaxValue) blows up in the allocation with an
        // uncaught OverflowException first. Pre-empt with the same TruncatedSegmentException
        // ReadExactly would have thrown, so the contract holds for every overrun magnitude.
        if (desc.ByteLength > streamLength - desc.ByteOffset)
        {
            throw new TruncatedSegmentException(
                $"Column '{desc.Name}' declares offset {desc.ByteOffset} + length {desc.ByteLength}, "
                + $"which overruns the {streamLength}-byte segment.",
                streamLength,
                desc.ByteOffset + desc.ByteLength);
        }
    }

    // A footer row count is attacker-controllable (CRC is forgeable, and FooterReader validates each
    // rowCount only against zero, never against segment size). A non-restartable column is decoded
    // one row group slice at a time, and its codec allocates the row group's values BEFORE decoding.
    // Bound the count against the slice payload: GorillaXor / DeltaOfDeltaVlq each emit >= 1 bit per
    // value, so `total` rows need >= total/8 payload bytes. A `total` exceeding payloadByteLength * 8
    // cannot have been produced by this payload — reject it with the same TruncatedSegmentException
    // the overrun contract uses.
    internal static void ValidateRowCountAgainstPayload(string columnName, long total, long payloadByteLength)
    {
        // The flat array allocated below is sized (int)total. The bytes×8 ceiling below bounds total
        // against the payload, but that ceiling itself can exceed int.MaxValue for a >256 MiB payload
        // (payloadByteLength × 8 > int.MaxValue once payloadByteLength > ~256 MiB). A forged `total`
        // in (int.MaxValue, payloadByteLength × 8] would then clear the ceiling yet overflow the
        // (int)total cast into a negative/wrapped length. No segment can legitimately carry more than
        // int.MaxValue rows in one column (the flat array is int-indexed), so reject the overflow
        // explicitly BEFORE the cast — using the same typed exception the ceiling below uses.
        if (total > int.MaxValue)
        {
            throw new TruncatedSegmentException(
                $"Column '{columnName}' footer declares {total} summed rows, which exceeds the "
                + $"{int.MaxValue} rows a single column's flat array can address; segment is corrupt.",
                payloadByteLength,
                total);
        }

        // payloadByteLength * 8 is the maximum number of >= 1-bit values the payload could hold.
        // Compute the ceiling in long arithmetic so it never overflows for a real segment.
        var maxEncodableRows = payloadByteLength * 8;
        if (total > maxEncodableRows)
        {
            throw new TruncatedSegmentException(
                $"Column '{columnName}' footer declares {total} summed rows, but the {payloadByteLength}-byte "
                + $"payload can encode at most {maxEncodableRows} rows (>= 1 bit per value); segment is corrupt.",
                payloadByteLength,
                total);
        }
    }

    private static void ReadExactly(Stream input, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = input.Read(buffer[total..]);
            if (read == 0)
            {
                throw new TruncatedSegmentException(
                    $"Stream ended after {total} of {buffer.Length} expected bytes.",
                    input.Length,
                    input.Position + (buffer.Length - total));
            }

            total += read;
        }
    }

    // ───── zone maps ──────────────────────────────────────

    private static IReadOnlyList<IReadOnlyList<ColumnRowGroupStats>>? ComputeZoneMaps(
        IReadOnlyList<ColumnDescriptor> descriptors,
        Array[] typedColumnArrays,
        IReadOnlyList<RowGroupDescriptor> rowGroups)
    {
        if (rowGroups.Count == 0)
        {
            // No row groups → no useful stats. Emit null so readers skip straight to full decode.
            return null;
        }

        var zoneMaps = new IReadOnlyList<ColumnRowGroupStats>[rowGroups.Count];
        for (var rg = 0; rg < rowGroups.Count; rg++)
        {
            var perColumn = new ColumnRowGroupStats[descriptors.Count];
            for (var c = 0; c < descriptors.Count; c++)
            {
                perColumn[c] = ZoneMapComputer.Compute(
                    typedColumnArrays[c],
                    descriptors[c].FieldType,
                    rowGroups[rg].FirstRowIndex,
                    rowGroups[rg].RowCount);
            }
            zoneMaps[rg] = perColumn;
        }

        return zoneMaps;
    }

    // ───── bitmap indexes ──────────

    private IReadOnlyList<BitmapIndexEntry>? ComputeBitmapIndexes(
        IReadOnlyList<ColumnDescriptor> descriptors,
        Array[] typedColumnArrays)
    {
        List<BitmapIndexEntry>? entries = null;
        for (var c = 0; c < _columns.Count; c++)
        {
            var column = _columns[c];
            if (!HasBitmapIndex(column))
            {
                continue;
            }

            var entry = BitmapIndexComputer.Compute(
                descriptors[c],
                typedColumnArrays[c]);
            entries ??= [];
            entries.Add(entry);
        }
        return entries;
    }

    // ───── per-row-group CRC32C — canonical row-group bytes ───

    private static IReadOnlyList<uint>? ComputeRowGroupCrcs(
        IReadOnlyList<ColumnDescriptor> descriptors,
        Array[] typedColumnArrays,
        IReadOnlyList<RowGroupDescriptor> rowGroups)
    {
        if (rowGroups.Count == 0)
        {
            return null;
        }

        var crcs = new uint[rowGroups.Count];
        for (var rg = 0; rg < rowGroups.Count; rg++)
        {
            var canonical = RowGroupCanonicalBytes.Build(descriptors, typedColumnArrays, rowGroups[rg]);
            crcs[rg] = Crc32C.HashToUInt32(canonical);
        }
        return crcs;
    }
}

/// <summary>
/// Opt-in capability for an <see cref="IColumnarSegmentFormat{TRecord}"/> whose write path cannot
/// encode every record the engine would otherwise accept. The columnar envelope calls
/// <see cref="ValidateRecord"/> from its entry admission, so a record the format would refuse at
/// flush is refused before the write is acknowledged.
/// </summary>
/// <typeparam name="TRecord">The record type.</typeparam>
internal interface IColumnarRecordAdmission<in TRecord>
{
    /// <summary>Throws <see cref="ArgumentException"/> when the format cannot write the record.</summary>
    /// <param name="record">The record to admit.</param>
    void ValidateRecord(TRecord record);
}
