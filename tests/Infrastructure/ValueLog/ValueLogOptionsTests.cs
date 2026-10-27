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

using Boutquin.Storage.Domain.ValueObjects.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests.ValueLog;

public sealed class ValueLogOptionsTests
{
    // ───── P3.7.3 ValueLogThresholdBytes lower-bound guard ────────────────

    [Fact]
    public void ValueLogOptions_ThresholdAt64_DoesNotThrow()
    {
        var act = () => new ValueLogOptions(ValueLogThresholdBytes: 64);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValueLogOptions_ThresholdAbove64_DoesNotThrow()
    {
        var act = () => new ValueLogOptions(ValueLogThresholdBytes: 1024);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValueLogOptions_ThresholdAt63_ThrowsArgumentOutOfRange()
    {
        var act = () => new ValueLogOptions(ValueLogThresholdBytes: 63);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("ValueLogThresholdBytes");
    }

    [Fact]
    public void ValueLogOptions_ThresholdAtZero_ThrowsArgumentOutOfRange()
    {
        var act = () => new ValueLogOptions(ValueLogThresholdBytes: 0);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("ValueLogThresholdBytes");
    }

    [Fact]
    public void ValueLogOptions_ThresholdNegative_ThrowsArgumentOutOfRange()
    {
        var act = () => new ValueLogOptions(ValueLogThresholdBytes: -1);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("ValueLogThresholdBytes");
    }

    [Fact]
    public void ValueLogOptions_Default_HasValidThreshold()
    {
        ValueLogOptions.Default.ValueLogThresholdBytes.Should().BeGreaterThanOrEqualTo(64);
    }

    // ───── ReadHandleCacheSize / MaxEntryBytes / SegmentMaxBytes guards ─────
    // Symmetric coverage with ValueLogThresholdBytes — the three other custom
    // setters validate non-positive inputs the same way.

    [Fact]
    public void ValueLogOptions_ReadHandleCacheSizeZero_ThrowsArgumentOutOfRange()
    {
        var act = () => new ValueLogOptions(ReadHandleCacheSize: 0);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("ReadHandleCacheSize");
    }

    [Fact]
    public void ValueLogOptions_ReadHandleCacheSizeNegative_ThrowsArgumentOutOfRange()
    {
        var act = () => new ValueLogOptions(ReadHandleCacheSize: -1);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("ReadHandleCacheSize");
    }

    [Fact]
    public void ValueLogOptions_MaxEntryBytesZero_ThrowsArgumentOutOfRange()
    {
        var act = () => new ValueLogOptions(MaxEntryBytes: 0);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("MaxEntryBytes");
    }

    [Fact]
    public void ValueLogOptions_MaxEntryBytesNegative_ThrowsArgumentOutOfRange()
    {
        var act = () => new ValueLogOptions(MaxEntryBytes: -1);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("MaxEntryBytes");
    }

    [Fact]
    public void ValueLogOptions_SegmentMaxBytesZero_ThrowsArgumentOutOfRange()
    {
        var act = () => new ValueLogOptions(SegmentMaxBytes: 0);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("SegmentMaxBytes");
    }

    [Fact]
    public void ValueLogOptions_SegmentMaxBytesNegative_ThrowsArgumentOutOfRange()
    {
        var act = () => new ValueLogOptions(SegmentMaxBytes: -1);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("SegmentMaxBytes");
    }

    // ───── Bounds hold on every construction path (with-expression, object initializer) ─────

    [Fact]
    public void ValueLogOptions_WithThresholdBelowMinimum_ThrowsArgumentOutOfRange()
    {
        var act = () => ValueLogOptions.Default with { ValueLogThresholdBytes = 1 };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("ValueLogThresholdBytes");
    }

    [Fact]
    public void ValueLogOptions_ObjectInitializerThresholdBelowMinimum_ThrowsArgumentOutOfRange()
    {
        var act = () => new ValueLogOptions { ValueLogThresholdBytes = 10 };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("ValueLogThresholdBytes");
    }

    [Fact]
    public void ValueLogOptions_WithReadHandleCacheSizeZero_ThrowsArgumentOutOfRange()
    {
        var act = () => ValueLogOptions.Default with { ReadHandleCacheSize = 0 };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("ReadHandleCacheSize");
    }

    [Fact]
    public void ValueLogOptions_WithMaxEntryBytesZero_ThrowsArgumentOutOfRange()
    {
        var act = () => ValueLogOptions.Default with { MaxEntryBytes = 0 };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("MaxEntryBytes");
    }

    [Fact]
    public void ValueLogOptions_WithSegmentMaxBytesNegative_ThrowsArgumentOutOfRange()
    {
        var act = () => ValueLogOptions.Default with { SegmentMaxBytes = -5 };
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("SegmentMaxBytes");
    }

    [Fact]
    public void ValueLogOptions_WithValidValues_AppliesThem()
    {
        var options = ValueLogOptions.Default with
        {
            ValueLogThresholdBytes = 64,
            ReadHandleCacheSize = 1,
            MaxEntryBytes = 1,
            SegmentMaxBytes = 1,
        };

        options.ValueLogThresholdBytes.Should().Be(64);
        options.ReadHandleCacheSize.Should().Be(1);
        options.MaxEntryBytes.Should().Be(1);
        options.SegmentMaxBytes.Should().Be(1);
        options.Should().Be(new ValueLogOptions(64, 1, 1, 1));
    }
}
