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

/// <summary>Advances a monotonic event-time completeness boundary.</summary>
/// <remarks>Instances are stateful. Implementations document their synchronization requirements.</remarks>
public interface IWatermarkPolicy
{
    /// <summary>Gets the current watermark.</summary>
    Watermark Current { get; }

    /// <summary>Observes an event timestamp and returns the resulting watermark.</summary>
    /// <param name="eventTime">The source event timestamp.</param>
    /// <returns>The monotonic boundary after observing the timestamp.</returns>
    Watermark Observe(DateTimeOffset eventTime);

    /// <summary>Resets policy state for a new stream execution.</summary>
    void Reset();
}
