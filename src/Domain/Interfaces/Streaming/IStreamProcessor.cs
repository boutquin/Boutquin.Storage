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
namespace Boutquin.Storage.Domain.Interfaces.Streaming;

/// <summary>Transforms one asynchronous stream into another in a single process.</summary>
/// <typeparam name="TIn">The contravariant input item type.</typeparam>
/// <typeparam name="TOut">The covariant output item type.</typeparam>
public interface IStreamProcessor<in TIn, out TOut>
{
    /// <summary>Processes input items as they arrive.</summary>
    /// <param name="input">The asynchronous input sequence.</param>
    /// <param name="ct">Cancels processing between input or output items.</param>
    /// <returns>The transformed output sequence.</returns>
    IAsyncEnumerable<TOut> ProcessAsync(IAsyncEnumerable<TIn> input, CancellationToken ct = default);
}
