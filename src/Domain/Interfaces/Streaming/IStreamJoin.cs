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

/// <summary>Joins two event-time streams.</summary>
/// <typeparam name="TLeft">The left input value type.</typeparam>
/// <typeparam name="TRight">The right input value type.</typeparam>
/// <typeparam name="TOut">The joined output value type.</typeparam>
public interface IStreamJoin<TLeft, TRight, TOut>
{
    /// <summary>Joins the two finite input streams and returns results in event-time order.</summary>
    /// <param name="left">The finite left input.</param>
    /// <param name="right">The finite right input.</param>
    /// <param name="ct">Cancels input buffering or output enumeration.</param>
    /// <returns>Joined records whose offsets retain source provenance and need not be contiguous.</returns>
    IAsyncEnumerable<StreamRecord<TOut>> JoinAsync(
        IAsyncEnumerable<StreamRecord<TLeft>> left,
        IAsyncEnumerable<StreamRecord<TRight>> right,
        CancellationToken ct = default);
}
