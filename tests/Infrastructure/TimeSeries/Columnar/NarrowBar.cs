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
/// Minimal two-column record used in fix-5 tests: writing a <see cref="ColumnarBar"/>
/// segment and reading with a <c>NarrowBar</c> format produces a footer that declares
/// columns the narrow format's plan does not recognise, triggering
/// <see cref="Boutquin.Storage.Domain.Exceptions.ColumnNotFoundException"/>.
/// </summary>
[ColumnarStorable]
public partial record NarrowBar : IHasDate
{
    public DateOnly Date { get; init; }
    public string Ticker { get; init; } = "";
    // Intentionally omits Close, Volume, TradeCount, Vwap, IsAdjusted, Residual.
}
