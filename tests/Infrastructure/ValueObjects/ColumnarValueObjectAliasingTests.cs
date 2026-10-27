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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.ValueObjects.TimeSeries;

/// <summary>
/// Pins the <c>byte[]</c> aliasing contract for the two columnar zone-map / bitmap value
/// objects. Both are <c>sealed record</c> types whose value-equality and hash code derive
/// from their <c>byte[]</c> payloads; storing the caller-supplied array by reference would
/// let a post-construction mutation of the caller's array silently change the record's
/// identity (and relocate it in any hash-based collection). The fix is a defensive clone in
/// the constructor AND a clone on the getter, keeping the public <c>byte[]</c> shape.
///
/// This is a cold construction/equality path (zone-map / bitmap build at footer-write time),
/// not a per-row scan — the clone has no per-row allocation impact.
/// </summary>
public sealed class ColumnarValueObjectAliasingTests
{
    // ── AC-2.1: ColumnRowGroupStats ───────────────────────────────────────────

    [Fact]
    public void ColumnRowGroupStats_MutatingCallerMinValueAfterConstruction_DoesNotChangeEqualityOrHashCode()
    {
        var min = new byte[] { 1, 2, 3 };
        var max = new byte[] { 9, 8, 7 };

        var stats = new ColumnRowGroupStats(min, max, NullCount: 0, DistinctCount: 3);
        // A second instance built from independent-but-equal arrays is the equality reference.
        var reference = new ColumnRowGroupStats([1, 2, 3], [9, 8, 7], NullCount: 0, DistinctCount: 3);

        var hashBefore = stats.GetHashCode();
        stats.Equals(reference).Should().BeTrue("equal payloads must be value-equal");

        // Mutate the caller's ORIGINAL array after construction.
        min[0] = 0xFF;

        stats.GetHashCode().Should().Be(hashBefore,
            "the record stored a defensive copy, so mutating the caller's array must not change its hash");
        stats.Equals(reference).Should().BeTrue(
            "the record stored a defensive copy, so mutating the caller's array must not break value-equality");
    }

    [Fact]
    public void ColumnRowGroupStats_MutatingCallerMaxValueAfterConstruction_DoesNotChangeEqualityOrHashCode()
    {
        var min = new byte[] { 1, 2, 3 };
        var max = new byte[] { 9, 8, 7 };

        var stats = new ColumnRowGroupStats(min, max, NullCount: 1, DistinctCount: 2);
        var reference = new ColumnRowGroupStats([1, 2, 3], [9, 8, 7], NullCount: 1, DistinctCount: 2);

        var hashBefore = stats.GetHashCode();

        max[2] = 0x00;

        stats.GetHashCode().Should().Be(hashBefore);
        stats.Equals(reference).Should().BeTrue();
    }

    [Fact]
    public void ColumnRowGroupStats_MutatingTheReturnedGetterArray_DoesNotChangeStoredValue()
    {
        var stats = new ColumnRowGroupStats([1, 2, 3], [9, 8, 7], NullCount: 0, DistinctCount: 3);

        // The getter must hand back a copy, so a consumer mutating it cannot reach into the record.
        stats.MinValue[0] = 0xFF;

        stats.MinValue.Should().Equal(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void ColumnRowGroupStats_TwoInstancesFromEqualArrays_AreStructurallyEqual()
    {
        // Guards that the defensive clone does NOT break record structural equality.
        var a = new ColumnRowGroupStats([4, 5], [6, 7], NullCount: 2, DistinctCount: 4);
        var b = new ColumnRowGroupStats([4, 5], [6, 7], NullCount: 2, DistinctCount: 4);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    // ── AC-2.2: BitmapIndexValue ──────────────────────────────────────────────

    [Fact]
    public void BitmapIndexValue_MutatingCallerValueAfterConstruction_DoesNotChangeEqualityOrHashCode()
    {
        var value = new byte[] { 10, 20 };
        var bitmap = new byte[] { 0xAB, 0xCD };

        var entry = new BitmapIndexValue(value, bitmap);
        var reference = new BitmapIndexValue([10, 20], [0xAB, 0xCD]);

        var hashBefore = entry.GetHashCode();
        entry.Equals(reference).Should().BeTrue();

        value[1] = 0x00;

        entry.GetHashCode().Should().Be(hashBefore,
            "the record stored a defensive copy of Value");
        entry.Equals(reference).Should().BeTrue();
    }

    [Fact]
    public void BitmapIndexValue_MutatingCallerBitmapAfterConstruction_DoesNotChangeEqualityOrHashCode()
    {
        var value = new byte[] { 10, 20 };
        var bitmap = new byte[] { 0xAB, 0xCD };

        var entry = new BitmapIndexValue(value, bitmap);
        var reference = new BitmapIndexValue([10, 20], [0xAB, 0xCD]);

        var hashBefore = entry.GetHashCode();

        bitmap[0] = 0x00;

        entry.GetHashCode().Should().Be(hashBefore,
            "the record stored a defensive copy of Bitmap");
        entry.Equals(reference).Should().BeTrue();
    }

    [Fact]
    public void BitmapIndexValue_MutatingTheReturnedGetterArrays_DoesNotChangeStoredValue()
    {
        var entry = new BitmapIndexValue([10, 20], [0xAB, 0xCD]);

        entry.Value[0] = 0xFF;
        entry.Bitmap[0] = 0xFF;

        entry.Value.Should().Equal(new byte[] { 10, 20 });
        entry.Bitmap.Should().Equal(new byte[] { 0xAB, 0xCD });
    }

    [Fact]
    public void BitmapIndexValue_TwoInstancesFromEqualArrays_AreStructurallyEqual()
    {
        var a = new BitmapIndexValue([1], [2]);
        var b = new BitmapIndexValue([1], [2]);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }
}
