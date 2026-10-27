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

/// <summary>Assigns each event to one fixed-size, non-overlapping UTC window.</summary>
/// <remarks>Assignment takes constant time and returns one half-open interval.</remarks>
public sealed class TumblingWindow : IWindowAssigner
{
    private readonly long _sizeTicks;

    /// <summary>Creates a tumbling window assigner.</summary>
    /// <param name="size">The positive width of each aligned UTC window.</param>
    /// <exception cref="ArgumentOutOfRangeException">The size is zero or negative.</exception>
    public TumblingWindow(TimeSpan size)
    {
        if (size <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Window size must be positive.");
        }

        _sizeTicks = size.Ticks;
    }

    /// <inheritdoc />
    public TimeSpan? SessionGap => null;

    /// <inheritdoc />
    public IReadOnlyList<TimeWindow> AssignWindows(DateTimeOffset eventTime)
    {
        var utcTicks = eventTime.UtcTicks;
        var startTicks = utcTicks - (utcTicks % _sizeTicks);
        var start = new DateTimeOffset(startTicks, TimeSpan.Zero);
        return [new TimeWindow(start, start.AddTicks(_sizeTicks))];
    }
}
