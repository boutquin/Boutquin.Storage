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
/// Direction in which a columnar reader iterates rows (and row-groups). The on-disk row
/// order is fixed at write time by the segment's
/// <see cref="ValueObjects.TimeSeries.ColumnSortSpec"/>; this knob lets readers iterate
/// the persisted order in either direction.
/// </summary>
/// <remarks>
/// <para>
/// <b>v2.0.0 ships only <see cref="Ascending"/>.</b> Same rationale as
/// <see cref="SortDirection"/>: an additive enum-member addition post-GA is binary-compatible;
/// shipping a Descending member with a runtime stub would expose a partial API surface.
/// </para>
/// </remarks>
public enum ScanDirection
{
    /// <summary>Iterate rows in the persisted (write-time) order.</summary>
    Ascending = 0,
}
