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

namespace Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

/// <summary>
/// Volatile, concurrent-safe in-memory checkpoint store for testing and composition.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 11 — "Stream Processing": checkpointing consumer offsets.</para>
/// </summary>
public sealed class InMemoryCheckpointStore : ICheckpointStore
{
    private readonly ConcurrentDictionary<string, (long Offset, byte[] State)> _store = new();

    /// <inheritdoc />
    public ValueTask SaveAsync(string processorId, long offset, ReadOnlyMemory<byte> state, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        ct.ThrowIfCancellationRequested();
        _store[processorId] = (offset, state.ToArray());
        return default;
    }

    /// <inheritdoc />
    public ValueTask<(long Offset, byte[] State)?> LoadAsync(string processorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        ct.ThrowIfCancellationRequested();

        if (_store.TryGetValue(processorId, out var checkpoint))
        {
            return new ValueTask<(long Offset, byte[] State)?>((checkpoint.Offset, checkpoint.State.ToArray()));
        }

        return new ValueTask<(long Offset, byte[] State)?>(((long, byte[])?)null);
    }
}
