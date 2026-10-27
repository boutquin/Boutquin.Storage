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

/// <summary>Provides replayable, offset-addressed event-time records.</summary>
/// <typeparam name="T">The value associated with each source position and event timestamp.</typeparam>
public interface IStream<T>
{
    /// <summary>Reads records from the inclusive offset in source order.</summary>
    /// <param name="offset">The first zero-based source position to enumerate.</param>
    /// <param name="ct">Cancels enumeration between records.</param>
    /// <returns>A replayable sequence ordered by a contiguous, increasing source offset.</returns>
    IAsyncEnumerable<StreamRecord<T>> ReadAsync(long offset = 0, CancellationToken ct = default);
}
