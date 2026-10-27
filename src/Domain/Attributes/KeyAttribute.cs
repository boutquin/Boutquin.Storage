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
/// Marks a <c>partial record struct</c> as a storage key: the source generator emits a
/// byte-lex-comparable ordered-wire encoding plus the <see cref="IComparable{T}"/> and
/// <see cref="IEquatable{T}"/> surface the engine needs for in-memory sort order and
/// dictionary lookup.
/// </summary>
/// <remarks>
/// <para>The annotated type must (a) be <c>partial</c> so the generator can emit
/// <c>CompareTo</c> and the wire-format helpers; (b) carry <c>[Tag(N)]</c> on every
/// property — the generator enforces this universally (BSSG010); (c) carry
/// <c>[DecimalScale(N)]</c> on every <c>decimal</c> property so the ordered wire can
/// canonicalise scales (BSSG017). See <c>docs/source-generator-type-reference.md</c>
/// for the full type matrix.</para>
/// <para>Marked <c>[Conditional("BOUTQUIN_STORAGE_GENERATOR")]</c> so the attribute
/// vanishes from compiled output once the generator has consumed it — zero runtime
/// metadata footprint on shipped consumer assemblies.</para>
/// </remarks>
/// <example>
/// <code>
/// [Key]
/// public partial record struct OrderId([property: Tag(1)] long Value);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
[Conditional("BOUTQUIN_STORAGE_GENERATOR")]
public sealed class KeyAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KeyAttribute"/> class.
    /// </summary>
    /// <remarks>
    /// This constructor initializes the attribute and sets it to the target class or struct.
    /// </remarks>
    public KeyAttribute()
    {
    }
}
