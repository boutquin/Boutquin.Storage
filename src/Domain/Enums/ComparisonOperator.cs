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
/// Comparison operator carried by a column predicate for value-based row-group skipping and
/// row-level filtering over a columnar segment (DDIA 2e Ch. 4 — zone-map / data-skipping
/// indexes). The operator is evaluated as <c>cell OP value</c> (the column cell on the left).
/// </summary>
public enum ComparisonOperator : byte
{
    /// <summary><c>cell == value</c>. Eligible for bitmap-index skipping on indexed columns.</summary>
    Equal = 0,

    /// <summary><c>cell != value</c>. Cannot skip a row group on zone maps alone (a non-matching
    /// span may still contain other matching values), so it is a row-level filter only.</summary>
    NotEqual = 1,

    /// <summary><c>cell &lt; value</c>. A row group is skippable when its zone-map minimum is
    /// already <c>&gt;= value</c>.</summary>
    LessThan = 2,

    /// <summary><c>cell &lt;= value</c>. A row group is skippable when its zone-map minimum is
    /// already <c>&gt; value</c>.</summary>
    LessThanOrEqual = 3,

    /// <summary><c>cell &gt; value</c>. A row group is skippable when its zone-map maximum is
    /// already <c>&lt;= value</c>.</summary>
    GreaterThan = 4,

    /// <summary><c>cell &gt;= value</c>. A row group is skippable when its zone-map maximum is
    /// already <c>&lt; value</c>.</summary>
    GreaterThanOrEqual = 5,
}
