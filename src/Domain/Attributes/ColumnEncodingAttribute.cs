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
using System.Diagnostics;

namespace Boutquin.Storage.Domain.Attributes;

/// <summary>
/// Overrides the per-field compression codec the source generator would otherwise select from
/// the property's CLR type. Apply to a property of a type marked with
/// <see cref="ColumnarStorableAttribute"/>.
/// </summary>
/// <remarks>
/// <para><b>When to use:</b> the generator's type-based defaults are tuned for the common
/// case in each type family; override is for known exceptions — e.g.,
/// <see cref="ColumnEncoding.RawDouble"/> on a <see cref="double"/> column known to be
/// high-entropy (residuals, Monte-Carlo draws), where <see cref="ColumnEncoding.GorillaXor"/>
/// would degrade to worse-than-raw output.</para>
/// <para><b>Decision rubric for <see cref="ColumnEncoding.GorillaXor"/> vs
/// <see cref="ColumnEncoding.RawDouble"/>:</b></para>
/// <list type="bullet">
/// <item><b>Keep <c>GorillaXor</c> (default):</b> price time-series (OHLC), curve yields,
/// regularly-sampled measurements that move slowly between adjacent rows.</item>
/// <item><b>Override to <c>RawDouble</c>:</b> computed residuals, Monte-Carlo draws, hashed
/// values, random samples — columns where consecutive rows share few leading/trailing zero
/// bits. Gorilla XOR degrades to ~8 B/value (equal to raw) plus ~1 B/value framing overhead
/// on high-entropy input, so raw is strictly faster and smaller there.</item>
/// </list>
/// <para><b>Benchmark citation:</b> Pelkonen et al. 2015 §4.1.2 reports ~1.37 B/value on
/// monitoring-style doubles; authors should not assume that ratio on their column without
/// measuring.</para>
/// <para><b>Compile-time validation:</b> applying an encoding that is not compatible with
/// the property's CLR type raises diagnostic <c>BSSG007</c> at build time. See
/// <see cref="ColumnEncoding"/> remarks for the full type-compatibility matrix.</para>
/// <para><b>Compile-time-only attribute:</b> vanishes from compiled output via
/// <c>[Conditional("BOUTQUIN_STORAGE_GENERATOR")]</c>.</para>
/// </remarks>
/// <remarks>Initialises a new instance with the supplied per-field encoding override.</remarks>
[Conditional("BOUTQUIN_STORAGE_GENERATOR")]
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class ColumnEncodingAttribute(ColumnEncoding encoding) : Attribute
{

    /// <summary>The encoding override the source generator must apply to the annotated property.</summary>
    public ColumnEncoding Encoding { get; } = encoding;
}
