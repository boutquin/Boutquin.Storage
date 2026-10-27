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
/// Derived segment-reader contract for formats that can service a <typeparamref name="TProjection"/>-
/// constrained range scan without materialising unprojected fields. Implemented by columnar
/// readers (where the projection maps to a per-column offset/decode skip); NOT implemented by
/// row-format readers (row formats materialise the full record at decode time — projection has
/// no per-field skip to offer).
/// </summary>
/// <typeparam name="TKey">Segment key type.</typeparam>
/// <typeparam name="TValue">Segment value type.</typeparam>
/// <typeparam name="TProjection">
/// Format-specific projection hint. For columnar time-series readers this is
/// <c>ColumnProjection</c>; future formats may introduce their own hint types.
/// </typeparam>
/// <remarks>
/// <para><b>Why a derived interface, not a parameter on <see cref="ISegmentReader{TKey,TValue}"/>:</b>
/// projection is a columnar-only concept. Adding an opaque hint parameter to the base reader
/// contract weakens its typing (row readers carry a parameter they can't use meaningfully) and
/// leaks columnar concerns onto the generic segment-format surface. A derived interface keeps
/// the generic reader contract column-agnostic while giving the columnar path a strongly-typed
/// projection channel.</para>
/// <para><b>Dispatch pattern:</b> the engine-decorator layer performs a runtime
/// <c>is IProjectingSegmentReader&lt;,,&gt;</c> cast per segment scan. A cast that fails falls
/// back to the non-projected path (full materialisation). Row-format segment readers fail the
/// cast by design.</para>
/// </remarks>
public interface IProjectingSegmentReader<TKey, TValue, TProjection>
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Streams every entry whose key is in <c>[<paramref name="startKey"/>, <paramref name="endKey"/>]</c>,
    /// materialising only the fields named by <paramref name="projection"/>. Unprojected field
    /// spans are not read from disk and their bytes are not decoded.
    /// </summary>
    /// <param name="startKey">Inclusive lower bound of the key range.</param>
    /// <param name="endKey">Inclusive upper bound of the key range.</param>
    /// <param name="projection">Which fields to materialise.</param>
    /// <param name="direction">
    /// Direction in which to iterate the segment. v2.0.0 ships only
    /// <see cref="ScanDirection.Ascending"/>; readers reject any other value with
    /// <see cref="ArgumentOutOfRangeException"/>. The columnar reader threads
    /// <paramref name="direction"/> through to the inner
    /// <c>IColumnarSegmentFormat&lt;TRecord&gt;.ReadAsync</c> call so the envelope and inner
    /// reader iterate in lockstep.
    /// </param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>Async stream of key/value pairs in the requested direction.</returns>
    IAsyncEnumerable<KeyValuePair<TKey, TValue>> StreamProjectedRangeAsync(
        TKey startKey,
        TKey endKey,
        TProjection projection,
        ScanDirection direction = ScanDirection.Ascending,
        CancellationToken cancellationToken = default);
}
