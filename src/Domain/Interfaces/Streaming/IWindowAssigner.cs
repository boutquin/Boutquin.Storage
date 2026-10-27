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
using Boutquin.Storage.Domain.ValueObjects.Streaming;

namespace Boutquin.Storage.Domain.Interfaces.Streaming;

/// <summary>Maps an event-time timestamp to one or more half-open windows.</summary>
public interface IWindowAssigner
{
    /// <summary>Gets the inactivity gap for session windows, or <see langword="null"/> otherwise.</summary>
    TimeSpan? SessionGap { get; }

    /// <summary>Returns the windows containing the event timestamp.</summary>
    /// <param name="eventTime">The event timestamp to assign.</param>
    /// <returns>One or more UTC, half-open windows containing the timestamp.</returns>
    IReadOnlyList<TimeWindow> AssignWindows(DateTimeOffset eventTime);
}
