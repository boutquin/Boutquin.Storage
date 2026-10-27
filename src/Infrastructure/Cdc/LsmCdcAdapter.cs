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
using Boutquin.Storage.Domain.Interfaces.Cdc;
using Boutquin.Storage.Domain.Interfaces.Events;
using Boutquin.Storage.Infrastructure.LsmTree;

namespace Boutquin.Storage.Infrastructure.Cdc;

/// <summary>Captures LSM writes in an authoritative change journal and replays it into the engine after interruption.</summary>
/// <typeparam name="TKey">Serializable storage key.</typeparam>
/// <typeparam name="TValue">Immutable serializable value.</typeparam>
/// <remarks>One adapter exclusively owns all writes to its engine and journal. Start with an empty engine/journal pair,
/// or reopen that same pair. Journal append is the commit point; an apply failure requires recovery and may already
/// have committed. The caller owns resource disposal and must finish operations first. All history and latest-key
/// metadata are retained. Durability follows the event log, not a two-resource atomic commit.</remarks>
public sealed class LsmCdcAdapter<TKey, TValue> : ISnapshotCdcSource<TKey, TValue>
    where TKey : IStorageKey<TKey>, new() where TValue : ISerializable<TValue>, new()
{
    private readonly ILsmStorageEngine<TKey, TValue> _engine;
    private readonly IEventLog<ChangeRecord<TKey, TValue>> _journal;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<TKey, ChangeRecord<TKey, TValue>> _latest = [];
    private long _next;
    private bool _ready;

    /// <summary>Creates an unopened adapter; recover its journal before reads or writes.</summary>
    /// <param name="engine">Exclusively owned LSM materialization.</param>
    /// <param name="journal">Exclusively owned authoritative change log.</param>
    public LsmCdcAdapter(ILsmStorageEngine<TKey, TValue> engine, IEventLog<ChangeRecord<TKey, TValue>> journal)
    {
        ArgumentNullException.ThrowIfNull(engine); ArgumentNullException.ThrowIfNull(journal);
        _engine = engine; _journal = journal;
    }

    /// <summary>Reconstructs the latest keyed state and idempotently materializes it in the LSM engine.</summary>
    /// <param name="ct">Cancellation leaves the adapter closed until recovery succeeds.</param>
    /// <returns>Completion when journal and engine agree. Unexpected engine keys are rejected before any replay write.</returns>
    public async ValueTask RecoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _ready = false;
            var latest = new Dictionary<TKey, ChangeRecord<TKey, TValue>>();
            long next = 0;
            await foreach (var (position, record) in _journal.ReadFromAsync(0, ct).ConfigureAwait(false))
            {
                if (position != next || record is null || record.Key is null ||
                    record.Operation is not (ChangeOperation.Insert or ChangeOperation.Update or ChangeOperation.Delete) ||
                    (record.Operation != ChangeOperation.Delete && record.Value is null))
                {
                    throw new InvalidDataException("Invalid CDC journal prefix.");
                }

                latest[record.Key] = record with { Position = position };
                next = checked(position + 1);
            }
            var existing = await _engine.GetAllItemsAsync(ct).ConfigureAwait(false);
            if (existing.Any(row => !latest.ContainsKey(row.Key)))
            {
                throw new InvalidOperationException("The engine contains keys absent from its CDC journal. Use a dedicated engine.");
            }

            foreach (var record in latest.Values.OrderBy(r => r.Position))
            {
                await ApplyAsync(record, ct).ConfigureAwait(false);
            }

            _latest = latest; _next = next; _ready = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Commits an upsert to the journal, then applies it to the engine. An entry the engine would refuse is refused before the journal.</summary>
    /// <param name="key">Affected key.</param>
    /// <param name="value">Immutable value.</param>
    /// <param name="ct">Cancellation token; a post-append failure has an uncertain acknowledged outcome.</param>
    /// <returns>Assigned change position after successful materialization.</returns>
    public ValueTask<long> SetAsync(TKey key, TValue value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WriteAsync(key, value, false, ct);
    }

    /// <summary>Commits a deletion and retains its position even if the key was already absent.</summary>
    /// <param name="key">Affected key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Assigned deletion position after materialization.</returns>
    public ValueTask<long> RemoveAsync(TKey key, CancellationToken ct = default) => WriteAsync(key, default, true, ct);

    private async ValueTask<long> WriteAsync(TKey key, TValue? value, bool delete, CancellationToken ct)
    {
        Guard.AgainstNullOrDefault(() => key);
        if (!delete)
        {
            Guard.AgainstNullOrDefault(() => value);

            // The journal append below is the commit point and recovery replays every committed
            // record, so an entry the engine refuses must be refused here: once journaled, its
            // deterministic refusal would fail every recovery and leave the adapter closed for good.
            // The LSM engine and the time-series decorator over it both answer the check (the
            // decorator by forwarding to its inner engine); an engine that answers none is not
            // pre-checked.
            (_engine as ILsmEntryAdmissionCheck<TKey, TValue>)?.ValidateEntryAdmission(key, value!);
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var operation = delete ? ChangeOperation.Delete : _latest.TryGetValue(key, out var previous) && previous.Operation != ChangeOperation.Delete
                ? ChangeOperation.Update : ChangeOperation.Insert;
            var record = new ChangeRecord<TKey, TValue>(_next, operation, key, value, DateTimeOffset.UtcNow);
            var expected = _next;
            _ready = false;
            var position = await _journal.AppendAsync(record, ct).ConfigureAwait(false);
            if (position != expected)
            {
                throw new InvalidDataException("The CDC journal has another writer; recover before continuing.");
            }
            // Once append succeeds, finish materialization independently of caller cancellation.
            // A crash or engine failure is repaired from the authoritative journal on reopen.
            await ApplyAsync(record, CancellationToken.None).ConfigureAwait(false);
            _latest[key] = record; _next = checked(position + 1); _ready = true;
            return position;
        }
        finally { _gate.Release(); }
    }

    private async Task ApplyAsync(ChangeRecord<TKey, TValue> record, CancellationToken ct)
    {
        if (record.Operation == ChangeOperation.Delete)
        {
            await _engine.RemoveAsync(record.Key, ct).ConfigureAwait(false);
        }
        else
        {
            await _engine.SetAsync(record.Key, record.Value!, ct).ConfigureAwait(false);
        }
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
        long end;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { EnsureReady(); end = _next; }
        finally { _gate.Release(); }
        await foreach (var (offset, record) in _journal.ReadFromAsync(position, ct).ConfigureAwait(false))
        {
            if (offset >= end)
            {
                yield break;
            }

            yield return record with { Position = offset };
        }
    }

    private void EnsureReady()
    {
        if (!_ready)
        {
            throw new InvalidOperationException("Recover the adapter before use or after a failed write.");
        }
    }
}
