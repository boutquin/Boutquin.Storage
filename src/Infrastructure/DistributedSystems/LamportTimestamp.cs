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
namespace Boutquin.Storage.Infrastructure.DistributedSystems;

/// <summary>
/// A Lamport logical timestamp implementation providing total ordering of events across distributed nodes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread safety:</b> All counter operations use <see cref="Interlocked"/> for lock-free thread safety.
/// </para>
/// </remarks>
public sealed class LamportTimestamp : ILamportTimestamp
{
    private long _counter;

    /// <summary>
    /// Initializes a new instance of the <see cref="LamportTimestamp"/> class.
    /// </summary>
    /// <param name="nodeId">The unique node identifier used for tie-breaking.</param>
    /// <param name="initialCounter">The initial counter value. Defaults to 0.</param>
    public LamportTimestamp(string nodeId, long initialCounter = 0)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        NodeId = nodeId;
        _counter = initialCounter;
    }

    /// <inheritdoc />
    public string NodeId { get; }

    /// <inheritdoc />
    public long GetCurrentTimestamp() => Interlocked.Read(ref _counter);

    /// <inheritdoc />
    public long Increment() => Interlocked.Increment(ref _counter);

    /// <inheritdoc />
    public long Update(long receivedTimestamp)
    {
        // Atomically set counter to max(local, received) + 1
        // Use a compare-and-swap loop for thread safety
        while (true)
        {
            var current = Interlocked.Read(ref _counter);
            var newValue = Math.Max(current, receivedTimestamp) + 1;

            if (Interlocked.CompareExchange(ref _counter, newValue, current) == current)
            {
                return newValue;
            }
        }
    }

    /// <inheritdoc />
    public int CompareTo(long otherTimestamp, string otherNodeId)
    {
        var myTimestamp = GetCurrentTimestamp();
        var timestampComparison = myTimestamp.CompareTo(otherTimestamp);

        return timestampComparison != 0
            ? timestampComparison
            : string.Compare(NodeId, otherNodeId, StringComparison.Ordinal);
    }
}
