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
namespace Boutquin.Storage.Distributed.Coordination;

// Each replica derives this state exclusively from its committed prefix. Replicated time, sorted expiry
// order, and request deduplication make replay independent of message delivery and local poll frequency.
internal sealed class CoordinationStateMachine
{
    private readonly Dictionary<string, CoordinationValue> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<long, CoordinationLease> _leases = [];
    private readonly Dictionary<string, long> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (CoordinationCommand Command, CoordinationOutcome Outcome)> _requests = new(StringComparer.Ordinal);
    private long _time;
    private long _revision;
    private long _sequence;

    internal long? NextExpiry => _leases.Count == 0 ? null : _leases.Values.Min(lease => lease.ExpiresAt);

    internal CoordinationOutcome Apply(CoordinationCommand command, Action<CoordinationEvent> publish)
    {
        _time = Math.Max(_time, command.Time);
        foreach (var lease in _leases.Values.Where(lease => lease.ExpiresAt <= _time).OrderBy(lease => lease.Id).ToArray())
        {
            EndLease(lease.Id, publish);
        }
        if (command.RequestId is { } id && _requests.TryGetValue(id, out var previous))
        {
            return command.SameRequest(previous.Command)
                ? previous.Outcome
                : new CoordinationOutcome(null, "The request ID was already used with different arguments.");
        }

        var outcome = Execute(command, publish);
        if (command.RequestId is { } requestId)
        {
            _requests.Add(requestId, (command, outcome));
        }
        return outcome;
    }

    private CoordinationOutcome Execute(CoordinationCommand command, Action<CoordinationEvent> publish)
    {
        var current = _values.GetValueOrDefault(command.Path);
        switch (command.Operation)
        {
            case CoordinationOperation.Read:
                return new CoordinationOutcome(new CoordinationRead(current, _revision));
            case CoordinationOperation.Set:
                if (!LeaseIsActive(command.LeaseId))
                {
                    return new CoordinationOutcome(null, "The ephemeral lease is not active.");
                }
                return new CoordinationOutcome(SetValue(command.Path, command.Value!, command.LeaseId, publish));
            case CoordinationOperation.CompareExchange:
                if ((current?.Revision ?? 0) != command.ExpectedRevision)
                {
                    return new CoordinationOutcome(new CompareExchangeResult(false, current));
                }
                if (command.Value is null)
                {
                    if (current is not null)
                    {
                        DeleteValue(command.Path, CoordinationEventKind.Deleted, publish);
                    }
                    return new CoordinationOutcome(new CompareExchangeResult(true, null));
                }
                if (!LeaseIsActive(command.LeaseId))
                {
                    return new CoordinationOutcome(null, "The ephemeral lease is not active.");
                }
                var replacement = SetValue(command.Path, command.Value, command.LeaseId, publish);
                return new CoordinationOutcome(new CompareExchangeResult(true, replacement));
            case CoordinationOperation.AcquireLease:
                if (_resources.ContainsKey(command.Path))
                {
                    return new CoordinationOutcome(null);
                }
                var token = checked(++_sequence);
                var lease = new CoordinationLease(token, command.Path, command.Owner!, token, Deadline(command.Duration));
                _leases.Add(token, lease);
                _resources.Add(command.Path, token);
                return new CoordinationOutcome(lease);
            case CoordinationOperation.RenewLease:
                if (!_leases.TryGetValue(command.LeaseId!.Value, out var active))
                {
                    return new CoordinationOutcome(false);
                }
                _leases[active.Id] = active with { ExpiresAt = Math.Max(active.ExpiresAt, Deadline(command.Duration)) };
                return new CoordinationOutcome(true);
            case CoordinationOperation.ReleaseLease:
                return new CoordinationOutcome(EndLease(command.LeaseId!.Value, publish));
            case CoordinationOperation.ReadLease:
                return new CoordinationOutcome(_resources.TryGetValue(command.Path, out var leaseId) ? _leases[leaseId] : null);
            case CoordinationOperation.Allocate:
                return new CoordinationOutcome(checked(++_sequence));
            case CoordinationOperation.Expire:
                return new CoordinationOutcome(null);
            default:
                throw new InvalidOperationException("Coordination command dispatch and operation definitions have diverged.");
        }
    }

    // Admission checks the duration against the submitting tick, but a reordered request applies at a later
    // replicated time. Saturating keeps apply total on every committed entry, so replay can never throw.
    private long Deadline(long duration) => duration > long.MaxValue - _time ? long.MaxValue : _time + duration;

    private bool LeaseIsActive(long? leaseId) => leaseId is null || _leases.ContainsKey(leaseId.Value);

    private CoordinationValue SetValue(string path, string value, long? leaseId, Action<CoordinationEvent> publish)
    {
        var changed = new CoordinationValue(value, checked(++_revision), leaseId);
        _values[path] = changed;
        publish(new CoordinationEvent(_revision, path, CoordinationEventKind.Changed, changed));
        return changed;
    }

    private void DeleteValue(string path, CoordinationEventKind kind, Action<CoordinationEvent> publish)
    {
        _values.Remove(path);
        publish(new CoordinationEvent(checked(++_revision), path, kind, null));
    }

    private bool EndLease(long id, Action<CoordinationEvent> publish)
    {
        if (!_leases.Remove(id, out var lease))
        {
            return false;
        }
        _resources.Remove(lease.Resource);
        foreach (var path in _values.Where(pair => pair.Value.LeaseId == id).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray())
        {
            DeleteValue(path, CoordinationEventKind.LeaseEnded, publish);
        }
        return true;
    }
}
