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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;

namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

// Locates the source-generator-emitted
//   public static IComparer<T> CreateColumnSortComparer(ColumnSortSpec)
// on a record type and exposes it as a typed delegate. Lookup runs once per closed generic
// (the static field `_factory` is initialised exactly once by the CLR's type-init machinery)
// so the per-row-group sort path pays one delegate invocation to obtain the comparer and
// zero reflection per Compare.
//
// Returning null when the type carries no generated method is the load-bearing piece — it
// lets the segment format fall back to the reflection-based comparator (a live fallback, not dead
// legacy code) for hand-written records (test stubs implementing IColumnarRehydratable<T> directly,
// fixtures that deliberately skip the [ColumnarStorable] generator path).
internal static class ColumnSortComparerFactory<T>
{
    private static readonly Func<ColumnSortSpec, IComparer<T>>? s_factory = ResolveFactory();

    public static IComparer<T>? TryCreate(ColumnSortSpec spec) => s_factory?.Invoke(spec);

    private static Func<ColumnSortSpec, IComparer<T>>? ResolveFactory()
    {
        var method = typeof(T).GetMethod(
            "CreateColumnSortComparer",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(ColumnSortSpec)],
            modifiers: null);
        if (method is null || method.ReturnType != typeof(IComparer<T>))
        {
            return null;
        }

        try
        {
            return (Func<ColumnSortSpec, IComparer<T>>)Delegate.CreateDelegate(
                typeof(Func<ColumnSortSpec, IComparer<T>>), method);
        }
        catch (ArgumentException)
        {
            // Signature mismatch surfaced post-binding (extremely rare under modern Roslyn —
            // the GetMethod call already filtered by parameter types and return type). Treat
            // as "no generated comparer available" and let the segment format fall back to
            // the reflection comparator. The hand-written path remains correct; only the
            // perf optimisation is forgone.
            return null;
        }
    }
}
