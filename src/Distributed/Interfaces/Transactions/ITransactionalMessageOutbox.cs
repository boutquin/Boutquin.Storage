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
using Boutquin.Storage.Distributed.Transactions;
namespace Boutquin.Storage.Distributed.Interfaces.Transactions;

/// <summary>Commits business state and outbound messages atomically in one local durable record.</summary>
/// <remarks>Delivery is at least once: acknowledge only after the receiver commits its deduplicated effect.
/// A crash after delivery but before acknowledgement deliberately causes redelivery.</remarks>
public interface ITransactionalMessageOutbox
{
    /// <summary>Commits the business changes, messages, identity and response together.</summary>
    /// <param name="id">Stable request identity.</param><param name="intent">Nonblank semantic intent, including command version when relevant.</param><param name="mutation">Owned immutable local effects.</param><param name="ct">Cancellation token; durable effects may already exist on failure.</param>
    /// <returns>The cached response after fsync; identical retry returns the original response.</returns>
    /// <exception cref="ArgumentException">An identity, key, intent or plan violates its documented constraints.</exception>
    /// <exception cref="ObjectDisposedException">The concrete state machine has been disposed.</exception>
    /// <exception cref="IOException">Storage or participant delivery failed; a durable effect may already exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed; an admitted durable effect may already exist.</exception>
    Task<string> CommitAsync(RequestId id, string intent, MessageMutation mutation, CancellationToken ct = default);
    /// <summary>Snapshots all unacknowledged messages in commit and within-batch order.</summary>
    /// <param name="ct">Read cancellation token.</param><returns>An owned snapshot, empty when no delivery remains.</returns>
    /// <exception cref="ArgumentException">An identity, key, intent or plan violates its documented constraints.</exception>
    /// <exception cref="ObjectDisposedException">The concrete state machine has been disposed.</exception>
    /// <exception cref="IOException">Storage or participant delivery failed; a durable effect may already exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed; an admitted durable effect may already exist.</exception>
    Task<IReadOnlyList<OutboxMessage>> PendingMessagesAsync(CancellationToken ct = default);
    /// <summary>Durably records receiver acknowledgement; does not itself deliver the message.</summary>
    /// <param name="messageId">Known outbound identity.</param><param name="ct">Cancellation token; acknowledgement can already be durable.</param>
    /// <returns>Completes after fsync, or immediately for an already acknowledged identity.</returns>
    /// <exception cref="KeyNotFoundException">No outbound message has that identity.</exception>
    /// <exception cref="ArgumentException">An identity, key, intent or plan violates its documented constraints.</exception>
    /// <exception cref="ObjectDisposedException">The concrete state machine has been disposed.</exception>
    /// <exception cref="IOException">Storage or participant delivery failed; a durable effect may already exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed; an admitted durable effect may already exist.</exception>
    Task AcknowledgeAsync(RequestId messageId, CancellationToken ct = default);
}
