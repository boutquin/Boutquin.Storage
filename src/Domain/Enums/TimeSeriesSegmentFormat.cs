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
namespace Boutquin.Storage.Domain.Enums;

/// <summary>
/// On-disk segment layout for a time-series storage engine. Selected once per engine at
/// construction via <c>TimeSeriesStoreOptions&lt;TRecord&gt;</c>; all segments written by
/// that engine share the format. Existing segments retain the format they were written
/// with — format choice is not dynamic per flush.
/// </summary>
/// <remarks>
/// <para><b>Why an on-disk-format property, not a runtime switch:</b> per DDIA 2nd ed, Ch. 4
/// (Column-Oriented Storage, pp. 136–144), the row-vs-column decision is a function of the
/// read workload — row format serves mixed OLTP-style record reads; column format serves bulk
/// analytical scans that project a subset of fields. A given consumer knows which workload it
/// owns; flipping per flush would produce heterogeneous segment sets whose reader path would
/// need a dispatch on every segment header.</para>
/// <para><b>Row is the default.</b> New engines ship as <see cref="Row"/>; opt into
/// <see cref="Columnar"/> for analytical workloads where per-column scan dominates.</para>
/// </remarks>
public enum TimeSeriesSegmentFormat
{
    /// <summary>
    /// Row-oriented segment format — each record is persisted as a self-contained entry in
    /// the block-structured SSTable (see <c>SstSegmentFormat&lt;TKey, TValue&gt;</c>). This
    /// is the default for new engines. Optimised for reads that project all (or most) fields
    /// per record, and for mixed read/write workloads.
    /// </summary>
    Row = 0,

    /// <summary>
    /// Column-oriented segment format — each field of <c>TRecord</c> is stored as a separate
    /// compressed column, with a footer index allowing a reader to project only the fields it
    /// needs. Optimised for bulk analytical scans (Trading backtests, Analytics calibrations)
    /// that read many records across many datasets but project few fields per record.
    /// </summary>
    Columnar = 1,
}
