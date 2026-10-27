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
using Boutquin.Storage.Domain.Attributes;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Canonical test double for a Boutquin.MarketData-shaped bar as a <c>record struct</c>.
/// Carries both generator annotations: <c>[StorageSerializable]</c> for
/// <c>ISerializable&lt;T&gt;</c> (row path / WAL) and <c>[ColumnarStorable]</c> for
/// <c>ColumnarFieldPlan</c> + <c>ProjectTo</c> + <c>Rehydrate</c>.
/// </summary>
/// <remarks>
/// This type verifies that a single type can satisfy both generator annotations
/// simultaneously — the primary goal of the <c>IColumnarRehydratable&lt;TSelf&gt;</c> feature.
/// A <c>record struct</c> annotated with <c>[ColumnarStorable]</c> previously failed to
/// rehydrate correctly because <c>PropertyInfo.SetValue</c> boxes value types; the generator-
/// emitted <c>Rehydrate</c> method uses <c>with</c>-expressions instead, eliminating the
/// boxing and enabling this type.
/// </remarks>
[StorageSerializable]
[ColumnarStorable]
public partial record struct ColumnarRecordStructBar(
    [property: Tag(1)] DateOnly Date,
    [property: Tag(2)] decimal Open,
    [property: Tag(3)] decimal High,
    [property: Tag(4)] decimal Low,
    [property: Tag(5)] decimal Close,
    [property: Tag(6)] decimal AdjustedClose,
    [property: Tag(7)] long Volume
) : IHasDate;
