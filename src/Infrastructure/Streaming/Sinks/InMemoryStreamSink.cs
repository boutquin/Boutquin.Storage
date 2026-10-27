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

namespace Boutquin.Storage.Infrastructure.Streaming.Sinks;

/// <summary>Collects every write, including retries, in arrival order.</summary>
/// <typeparam name="T">The collected output type.</typeparam>
/// <remarks>Thread-safe but non-durable; process exit loses all output.</remarks>
public sealed class InMemoryStreamSink<T> : IStreamSink<T>
{
    private readonly Lock _gate = new();
    private readonly List<T> _outputs = [];

    /// <summary>Gets a point-in-time copy of collected output.</summary>
    public IReadOnlyList<T> Outputs
    {
        get
        {
            lock (_gate)
            {
                return _outputs.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(T value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _outputs.Add(value);
        }

        return ValueTask.CompletedTask;
    }
}
