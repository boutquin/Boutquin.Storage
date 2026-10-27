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
namespace Boutquin.Storage.Distributed.Consensus;

/// <summary>
/// In-memory <see cref="IRaftLog{TCommand}"/> default — backs every entry as a list element
/// and provides no crash durability. Correct only for non-failing reference scenarios; a
/// production deployment must supply a file-backed implementation through the same ctor
/// parameter on <see cref="RaftNode{TCommand}"/>.
/// </summary>
/// <typeparam name="TCommand">The immutable command type the state machine processes.</typeparam>
/// <remarks>
/// <para>
/// <b>Why ship this.</b> Keeps in-memory test fixtures cheap — every Raft cluster test
/// would otherwise have to construct a file-backed log. The seam itself is the load-bearing
/// pre-GA piece (post-GA addition would either break the ctor or force overloads); the
/// in-memory default is just the convenience escape hatch for tests + reference scenarios.
/// </para>
/// <para><b>Ownership.</b> Commands are borrowed immutable references: append and reads do not clone
/// command objects. A range has its own list but retains the same commands. Never mutate a command
/// after append or through a returned entry. Use a file-backed log with an appropriate serializer
/// when private command copies are required.</para>
/// </remarks>
public sealed class InMemoryRaftLog<TCommand> : IRaftLog<TCommand>
{
    private readonly List<(long Term, TCommand Command)> _entries = [];

    /// <inheritdoc/>
    public int Count => _entries.Count;

    /// <inheritdoc/>
    public (long Term, TCommand Command) GetEntry(int index)
    {
        if (index < 0 || index >= _entries.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                $"Log index {index} is outside [0, {_entries.Count}).");
        }
        return _entries[index];
    }

    /// <inheritdoc/>
    public void Append((long Term, TCommand Command) entry) => _entries.Add(entry);

    /// <inheritdoc/>
    public void TruncateFrom(int fromIndex)
    {
        if (fromIndex < 0 || fromIndex > _entries.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fromIndex),
                $"Truncate index {fromIndex} is outside [0, {_entries.Count}].");
        }
        if (fromIndex < _entries.Count)
        {
            _entries.RemoveRange(fromIndex, _entries.Count - fromIndex);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<(long Term, TCommand Command)> GetRange(int startIndex, int count)
    {
        // count > Count - startIndex, not startIndex + count > Count: the sum overflows int for a
        // count near int.MaxValue and would pass the guard.
        if (startIndex < 0 || count < 0 || startIndex > _entries.Count || count > _entries.Count - startIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startIndex),
                $"Range [{startIndex}, {(long)startIndex + count}) is outside [0, {_entries.Count}).");
        }
        return count == 0 ? [] : _entries.GetRange(startIndex, count);
    }
}
