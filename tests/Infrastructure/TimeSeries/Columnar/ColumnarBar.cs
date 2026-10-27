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
/// Realistic daily-bar test record that exercises every codec in the Phase-3 family:
/// <c>DeltaOfDeltaVlqCodec</c> on Date, <c>DeltaVlqCodec</c> on Volume and
/// TradeCount, <c>DictionaryCodec</c> on Close and Ticker, <c>GorillaXorCodec</c>
/// on Vwap, <c>BitmapRleCodec</c> on IsAdjusted, plus a <see cref="ColumnEncoding.RawDouble"/>
/// override on Residual to verify the raw-IEEE-754 opt-out path for high-entropy doubles.
/// </summary>
[ColumnarStorable]
public partial record ColumnarBar : IHasDate
{
    public DateOnly Date { get; init; }
    public string Ticker { get; init; } = "";
    public decimal Close { get; init; }
    public long Volume { get; init; }
    public int TradeCount { get; init; }
    public double Vwap { get; init; }
    public bool IsAdjusted { get; init; }

    [ColumnEncoding(ColumnEncoding.RawDouble)]
    public double Residual { get; init; }

    public ColumnarBar() { }
}
