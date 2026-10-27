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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.ValueObjects;

public sealed class TimeSeriesKeyOrderingTests
{
    private static TimeSeriesKey<SerializableWrapper<string>> K(string dataset, int y, int m, int d)
        => new(new SerializableWrapper<string>(dataset), new DateOnly(y, m, d));

    [Fact]
    public void CompareTo_DifferentDatasets_OrdersByDataset()
    {
        var a = K("AAPL", 2020, 1, 1);
        var b = K("MSFT", 2000, 1, 1);

        a.CompareTo(b).Should().BeNegative();
        b.CompareTo(a).Should().BePositive();
    }

    [Fact]
    public void CompareTo_SameDataset_OrdersByDate()
    {
        var a = K("AAPL", 2020, 1, 1);
        var b = K("AAPL", 2020, 1, 2);

        a.CompareTo(b).Should().BeNegative();
        b.CompareTo(a).Should().BePositive();
    }

    [Fact]
    public void CompareTo_SameKey_IsZero()
    {
        var a = K("AAPL", 2020, 1, 1);
        var b = K("AAPL", 2020, 1, 1);

        a.CompareTo(b).Should().Be(0);
    }

    [Fact]
    public void SortingMixedKeys_ContiguousPerDataset_DateAscending()
    {
        var keys = new[]
        {
            K("MSFT", 2020, 3, 1),
            K("AAPL", 2020, 1, 2),
            K("MSFT", 2020, 1, 1),
            K("AAPL", 2020, 1, 1),
        };

        Array.Sort(keys);

        keys[0].Dataset.Value.Should().Be("AAPL");
        keys[0].Date.Should().Be(new DateOnly(2020, 1, 1));
        keys[1].Dataset.Value.Should().Be("AAPL");
        keys[1].Date.Should().Be(new DateOnly(2020, 1, 2));
        keys[2].Dataset.Value.Should().Be("MSFT");
        keys[2].Date.Should().Be(new DateOnly(2020, 1, 1));
        keys[3].Dataset.Value.Should().Be("MSFT");
        keys[3].Date.Should().Be(new DateOnly(2020, 3, 1));
    }
}
