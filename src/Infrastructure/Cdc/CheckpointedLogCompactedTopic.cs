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
using System.Text.Json;
using Boutquin.Storage.Domain.Interfaces.Cdc;
using Boutquin.Storage.Domain.Interfaces.Streaming;
using Boutquin.Storage.Infrastructure.Schemas;

namespace Boutquin.Storage.Infrastructure.Cdc;

/// <summary>Atomically checkpoints the latest record per key and next offset, retaining deletion watermarks.</summary>
/// <typeparam name="TKey">Stable non-null keys serializable by the supplied JSON options.</typeparam>
/// <typeparam name="TValue">Immutable JSON-serializable values.</typeparam>
/// <remarks>One owner per checkpoint identifier. Persistence follows the checkpoint store; each append costs O(retained keys).
/// Reads have compacted gaps and finite capture boundaries. Restore after any failed save before using the topic again.
/// An append whose rows would not restore exactly as written (text with an unpaired surrogate, keys that collapse on disk)
/// is refused with <see cref="ArgumentException"/> before anything is saved.</remarks>
public sealed class CheckpointedLogCompactedTopic<TKey, TValue> : ILogCompactedTopic<TKey, TValue> where TKey : notnull
{
    private readonly ICheckpointStore _store;
    private readonly string _id;
    private readonly JsonSerializerOptions? _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<TKey, ChangeRecord<TKey, TValue>> _latest = [];
    private long _next;
    private bool _ready;

    /// <summary>Creates an unopened topic; call RestoreAsync before use.</summary>
    /// <param name="store">Atomic offset/state checkpoint persistence.</param>
    /// <param name="id">Exclusive topic checkpoint identifier.</param>
    /// <param name="options">Stable serialization contract used across reopen.</param>
    public CheckpointedLogCompactedTopic(ICheckpointStore store, string id, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _store = store; _id = id; _options = options;
    }

    /// <summary>Loads and validates persisted rows and their exclusive high-water mark.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Completion when reads and appends may begin.</returns>
    public async ValueTask RestoreAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _ready = false;
            var saved = await _store.LoadAsync(_id, ct).ConfigureAwait(false);
            var records = saved is null ? [] : JsonSerializer.Deserialize<ChangeRecord<TKey, TValue>[]>(saved.Value.State, _options)
                ?? throw new InvalidDataException("Missing compacted topic state.");
            var next = saved?.Offset ?? 0;
            if (next < 0)
            {
                throw new InvalidDataException("Negative topic cursor.");
            }

            _latest = Validate(records, next); _next = next; _ready = true;
        }
        finally { _gate.Release(); }
    }

    private static Dictionary<TKey, ChangeRecord<TKey, TValue>> Validate(ChangeRecord<TKey, TValue>[] records, long next)
    {
        var latest = new Dictionary<TKey, ChangeRecord<TKey, TValue>>();
        var positions = new HashSet<long>();
        foreach (var record in records)
        {
            if (record is null || record.Key is null || record.Position < 0 || record.Position >= next ||
                record.Operation is not (ChangeOperation.Insert or ChangeOperation.Update or ChangeOperation.Delete) ||
                (record.Operation != ChangeOperation.Delete && record.Value is null) ||
                !positions.Add(record.Position) || !latest.TryAdd(record.Key, record))
            {
                throw new InvalidDataException("Invalid compacted topic record.");
            }
        }
        if ((next == 0) != (records.Length == 0) || (next > 0 && !positions.Contains(next - 1)))
        {
            throw new InvalidDataException("Topic cursor does not match retained state.");
        }

        return latest;
    }

    // The checkpoint is the only durable copy, so the append is acknowledged only if RestoreAsync would
    // load exactly the rows written: JSON writes an unpaired surrogate as U+FFFD and drops ignored key
    // members, so distinct keys can collapse on disk into rows the restore refuses as duplicates.
    private string? FindRestoreDifference(ChangeRecord<TKey, TValue>[] rows, byte[] bytes, long next)
    {
        string? difference;
        try
        {
            var decoded = JsonSerializer.Deserialize<ChangeRecord<TKey, TValue>[]>(bytes, _options)
                ?? throw new InvalidDataException("Missing compacted topic state.");
            _ = Validate(decoded, next);
            difference = JsonRoundTripIdentity.FindDifference(rows, decoded, _options);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            difference = ex.Message;
        }

        return difference;
    }

    /// <inheritdoc />
    public async ValueTask<long> AppendAsync(TKey key, TValue? value, ChangeOperation operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (operation is not (ChangeOperation.Insert or ChangeOperation.Update or ChangeOperation.Delete))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        if (operation != ChangeOperation.Delete)
        {
            ArgumentNullException.ThrowIfNull(value);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var position = _next;
            var next = checked(position + 1);
            var candidate = new Dictionary<TKey, ChangeRecord<TKey, TValue>>(_latest)
            { [key] = new(position, operation, key, operation == ChangeOperation.Delete ? default : value, DateTimeOffset.UtcNow) };
            var rows = candidate.Values.ToArray();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(rows, _options);
            if (FindRestoreDifference(rows, bytes, next) is { } difference)
            {
                throw new ArgumentException(
                    $"The change does not read back from the checkpoint as written ({difference}); nothing was saved.");
            }
            // A store can persist then report an I/O error. Reopening resolves that uncertainty;
            // continuing with the old cursor could otherwise reuse a committed offset.
            _ready = false;
            await _store.SaveAsync(_id, next, bytes, ct).ConfigureAwait(false);
            _latest = candidate; _next = next; _ready = true;
            return position;
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async ValueTask<CdcSnapshot<TKey, TValue>> CaptureSnapshotAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureReady();
            return new(_latest.Values.OrderBy(r => r.Position).Select(r => r.Operation == ChangeOperation.Delete ? r : r with { Operation = ChangeOperation.Snapshot }).ToArray(), _next);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChangeRecord<TKey, TValue>> ReadChangesAsync(long position, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ChangeRecord<TKey, TValue>[] records;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { EnsureReady(); records = _latest.Values.Where(r => r.Position >= position).OrderBy(r => r.Position).ToArray(); }
        finally { _gate.Release(); }
        foreach (var record in records) { ct.ThrowIfCancellationRequested(); yield return record; }
    }

    private void EnsureReady()
    {
        if (!_ready)
        {
            throw new InvalidOperationException("Restore the topic before use or after a failed append.");
        }
    }
}
