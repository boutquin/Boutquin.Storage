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
using Boutquin.Storage.Domain.Interfaces.Streaming;
using Boutquin.Storage.Domain.ValueObjects.Streaming;

namespace Boutquin.Storage.Infrastructure.Streaming.Watermarks;

/// <summary>Tracks maximum observed event time minus a configured out-of-order allowance.</summary>
/// <remarks>
/// Externally synchronized: a caller must serialize <see cref="Observe"/> and <see cref="Reset"/>.
/// The watermark advances monotonically between resets.
/// </remarks>
public sealed class BoundedOutOfOrdernessWatermarkPolicy : IWatermarkPolicy
{
    private readonly TimeSpan _maximumOutOfOrderness;
    private DateTimeOffset? _maximumObserved;

    /// <summary>Creates an inert watermark policy at <see cref="DateTimeOffset.MinValue"/>.</summary>
    /// <param name="maximumOutOfOrderness">The non-negative delay subtracted from the maximum timestamp.</param>
    /// <exception cref="ArgumentOutOfRangeException">The allowance is negative.</exception>
    public BoundedOutOfOrdernessWatermarkPolicy(TimeSpan maximumOutOfOrderness)
    {
        if (maximumOutOfOrderness < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumOutOfOrderness), "Out-of-order allowance cannot be negative.");
        }

        _maximumOutOfOrderness = maximumOutOfOrderness;
        Current = new Watermark(DateTimeOffset.MinValue);
    }

    /// <inheritdoc />
    public Watermark Current { get; private set; }

    /// <inheritdoc />
    public Watermark Observe(DateTimeOffset eventTime)
    {
        var utc = eventTime.ToUniversalTime();
        if (_maximumObserved is null || utc > _maximumObserved.Value)
        {
            _maximumObserved = utc;
            var ticks = Math.Max(DateTimeOffset.MinValue.Ticks, utc.Ticks - _maximumOutOfOrderness.Ticks);
            Current = new Watermark(new DateTimeOffset(ticks, TimeSpan.Zero));
        }

        return Current;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _maximumObserved = null;
        Current = new Watermark(DateTimeOffset.MinValue);
    }
}
