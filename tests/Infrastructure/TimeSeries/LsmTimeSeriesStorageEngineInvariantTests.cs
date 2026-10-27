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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

/// <summary>
/// AC-9.4 (F-40, test gaps): two engine-boundary invariants on the top-level
/// <see cref="LsmTimeSeriesStorageEngine{TDatasetKey,TRecord}"/> projected-range path.
/// <list type="number">
///   <item>The projected-columnar fast path requires a PUBLIC <c>Date</c> property of type
///   <see cref="DateOnly"/>; a record that satisfies <c>IHasDate</c> only through an explicit
///   interface implementation must fail fast with <see cref="InvalidOperationException"/>
///   rather than surface a phantom-column error from deep inside the segment read.</item>
///   <item>The per-segment <c>SegmentMaxSeqnos</c> invariant — every snapshot segment has a
///   corresponding max-seqno entry — must throw <see cref="InvalidOperationException"/> on a
///   shortfall rather than silently fall back to seqno 0 and lose tombstone races.</item>
/// </list>
/// </summary>
public sealed class LsmTimeSeriesStorageEngineInvariantTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"TsInvariant_{Guid.NewGuid():N}");

    public LsmTimeSeriesStorageEngineInvariantTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
        }
    }

    // Builds a columnar-format top-level engine over ExplicitDateBar — a record satisfying
    // IHasDate via an EXPLICIT interface implementation (its public date column is "TradeDate";
    // no public "Date" property exists). This is exactly the shape the s_hasPublicDateColumn
    // guard rejects on the projected-subset path.
    private LsmTimeSeriesStorageEngine<SerializableWrapper<string>, ExplicitDateBar> BuildColumnarExplicitDateEngine()
    {
        var innerColumnar = new CompressedColumnarSegmentFormat<ExplicitDateBar>();
        var segmentFormat = new ColumnarSegmentFormat<SerializableWrapper<string>, ExplicitDateBar>(innerColumnar);

        var engine = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, ExplicitDateBar>(
            memTableCapacity: 32,
            segmentFolder: _folder,
            segmentPrefix: "expl",
            segmentFormat: segmentFormat);

        var options = new ColumnarTimeSeriesStoreOptions<ExplicitDateBar>
        {
            ColumnarFormat = innerColumnar,
        };
        return new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, ExplicitDateBar>(engine, options);
    }

    // ── AC-9.4 #1: explicit-IHasDate record with no public "Date" fails fast on projected read ──

    [Fact]
    public async Task ProjectedSubsetRange_RecordWithoutPublicDateColumn_ThrowsInvalidOperation()
    {
        var engine = BuildColumnarExplicitDateEngine();
        var ds = new SerializableWrapper<string>("XDAT");
        var d0 = new DateOnly(2024, 1, 1);
        var startKey = new TimeSeriesKey<SerializableWrapper<string>>(ds, d0);
        var endKey = new TimeSeriesKey<SerializableWrapper<string>>(ds, d0.AddDays(10));

        // A NON-empty projection routes into GetRangeProjectedAsync, which augments the projection
        // with the literal column name "Date" — only valid when the record exposes a public Date.
        var projection = new ColumnProjection(["Value"]);

        var act = async () =>
        {
            await foreach (var _ in engine.GetRangeAsync(startKey, endKey, projection))
            {
                // drain — the guard throws as enumeration starts.
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>(
                "a projected-subset range read on a record whose date column is not a public 'Date' "
                + "property must fail fast at the engine boundary, not surface a phantom-column error")
            .WithMessage("*Date*");
    }

    // ── AC-9.4 #2: SegmentMaxSeqnos shortfall throws (per-segment invariant) ──
    // The invariant is normally unreachable through the wired path because the inner LSM engine's
    // snapshot machinery always populates one max-seqno per segment. The check is extracted into
    // ValidateSegmentMaxSeqnoInvariant — the exact logic the projected-range loop runs per segment
    // — so the invariant violation is directly testable without forging an internal snapshot.

    [Fact]
    public void SegmentMaxSeqnoInvariant_FewerSeqnosThanSegments_Throws()
    {
        // A snapshot claiming 2 segments but carrying only 1 max-seqno entry violates the
        // invariant; reading segment index 1 has no corresponding seqno.
        var act = () => LsmTimeSeriesStorageEngine<SerializableWrapper<string>, ExplicitDateBar>
            .ValidateSegmentMaxSeqnoInvariant(segmentCount: 2, maxSeqnoCount: 1, segmentIndex: 1);

        act.Should().Throw<InvalidOperationException>(
                "a segment with no corresponding SegmentMaxSeqnos entry would silently lose every "
                + "tombstone race; the invariant must be asserted, not masked")
            .WithMessage("*SegmentMaxSeqnos invariant violated*")
            .WithMessage("*2 segment(s)*")
            .WithMessage("*only 1 max-seqno*");
    }

    [Fact]
    public void SegmentMaxSeqnoInvariant_OneSeqnoPerSegment_DoesNotThrow()
    {
        // The well-formed case: every segment index has a corresponding max-seqno entry.
        var act0 = () => LsmTimeSeriesStorageEngine<SerializableWrapper<string>, ExplicitDateBar>
            .ValidateSegmentMaxSeqnoInvariant(segmentCount: 2, maxSeqnoCount: 2, segmentIndex: 0);
        var act1 = () => LsmTimeSeriesStorageEngine<SerializableWrapper<string>, ExplicitDateBar>
            .ValidateSegmentMaxSeqnoInvariant(segmentCount: 2, maxSeqnoCount: 2, segmentIndex: 1);

        act0.Should().NotThrow();
        act1.Should().NotThrow();
    }
}
