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
using Boutquin.Storage.Distributed.Interfaces.Coordination;
using Boutquin.Storage.Infrastructure.Schemas;

namespace Boutquin.Storage.Distributed.Coordination;

/// <summary>A typed JSON register over the coordination service's quorum-ordered path operations.</summary>
/// <typeparam name="TKey">Immutable key with stable, distinct default System.Text.Json encoding.</typeparam>
/// <typeparam name="TValue">Non-null value that default System.Text.Json can round-trip exactly.</typeparam>
/// <remarks>Keys are hex-encoded UTF-8 JSON under an exclusive namespace prefix. Do not mix register types
/// under the same prefix or use keys whose serialization changes over their lifetime. Values are serialized
/// before admission, so later caller mutation cannot change an enqueued command. CAS compares revisions,
/// avoiding value-equality and ABA ambiguity. The caller retains ownership of the underlying service.
/// Keys and values that decode differently (including unpaired UTF-16 surrogates or object-declared
/// scalars that decode as JsonElement) are refused with ArgumentException before admission.
/// Each typed result completes inside the service's own completion, on the thread that completes it (the
/// virtual-time driver for the Raft-backed service), so a host that drives time until a task reports
/// completion advances the same number of ticks on every seeded run. Continuations on the returned task
/// still run asynchronously, as they do on the service's task.</remarks>
public sealed class LinearizableRegister<TKey, TValue> : ILinearizableRegister<TKey, TValue>
    where TKey : notnull where TValue : notnull
{
    private readonly ICoordinationService _service;
    private readonly string _prefix;

    /// <summary>Creates an inert typed facade under a dedicated absolute prefix.</summary>
    /// <param name="service">Borrowed non-null coordination service.</param>
    /// <param name="prefix">Nonblank absolute namespace, used exclusively for this key/value encoding.</param>
    /// <exception cref="ArgumentNullException">Service or prefix is null.</exception>
    /// <exception cref="ArgumentException">Prefix is blank or not absolute.</exception>
    public LinearizableRegister(ICoordinationService service, string prefix)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (!prefix.StartsWith('/'))
        {
            throw new ArgumentException("Register prefix must be absolute.", nameof(prefix));
        }
        _service = service;
        _prefix = prefix.TrimEnd('/');
    }

    /// <inheritdoc />
    public Task<RegisterValue<TValue>> ReadAsync(TKey key, CancellationToken ct = default)
        => CompleteWithService(_service.ReadAsync(PathFor(key), ct), static read =>
        {
            if (read.Value is null)
            {
                return new RegisterValue<TValue>(false, default, 0);
            }
            var value = JsonSerializer.Deserialize<TValue>(read.Value.Value);
            if (value is null)
            {
                throw new InvalidDataException("The typed register contained a null JSON value.");
            }
            return new RegisterValue<TValue>(true, value, read.Value.Revision);
        });

    /// <inheritdoc />
    public Task<long> SetAsync(TKey key, TValue value, string requestId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return CompleteWithService(
            _service.SetAsync(PathFor(key), Encoding.UTF8.GetString(SerializeChecked(value, nameof(value))), requestId, ct: ct),
            static result => result.Revision);
    }

    /// <inheritdoc />
    public Task<bool> CompareExchangeAsync(TKey key, long expectedRevision, TValue value, string requestId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return CompleteWithService(
            _service.CompareExchangeAsync(PathFor(key), expectedRevision, Encoding.UTF8.GetString(SerializeChecked(value, nameof(value))), requestId, ct: ct),
            static result => result.Exchanged);
    }

    /// <summary>
    /// Converts the service's result inside the service's own completion. An awaited conversion would
    /// complete later on a thread-pool continuation, at a wall-clock-dependent moment, so a host polling
    /// for completion between virtual ticks would advance a timing-dependent number of ticks.
    /// </summary>
    private static Task<TResult> CompleteWithService<TSource, TResult>(Task<TSource> source, Func<TSource, TResult> convert)
    {
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The service's task runs its continuations asynchronously, which queues every continuation to its
        // scheduler — so the inline scheduler, not ExecuteSynchronously, is what keeps this on the
        // completing thread. HideScheduler keeps the conversion from seeing the inline scheduler as current.
        _ = source.ContinueWith(
            static (antecedent, state) =>
            {
                var (target, map) = ((TaskCompletionSource<TResult>, Func<TSource, TResult>))state!;
                if (antecedent.IsCanceled)
                {
                    target.TrySetCanceled();
                    return;
                }
                if (antecedent.Exception is { } failure)
                {
                    target.TrySetException(failure.InnerExceptions);
                    return;
                }
                try
                {
                    target.TrySetResult(map(antecedent.Result));
                }
                catch (Exception error)
                {
                    target.TrySetException(error);
                }
            },
            (completion, convert),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.HideScheduler,
            InlineTaskScheduler.Instance);
        return completion.Task;
    }

    private string PathFor(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return $"{_prefix}/{Convert.ToHexString(SerializeChecked(key, nameof(key)))}";
    }

    // Use the bytes the read path will decode, so a lossy key cannot alias another path and a
    // committed replacement cannot silently change its value on the next typed read.
    private static byte[] SerializeChecked<T>(T value, string paramName)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        T? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<T>(bytes);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            throw new ArgumentException("The register argument cannot be read back from its JSON encoding.", paramName, ex);
        }

        if (JsonRoundTripIdentity.FindDifference(value, decoded, null) is { } difference)
        {
            throw new ArgumentException($"The register argument does not read back as written ({difference}).", paramName);
        }

        return bytes;
    }
}

/// <summary>Runs each queued task immediately on the queuing thread.</summary>
file sealed class InlineTaskScheduler : TaskScheduler
{
    public static readonly InlineTaskScheduler Instance = new();

    protected override void QueueTask(Task task) => TryExecuteTask(task);

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => TryExecuteTask(task);

    protected override IEnumerable<Task>? GetScheduledTasks() => [];
}
