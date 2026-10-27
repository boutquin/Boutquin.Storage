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
using System.Text.RegularExpressions;

using Boutquin.Storage.Domain.Interfaces.Streaming;
using Boutquin.Storage.Infrastructure.KeyValueStore;

namespace Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

/// <summary>
/// File-backed checkpoint store that persists one file per processor ID.
///
/// <para><b>Binary format:</b> 8-byte little-endian offset, state bytes, then a 4-byte
/// little-endian CRC32C over everything before it. Writes are atomic via
/// temp-file-and-rename with a directory fsync.</para>
///
/// <para><b>Fail closed on corruption:</b> a checkpoint whose CRC fails — or that is too
/// short to even carry the offset and checksum — makes <see cref="LoadAsync"/> throw
/// <see cref="InvalidDataException"/> rather than return a value. Returning a bit-flipped
/// offset would silently resume the processor at the wrong position; returning
/// <see langword="null"/> ("no checkpoint") would silently restart it from zero. Both are
/// undetectable downstream, so the contract's "implementations must guarantee no corruption"
/// is enforced by refusing to serve a checkpoint that cannot be authenticated.</para>
///
/// <para><b>Processor-id charset:</b> ids are mapped to file names injectively, so they are
/// restricted to alphanumeric, <c>.</c>, <c>_</c>, and <c>-</c>. Substituting separators
/// (the previous behavior) aliased distinct ids — <c>a/b</c> and <c>a_b</c> shared one
/// checkpoint file and clobbered each other's resume offsets.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026),
/// Ch. 12 — "Stream Processing": checkpointing consumer offsets.</para>
/// </summary>
public sealed partial class FileCheckpointStore : ICheckpointStore
{
    private const int OffsetSize = sizeof(long);
    private const int ChecksumSize = sizeof(uint);

    private readonly string _directory;

    /// <summary>
    /// Initializes a new instance of <see cref="FileCheckpointStore"/>.
    /// </summary>
    /// <param name="directory">Directory where checkpoint files are stored. Created if it does not exist.</param>
    public FileCheckpointStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        // Durable creation: each newly created ancestor level's entry is fsync'd, or a crash
        // after an acknowledged SaveAsync into a fresh directory tree can drop the subtree
        // with the checkpoint in it (SaveAsync fsyncs only the immediate containing directory).
        _ = DurableFile.CreateDirectoryDurable(_directory);
    }

    /// <inheritdoc />
    // AC-8.7 (F-49, DR-6): `state` changed from ReadOnlyMemory<byte> to byte[] to match
    // the storage representation and LoadAsync's return type (no asymmetry benefit).
    public async ValueTask SaveAsync(string processorId, long offset, byte[] state, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        ArgumentNullException.ThrowIfNull(state);
        ct.ThrowIfCancellationRequested();

        var path = ResolvePath(processorId);
        var tempPath = path + $".tmp.{Guid.NewGuid():N}";

        try
        {
            var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            try
            {
                // [8B LE offset][state][4B LE CRC32C over offset + state]
                var record = new byte[OffsetSize + state.Length + ChecksumSize];
                BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(0, OffsetSize), offset);
                state.AsSpan().CopyTo(record.AsSpan(OffsetSize));
                var crc = Crc32C.HashToUInt32(record.AsSpan(0, OffsetSize + state.Length));
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(OffsetSize + state.Length), crc);
                await fs.WriteAsync(record, ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }
            finally
            {
                await fs.DisposeAsync().ConfigureAwait(false);
            }

            DurableFile.AtomicReplace(tempPath, path);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<(long Offset, byte[] State)?> LoadAsync(string processorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        ct.ThrowIfCancellationRequested();

        var path = ResolvePath(processorId);
        if (!File.Exists(path))
        {
            return null;
        }

        var data = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);

        // Fail closed: a checkpoint that cannot be authenticated must not be served — neither
        // as a (possibly bit-flipped) value nor as null ("no checkpoint" → restart from zero).
        if (data.Length < OffsetSize + ChecksumSize)
        {
            throw new InvalidDataException(
                $"Checkpoint for processor '{processorId}' is truncated ({data.Length} bytes). " +
                "Refusing to treat a torn checkpoint as 'no checkpoint' — that would silently " +
                "restart the processor from zero. Restore or delete the file explicitly.");
        }

        var payloadLength = data.Length - ChecksumSize;
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(payloadLength));
        var computed = Crc32C.HashToUInt32(data.AsSpan(0, payloadLength));
        if (stored != computed)
        {
            throw new InvalidDataException(
                $"Checkpoint for processor '{processorId}' failed its CRC check (corrupt). " +
                "Refusing to resume from an unauthenticated offset. Restore or delete the " +
                "file explicitly.");
        }

        var offset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0, OffsetSize));
        var state = data[OffsetSize..payloadLength];
        return (offset, state);
    }

    /// <summary>
    /// Validates a processor id against the safe charset. Internal so the in-memory sibling
    /// enforces the IDENTICAL id contract — swapping <c>ICheckpointStore</c> implementations
    /// must not change which ids are valid.
    /// </summary>
    internal static void ValidateProcessorId(string processorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);

        // Injective id → file mapping: reject anything outside the safe charset rather than
        // substituting separators, which aliased distinct ids onto one file.
        if (!SafeProcessorIdPattern().IsMatch(processorId))
        {
            throw new ArgumentException(
                "Processor id contains invalid characters. Allowed: alphanumeric, '.', '_', '-'.",
                nameof(processorId));
        }
    }

    private string ResolvePath(string processorId)
    {
        ValidateProcessorId(processorId);

        // Case-sensitive fingerprint suffix: the charset alone leaves the mapping
        // non-injective on case-INSENSITIVE filesystems (default macOS APFS, NTFS), where
        // "OrderProcessor" and "orderprocessor" fold onto one file and silently clobber each
        // other's resume offsets. The CRC of the exact id bytes distinguishes case-distinct
        // ids up to CRC32C collision (2⁻³² per pair — accepted residual risk, not a proof of
        // injectivity), keeping the mapping practically unique while the readable id stays
        // in the filename.
        var fingerprint = Crc32C.HashToUInt32(Encoding.UTF8.GetBytes(processorId));
        return Path.Combine(_directory, $"{processorId}.{fingerprint:x8}.checkpoint");
    }

    [GeneratedRegex(@"^[a-zA-Z0-9._\-]+$")]
    private static partial Regex SafeProcessorIdPattern();
}
