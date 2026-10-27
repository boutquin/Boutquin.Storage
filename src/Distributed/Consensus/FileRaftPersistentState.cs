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
using System.Buffers.Binary;

using Boutquin.Storage.Infrastructure.KeyValueStore;

namespace Boutquin.Storage.Distributed.Consensus;

/// <summary>
/// File-backed <see cref="IRaftPersistentState"/> — the post-GA completion of the Raft
/// persistence seam. Persists <c>currentTerm</c> and <c>votedFor</c> durably so that a crash
/// and restart cannot lose the vote and elect two leaders in the same term (split brain) —
/// the Raft <i>safety</i> property the in-memory default explicitly does not provide. Drops in
/// through the same <see cref="RaftNode{TCommand}"/> ctor parameter as
/// <see cref="InMemoryRaftPersistentState"/>, so substituting it is binary-compatible.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomic rewrite.</b> The two state words are small and change together at most once per
/// election, so each mutation rewrites the whole file: the new record is written to a sibling
/// <c>.tmp</c> file, flushed to disk (<c>FileStream.Flush(flushToDisk: true)</c> = fsync), then
/// atomically renamed over the target. A crash mid-write leaves either the old complete record
/// or the new complete record on disk — never a torn one. This is why the file does not carry
/// the torn-write replay logic <see cref="FileRaftLog{TCommand}"/> needs for its append path.
/// </para>
/// <para>
/// <b>Format.</b> <c>[currentTerm: 8 bytes LE][voteLength: 4 bytes LE, -1 ⇒ null]
/// [voteUtf8: voteLength bytes, strict UTF-8][crc32c: 4 bytes LE over the preceding bytes]</c>. The checksum
/// is the repo-standard Castagnoli <c>Crc32C</c> (the repo's single-checksum convention), present as
/// defence-in-depth against silent disk corruption of an otherwise-atomically-written file.
/// </para>
/// <para>
/// <b>Threading contract.</b> Not thread-safe — same as the in-memory default. The consuming
/// <see cref="RaftNode{TCommand}"/> serialises every mutation through the cluster tick loop.
/// </para>
/// <para>
/// <b>Reference:</b> Ongaro &amp; Ousterhout (2014); Kleppmann &amp; Riccomini,
/// <i>Designing Data-Intensive Applications</i>, 2nd ed. (O'Reilly, 2026), Ch. 10 p. 366–370.
/// </para>
/// </remarks>
public sealed class FileRaftPersistentState : IRaftPersistentState
{
    // A vote is a node identity: its durable bytes must decode to exactly the id voted for. The default
    // Encoding.UTF8 replaces an unpaired surrogate with U+FFFD, which would persist a vote for a DIFFERENT
    // node; the strict encoding throws instead, on both the write and the read side.
    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _path;
    private long _currentTerm;
    private string? _votedFor;

    /// <summary>
    /// Opens (or creates) the persistent-state file at <paramref name="path"/>, loading any
    /// previously persisted term and vote. A missing file starts at term 0 with no vote.
    /// </summary>
    /// <param name="path">The durable file path. Must be non-empty; its directory must exist.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InvalidDataException">The existing file is truncated or fails its checksum.</exception>
    public FileRaftPersistentState(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Persistent-state path must be non-empty.", nameof(path));
        }

