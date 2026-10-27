// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.ValueObjects.TimeSeries;

/// <summary>
/// Pins the <see cref="ColumnSortSpec"/> constructor's validation of the per-column
/// <see cref="SortDirection"/>. The on-disk row order and the generator-emitted comparer
/// branch on the direction enum; an undefined enum value (e.g. <c>(SortDirection)99</c>)
/// would slip past the existing Name validation and silently produce an out-of-contract
/// physical layout. The constructor must reject it fail-fast.
/// </summary>
public sealed class ColumnSortSpecDirectionValidationTests
{
    [Fact]
    public void Constructor_UndefinedSortDirection_Throws()
    {
        var bad = new (string Name, SortDirection Direction)[]
        {
            ("Date", (SortDirection)99),
        };

        var act = () => new ColumnSortSpec(bad);

        act.Should().Throw<ArgumentException>(
            "an undefined SortDirection must be rejected fail-fast, not encoded into the segment layout");
    }

    [Fact]
    public void Constructor_UndefinedSortDirection_OnLaterColumn_Throws()
    {
        var bad = new (string Name, SortDirection Direction)[]
        {
            ("Date", SortDirection.Ascending),
            ("Ticker", (SortDirection)255),
        };

        var act = () => new ColumnSortSpec(bad);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(SortDirection.Ascending)]
    [InlineData(SortDirection.Descending)]
    public void Constructor_DefinedSortDirection_IsAccepted(SortDirection direction)
    {
        var act = () => new ColumnSortSpec([("Date", direction)]);

        act.Should().NotThrow();
    }

    [Fact]
    public void Columns_CannotBeMutatedThroughTheExposedList()
    {
        // The validated sort order is load-bearing for on-disk segment layout; the list a spec
        // exposes must not be a writable alias of its backing store. A fresh spec is used (not the
        // shared DateAscending default) so a failing run cannot poison other tests.
        var spec = new ColumnSortSpec([("Date", SortDirection.Ascending)]);

        var act = () => ((IList<(string Name, SortDirection Direction)>)spec.Columns)[0] =
            ("Close", SortDirection.Descending);

        act.Should().Throw<NotSupportedException>("the exposed column list must be read-only");
        spec.Columns[0].Should().Be(("Date", SortDirection.Ascending));
    }

    [Fact]
    public void DateAscending_ExposesReadOnlyColumns()
    {
        ColumnSortSpec.DateAscending.Columns.Should().BeAssignableTo<System.Collections.ObjectModel.ReadOnlyCollection<(string Name, SortDirection Direction)>>(
            "the process-wide default spec must not hand out a writable array");
        ColumnSortSpec.DateAscending.Columns.Should().Equal([("Date", SortDirection.Ascending)]);
    }
}
