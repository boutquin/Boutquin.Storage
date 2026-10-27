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
namespace Boutquin.Storage.Domain.ValueObjects.Streaming;

/// <summary>Associates a stream value with its source or transformation provenance and event time.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="Offset">
/// The contiguous source position for records read from <c>IStream&lt;T&gt;</c>, or a provenance
/// position for transformed join output where duplicates and gaps are permitted.
/// </param>
/// <param name="EventTime">When the event occurred at the source.</param>
/// <param name="Value">The event value.</param>
public sealed record StreamRecord<T>(long Offset, DateTimeOffset EventTime, T Value)
{
    /// <summary>
    /// Gets the identity of the producer that emitted the record, compared ordinally, or
    /// <see langword="null"/> when the stream has a single producer. Event-time consumers that
    /// merge several producers track each producer's completeness threshold under this identity.
    /// </summary>
    public string? Producer { get; init; }
}
