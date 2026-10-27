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
using System.Threading.Channels;

namespace Boutquin.Storage.Distributed.Coordination;

/// <summary>An owned path subscription with replayable committed change cursors.</summary>
/// <remarks>The channel is unbounded so events are not silently dropped; slow consumers retain memory.
/// Dispose on the serialized simulation driver. A watch is local to one replica and may stall during
/// partition; resume on a reachable leader with the last consumed revision. No callback runs in Raft handlers.</remarks>
public sealed class CoordinationWatch : IDisposable
{
    private readonly Channel<CoordinationEvent> _channel = Channel.CreateUnbounded<CoordinationEvent>(
        new UnboundedChannelOptions { SingleWriter = true, AllowSynchronousContinuations = false });
    private readonly Action<CoordinationWatch> _remove;
    private long _afterRevision;
    private bool _disposed;

    internal CoordinationWatch(string path, long afterRevision, Action<CoordinationWatch> remove)
    {
        Path = path;
        _afterRevision = afterRevision;
        _remove = remove;
    }

    internal string Path { get; }

    /// <summary>Reads the next queued committed event without blocking.</summary>
    /// <param name="change">Event when true; null when the queue is empty.</param>
    /// <returns>Whether an event was dequeued.</returns>
    /// <exception cref="ObjectDisposedException">The watch was disposed.</exception>
    public bool TryRead(out CoordinationEvent? change)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _channel.Reader.TryRead(out change);
    }

    /// <summary>Enumerates committed events until cancellation or subscription disposal.</summary>
    /// <param name="ct">Cancels enumeration without disposing the watch.</param>
    /// <returns>An asynchronous stream; dispose the subscription when no longer needed.</returns>
    /// <exception cref="ObjectDisposedException">The watch was disposed before enumeration.</exception>
    public IAsyncEnumerable<CoordinationEvent> ReadAllAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _channel.Reader.ReadAllAsync(ct);
    }

    internal void Publish(CoordinationEvent change)
    {
        if (change.Revision > _afterRevision)
        {
            _channel.Writer.TryWrite(change);
            _afterRevision = change.Revision;
        }
    }

    /// <summary>Unregisters the subscription and completes waiting readers; repeated disposal is harmless.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _remove(this);
        _channel.Writer.TryComplete();
    }
}
