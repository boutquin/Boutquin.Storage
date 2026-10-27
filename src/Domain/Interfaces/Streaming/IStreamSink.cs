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

/// <summary>Receives processor output without duplicate suppression.</summary>
/// <typeparam name="T">The contravariant output value type.</typeparam>
public interface IStreamSink<in T>
{
    /// <summary>Writes one output value.</summary>
    /// <param name="value">The output value.</param>
    /// <param name="ct">Cancels the write before it completes.</param>
    /// <returns>A task that completes when the sink acknowledges the write.</returns>
    ValueTask WriteAsync(T value, CancellationToken ct = default);
}

/// <summary>Receives processor output with an idempotency key.</summary>
/// <typeparam name="T">The contravariant output value type.</typeparam>
public interface IIdempotentStreamSink<in T>
{
    /// <summary>Writes one output at most once per operation id.</summary>
    /// <param name="operationId">A stable identifier reused by every retry of the same logical output.</param>
    /// <param name="value">The output value.</param>
    /// <param name="ct">Cancels the write before it completes.</param>
    /// <returns>A task that completes when the sink accepts or suppresses the operation.</returns>
    ValueTask WriteAsync(string operationId, T value, CancellationToken ct = default);
}
