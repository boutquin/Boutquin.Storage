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

namespace Boutquin.Storage.Infrastructure.Streaming.Windows;

/// <summary>Assigns each event to every fixed-size window whose slide places it inside.</summary>
/// <remarks>Assignment takes O(size / slide) time and returns at most 10,000 windows.</remarks>
public sealed class SlidingWindow : IWindowAssigner
{
    private const int MaxWindowsPerEvent = 10_000;
    private readonly long _sizeTicks;
    private readonly long _slideTicks;

    /// <summary>Creates a sliding window assigner.</summary>
    /// <param name="size">The positive width of each window.</param>
    /// <param name="slide">The positive distance between starts, no larger than <paramref name="size"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A duration is invalid or creates more than 10,000 windows per event.</exception>
    public SlidingWindow(TimeSpan size, TimeSpan slide)
    {
        if (size <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Window size must be positive.");
        }

        if (slide <= TimeSpan.Zero || slide > size)
        {
            throw new ArgumentOutOfRangeException(nameof(slide), "Slide must be positive and no larger than size.");
        }

        var windowsPerEvent = checked((size.Ticks + slide.Ticks - 1) / slide.Ticks);
        if (windowsPerEvent > MaxWindowsPerEvent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slide), $"A single event may belong to at most {MaxWindowsPerEvent} windows.");
        }

        _sizeTicks = size.Ticks;
        _slideTicks = slide.Ticks;
    }

    /// <inheritdoc />
    public TimeSpan? SessionGap => null;

    /// <inheritdoc />
    public IReadOnlyList<TimeWindow> AssignWindows(DateTimeOffset eventTime)
    {
        var utcTicks = eventTime.UtcTicks;
        var lastStart = utcTicks - (utcTicks % _slideTicks);
        var starts = new List<long>();
        for (var start = lastStart; start + _sizeTicks > utcTicks; start -= _slideTicks)
        {
            starts.Add(start);
            if (start < _slideTicks)
            {
                break;
            }
        }

        starts.Reverse();
        return starts
            .Select(start => new TimeWindow(
                new DateTimeOffset(start, TimeSpan.Zero),
                new DateTimeOffset(start + _sizeTicks, TimeSpan.Zero)))
            .ToArray();
    }
}
