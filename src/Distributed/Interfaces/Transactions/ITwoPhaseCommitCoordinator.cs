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

/// <summary>Orders durable intent, participant votes, a durable decision, and idempotent decision delivery.</summary>
/// <remarks>Provides atomic final outcomes, not simultaneous visibility across independently read shards.
/// A prepare that fails or times out is a no vote: the plan is durably aborted and abort is sent to every participant.
/// After the decision is logged, a failed delivery is retried with bounded backoff until it succeeds or the caller cancels,
/// when a reopen hook is supplied; without one the call fails with <see cref="TransactionDeliveryException"/> and the decision
/// stays durable. A canceled call can be undecided or decided; recovery or identical retry resolves uncertainty.</remarks>
public interface ITwoPhaseCommitCoordinator
{
    /// <summary>Commits all prepared participants or durably aborts the entire plan.</summary>
    /// <param name="id">Globally unique stable transaction identity.</param><param name="plan">Nonempty plan with unique registered resources.</param>
    /// <param name="reopen">Optional caller-owned hook, invoked under the coordinator's serialization (one call at a time) after a failed delivery with the
    /// participant that failed; it returns the participant (same identity) to retry against, reopening one that poisoned itself or
    /// returning the same instance. The result replaces the registration. It must not call back into the coordinator.
    /// Null disables in-call retry.</param>
    /// <param name="ct">Cancellation token; the only way to stop delivery retries. Already logged decisions are irrevocable.</param>
    /// <returns>The final decision once every participant acknowledges it.</returns>
    /// <exception cref="InvalidOperationException">The ID was already bound to different intent, or the reopen hook returned another identity.</exception>
    /// <exception cref="TransactionDeliveryException">No reopen hook was supplied and the durable decision did not reach every participant.</exception>
    /// <exception cref="ArgumentException">An identity, key, intent or plan violates its documented constraints.</exception>
    /// <exception cref="ObjectDisposedException">The concrete state machine has been disposed.</exception>
    /// <exception cref="IOException">Storage or participant delivery failed; a durable effect may already exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed; an admitted durable effect may already exist.</exception>
    Task<CommitResult> ExecuteAsync(RequestId id, IReadOnlyList<TransactionIntent> plan, Func<ITransactionalResource, CancellationToken, Task<ITransactionalResource>>? reopen = null, CancellationToken ct = default);
    /// <summary>Durably aborts every undecided start, then redelivers every recorded decision.</summary>
    /// <remarks>Every transaction is attempted even when an earlier one cannot be delivered, so one unreachable participant
    /// does not strand other transactions' prepared locks: participants are driven independently, and within this pass one that
    /// fails or never answers holds back only its own decisions. The call then completes once that participant answers or the caller
    /// cancels; a canceled pass leaves the remaining decisions to the next recovery. The concrete coordinator is serialized, so a
    /// pass stuck on such a participant blocks every other ExecuteAsync and RecoverAsync call on the coordinator until it completes
    /// or its caller cancels. Cancellation reaches the pass only through the participant: the coordinator forwards the token and
    /// waits for the participant call to return, so a participant that ignores cancellation holds the coordinator until it answers.
    /// Recovery takes no reopen hook and sends to the registered participant
    /// instances: one that poisoned itself is reached again only after a reopen hook passed to ExecuteAsync rebinds it, or through
    /// a coordinator created with its reopened instance.</remarks>
    /// <param name="ct">Cancellation token; a partial recovery is safely repeatable.</param>
    /// <returns>Completes after all known participants acknowledge their durable decisions; it does not retry in-call.</returns>
    /// <exception cref="ArgumentException">An identity, key, intent or plan violates its documented constraints.</exception>
    /// <exception cref="ObjectDisposedException">The concrete state machine has been disposed.</exception>
    /// <exception cref="TransactionDeliveryException">After every transaction was attempted, exactly one recorded decision did not
    /// reach every participant of its transaction.</exception>
    /// <exception cref="AggregateException">After every transaction was attempted, several recorded decisions were undelivered;
    /// each inner exception is a <see cref="TransactionDeliveryException"/>, in journal order.</exception>
    /// <exception cref="IOException">Storage or participant delivery failed; a durable effect may already exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed; an admitted durable effect may already exist.</exception>
    Task RecoverAsync(CancellationToken ct = default);
}
