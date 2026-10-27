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
namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

/// <summary>
/// Internal single-byte code identifying the CLR type of a column's values. Written into
/// the footer so the reader can pick the correct decoder without reflecting on the record
/// type. Stable across segments — new types are appended with new codes; existing codes
/// are never reassigned.
/// </summary>
internal enum ColumnFieldTypeCode : byte
{
    /// <summary><see cref="System.DateOnly"/>: int32 LE of the day number.</summary>
    DateOnly = 1,

    /// <summary><see cref="int"/>: int32 LE.</summary>
    Int32 = 2,

    /// <summary><see cref="long"/>: int64 LE.</summary>
    Int64 = 3,

    /// <summary><see cref="decimal"/>: 16 bytes LE (the four int32 components of <c>decimal.GetBits</c>).</summary>
    Decimal = 4,

    /// <summary><see cref="double"/>: 8 bytes IEEE-754 LE.</summary>
    Double = 5,

    /// <summary><see cref="float"/>: 4 bytes IEEE-754 LE.</summary>
    Single = 6,

    /// <summary><see cref="bool"/>: 1 byte (0 = false, 1 = true).</summary>
    Boolean = 7,

    /// <summary><see cref="string"/>: VLQ length prefix (variable length) + UTF-8 payload.</summary>
    String = 8,

    /// <summary>
    /// Any CLR enum: the underlying value widened to int64 (two's-complement bit transport
    /// for <see cref="ulong"/> underlyings), 8 bytes LE in canonical/zone-map/bitmap byte
    /// forms. The concrete enum type is recovered from the record's column plan at decode.
    /// Zone-map ordering follows the widened signed value, which diverges from the enum's
    /// unsigned natural order for ulong-underlying members above <see cref="long.MaxValue"/>
    /// — a range skip across that divergence would silently drop matching rows, so the
    /// pushdown layer never zone-map-skips RANGE predicates on ulong-underlying enums
    /// (they defer to the row-level filter). Equality predicates are sign-agnostic and
    /// stay skippable for every underlying.
    /// </summary>
    Enum = 9,
}
