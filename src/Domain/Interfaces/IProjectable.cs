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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Record-shaped types that can produce a projected copy of themselves — a copy whose
/// unprojected fields are reset to <c>default(TField)</c> while projected fields retain their
/// original values. Emitted per record by the storage source generator (for
/// <c>[StorageSerializable]</c> and <c>[ColumnarStorable]</c>-annotated records) so that
/// memtable-residing records expose the same projection shape as records read back from a
/// columnar segment (DDIA 2e Ch. 4 p. 141 — the engine hides the memtable-vs-segment
/// distinction from the caller).
/// </summary>
/// <typeparam name="TSelf">The concrete record type (F-bounded: <c>record : IProjectable&lt;record&gt;</c>).</typeparam>
/// <remarks>
/// <para><b>Strict-mode tie-in:</b> when <see cref="ColumnProjection.AllowMissing"/> is
/// <see langword="false"/> (default) and a projected field name is not declared on
/// <typeparamref name="TSelf"/>, implementations throw
/// <see cref="Boutquin.Storage.Domain.Exceptions.ColumnNotFoundException"/> — mirroring the
/// missing-column behaviour of <c>IColumnarSegmentFormat.ReadAsync</c>. When
/// <see cref="ColumnProjection.AllowMissing"/> is <see langword="true"/>, unknown field names
/// are silently dropped.</para>
/// <para><b>Zero-allocation target:</b> for value-type records the projected copy sits entirely
/// on the stack; for reference-type records one allocation per call is the minimum and
/// generator implementations aim to match it without boxing.</para>
/// <para><b>Decorator-level consumption:</b> <c>LsmTimeSeriesStorageEngine&lt;,&gt;</c> applies
/// <see cref="ProjectTo"/> to memtable records via a runtime <c>is IProjectable&lt;TRecord&gt;</c>
/// cast. Records that do not implement this interface flow through unprojected — a documented
/// trade-off that keeps the generic engine TRecord constraint untouched while still honouring
/// the uniformity guarantee for generator-emitted records.</para>
/// </remarks>
public interface IProjectable<TSelf>
{
    /// <summary>
    /// Returns a copy of this record with only the fields named by <paramref name="projection"/>
    /// populated; every other field is <c>default(TField)</c>.
    /// </summary>
    /// <param name="projection">Which fields to copy. <see cref="ColumnProjection.All"/> returns the record unchanged.</param>
    /// <returns>The projected copy.</returns>
    TSelf ProjectTo(ColumnProjection projection);
}