        _path = path;
        Load();
    }

    /// <inheritdoc/>
    public long CurrentTerm
    {
        get => _currentTerm;
        set
        {
            Persist(value, _votedFor);
            _currentTerm = value;
        }
    }

    /// <inheritdoc/>
    public string? VotedFor
    {
        get => _votedFor;
        set
        {
            Persist(_currentTerm, value);
            _votedFor = value;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Single <c>Persist()</c> for the pair: the record format already carries term and vote
    /// together, so one temp-write + fsync + atomic rename publishes the whole transition —
    /// a crash exposes either the complete old (term, vote) pair or the complete new one,
    /// never a new term with a stale vote.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="votedFor"/> is not well-formed UTF-16 (an unpaired
    /// surrogate) and so has no exact durable form; neither the durable nor the in-memory state changes.</exception>
    public void SetTermAndVote(long term, string? votedFor)
    {
        Persist(term, votedFor);
        _currentTerm = term;
        _votedFor = votedFor;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            _currentTerm = 0;
            _votedFor = null;
            return;
        }

        var bytes = File.ReadAllBytes(_path);
        const int minLength = sizeof(long) + sizeof(int) + sizeof(uint); // term + voteLen + crc
        if (bytes.Length < minLength)
        {
            throw new InvalidDataException(
                $"Raft persistent-state file '{_path}' is truncated ({bytes.Length} bytes).");
        }

        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - sizeof(uint)));
        var computedCrc = Crc32C.HashToUInt32(bytes.AsSpan(0, bytes.Length - sizeof(uint)));
        if (storedCrc != computedCrc)
        {
            throw new InvalidDataException(
                $"Raft persistent-state file '{_path}' failed its CRC check (corrupt).");
        }

        _currentTerm = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0));
        var voteLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(sizeof(long)));
        if (voteLength == -1)
        {
            // -1 is the writer's only null-vote sentinel, and a null-vote record has no vote
            // payload — its length is exactly term + voteLen + crc. Anything longer means the
            // length field and the file contents disagree; accepting it would silently honour
            // a record whose CRC happened to cover bytes the reader then ignores.
            if (bytes.Length != minLength)
            {
                throw new InvalidDataException(
                    $"Raft persistent-state file '{_path}' has an inconsistent vote length.");
            }
            _votedFor = null;
        }
        else if (voteLength < 0)
        {
            // The writer never emits a negative length other than -1; any other negative is
            // corruption, not a sentinel. Treating it as null-vote would silently load a
            // damaged record.
            throw new InvalidDataException(
                $"Raft persistent-state file '{_path}' has an inconsistent vote length.");
        }
        else
        {
            // Mirror the long-arithmetic bound used in FileRaftLog.Replay: validate using
            // long arithmetic so an absurd voteLength from a corrupt file cannot cause an
            // Int32 overflow that incidentally passes the consistency check or causes an
            // out-of-bounds array access.
            const int voteStart = sizeof(long) + sizeof(int);
            if ((long)voteStart + voteLength + sizeof(uint) != bytes.Length)
            {
                throw new InvalidDataException(
                    $"Raft persistent-state file '{_path}' has an inconsistent vote length.");
            }
            try
            {
                _votedFor = s_strictUtf8.GetString(bytes, voteStart, voteLength);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException(
                    $"Raft persistent-state file '{_path}' has a vote that is not well-formed UTF-8.", ex);
            }
        }
    }

    // Writes the (term, vote) pair durably. Callers publish the pair to the in-memory fields only after this
    // returns, so a refused vote (ill-formed UTF-16, EncoderFallbackException) or a failed write leaves the
    // in-memory state equal to the durable state.
    private void Persist(long currentTerm, string? votedFor)
    {
        var voteBytes = votedFor is null ? [] : s_strictUtf8.GetBytes(votedFor);
        var voteLength = votedFor is null ? -1 : voteBytes.Length;

        var payloadLength = sizeof(long) + sizeof(int) + voteBytes.Length;
        var buffer = new byte[payloadLength + sizeof(uint)];

        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0), currentTerm);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(sizeof(long)), voteLength);
        if (voteBytes.Length > 0)
        {
            voteBytes.CopyTo(buffer.AsSpan(sizeof(long) + sizeof(int)));
        }

        var crc = Crc32C.HashToUInt32(buffer.AsSpan(0, payloadLength));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(payloadLength), crc);

        // Write to a sibling temp file, fsync it, then atomically rename over the target so a
        // crash can never expose a torn record — the rename publishes the whole new file or
        // nothing. The rename goes through DurableFile.AtomicReplace (not a bare File.Move) so the
        // containing directory is fsync'd after the rename: without that, a crash between the
        // rename returning and the directory entry reaching disk could expose the PRE-write record
        // (stale currentTerm/votedFor) on restart, letting the node forget its vote and elect two
        // leaders in one term (split brain) — the exact failure this type exists to prevent.
        var tempPath = _path + ".tmp";
        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(buffer, 0, buffer.Length);
            fs.Flush(flushToDisk: true);
        }

        DurableFile.AtomicReplace(tempPath, _path);
    }
}
