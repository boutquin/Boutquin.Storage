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
using Boutquin.Storage.Infrastructure.Events;
namespace Boutquin.Storage.Distributed.Transactions;

// A second active state-machine owner cannot safely share an append log: append serialization alone
// does not serialize read/decide/write. Keep this distinct owner lock open until the host quiesces.
internal sealed class TransactionJournal<TEvent> : IDisposable
{
    private readonly AppendOnlyEventLog<TEvent> _log;
    private readonly FileStream _owner;
    private bool _poisoned;
    private bool _disposed;
    internal Action<string, bool>? FaultInjector { get; set; }
    private TransactionJournal(AppendOnlyEventLog<TEvent> log, FileStream owner) { _log = log; _owner = owner; }
    internal static async Task<TransactionJournal<TEvent>> OpenAsync(string path, CancellationToken ct)
    {
        var log = await AppendOnlyEventLog<TEvent>.CreateAsync(path, ct: ct).ConfigureAwait(false);
        try
        {
            var owner = new FileStream(path + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new TransactionJournal<TEvent>(log, owner);
        }
        catch { log.Dispose(); throw; }
    }
    internal IAsyncEnumerable<(long Position, TEvent Event)> Replay(CancellationToken ct) => _log.ReadFromAsync(0, ct);
    internal void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_poisoned) { throw new InvalidOperationException("Journal outcome is uncertain; dispose and reopen before continuing."); }
    }
    internal async Task AppendAsync(TEvent record, string boundary, CancellationToken ct)
    {
        EnsureUsable();
        try
        {
            FaultInjector?.Invoke(boundary, false);
            await _log.AppendAsync(record, ct).ConfigureAwait(false);
            FaultInjector?.Invoke(boundary, true);
        }
        catch { _poisoned = true; throw; }
    }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _log.Dispose(); _owner.Dispose();
    }
}
