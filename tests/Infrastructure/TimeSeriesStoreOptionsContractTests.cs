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
using Boutquin.Storage.Infrastructure.Tests.TimeSeries;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the public contract of the time-series engine options: every public member they expose
/// is one the engine actually consults. Row-group sizing and the per-block sort order are owned
/// by the columnar format constructors (<c>CompressedColumnarSegmentFormat</c>,
/// <c>ColumnarSegmentFormat</c>) and by <c>ColumnarTimeSeriesEngineBuilder.BuildAsync</c>; an
/// options property that merely mirrored them would advertise an effect that never happens.
/// </summary>
public sealed class TimeSeriesStoreOptionsContractTests
{
    private static string[] PublicInstancePropertyNames(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void BaseOptions_ExposeOnlyMembersTheEngineConsults()
    {
        PublicInstancePropertyNames(typeof(TimeSeriesStoreOptions<TimeSeriesTestRecord>))
            .Should().Equal(
                ["IsColumnarFormatSupplied", "SegmentFormat"],
                "LsmTimeSeriesStorageEngine reads only the segment format and whether a columnar format was supplied; any other knob would have no runtime effect");
    }

    [Fact]
    public void ColumnarOptions_ExposeOnlyMembersTheEngineConsults()
    {
        PublicInstancePropertyNames(typeof(ColumnarTimeSeriesStoreOptions<MarketDataBar>))
            .Should().Equal(
                ["ColumnarFormat", "IsColumnarFormatSupplied", "SegmentFormat"],
                "row-group sizing and sort order are configured on the columnar format constructors, not on the options");
    }
}
