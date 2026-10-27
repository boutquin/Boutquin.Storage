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
#nullable enable

namespace Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;

/// <summary>
/// Columnar types that can reconstruct a <typeparamref name="TSelf"/> instance from a
/// row-group's decoded column arrays without reflection or per-field boxing.
/// </summary>
/// <typeparam name="TSelf">
/// The concrete record type implementing this interface
/// (F-bounded: <c>record : IColumnarRehydratable&lt;record&gt;</c>).
/// </typeparam>
/// <remarks>
/// <para><b>Generator contract:</b> the storage source generator emits a <c>Rehydrate</c>
/// implementation on every <c>[ColumnarStorable]</c>-annotated <c>partial</c> type using
/// <c>with</c>-expressions. The generated implementation casts column arrays directly to their
/// CLR element types, avoiding <see cref="System.Reflection.PropertyInfo.SetValue(object, object)"/> and the
/// per-field boxing that <c>SetValue</c> incurs on value-type records.</para>
/// <para><b>Partial projection:</b> columns not present in <c>columns</c> retain
/// the type's default value for that field — the same partial-materialisation semantics as
/// <c>IColumnarSegmentFormat.ReadAsync</c> with a projected <c>ColumnProjection</c>.</para>
/// <para><b>Error contract:</b> the generated implementation casts arrays directly
/// (<c>((CLRType[])array)[rowIndex]</c>) and performs no type probe; an array of the wrong
/// element type throws <see cref="System.InvalidCastException"/>. An out-of-range
/// <c>rowIndex</c> throws <see cref="System.IndexOutOfRangeException"/>. No
/// bounds check is emitted — callers that pass row-group-scoped slice arrays use 0-based
/// intra-group indices; callers that pass full-segment arrays use the absolute row index.</para>
/// </remarks>
public interface IColumnarRehydratable<TSelf>
    where TSelf : IColumnarRehydratable<TSelf>
{
    /// <summary>
    /// Constructs a <typeparamref name="TSelf"/> instance from a set of decoded column arrays.
    /// Only the columns present in <paramref name="columns"/> are populated; missing columns
    /// retain the type's default value for that field.
    /// </summary>
    /// <param name="columns">
    /// Column name → typed <see cref="System.Array"/> of the column's CLR element type. Callers
    /// are responsible for passing arrays of the correct element type; the generated
    /// implementation casts directly (no element-type probe) and will throw
    /// <see cref="System.InvalidCastException"/> on type mismatch.
    /// </param>
    /// <param name="rowIndex">
    /// Zero-based index into each array for the row to materialise. Callers that pass
    /// row-group-scoped slice arrays use 0-based intra-group indices; callers that pass
    /// full-segment arrays use the absolute row index. No bounds check is performed in the
    /// generated implementation; out-of-range indices throw
    /// <see cref="System.IndexOutOfRangeException"/>.
    /// </param>
    /// <returns>A <typeparamref name="TSelf"/> with projected fields populated from the arrays.</returns>
    static abstract TSelf Rehydrate(IReadOnlyDictionary<string, Array> columns, int rowIndex);
}
