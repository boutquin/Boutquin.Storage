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

/// <summary>Assigns provisional windows that a stateful aggregator merges by inactivity gap.</summary>
/// <remarks>Assignment takes constant time; keyed session merging is performed by the processor.</remarks>
public sealed class SessionWindow : IWindowAssigner
{
    /// <summary>Creates a session window assigner.</summary>
    /// <param name="inactivityGap">The positive quiet period that closes a session.</param>
    /// <exception cref="ArgumentOutOfRangeException">The gap is zero or negative.</exception>
    public SessionWindow(TimeSpan inactivityGap)
    {
        if (inactivityGap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(inactivityGap), "Session gap must be positive.");
        }

        SessionGap = inactivityGap;
    }

    /// <inheritdoc />
    public TimeSpan? SessionGap { get; }

    /// <inheritdoc />
    public IReadOnlyList<TimeWindow> AssignWindows(DateTimeOffset eventTime)
    {
        var utc = eventTime.ToUniversalTime();
        return [new TimeWindow(utc, utc.Add(SessionGap!.Value))];
    }
}
