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
using Boutquin.Storage.Domain.Interfaces.Streaming;

namespace Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

/// <summary>
/// File-backed checkpoint store that persists one file per processor ID.
///
/// <para><b>Binary format:</b> 8-byte little-endian offset followed by state bytes.
/// Writes are atomic via temp-file-and-rename.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 11 — "Stream Processing": checkpointing consumer offsets.</para>
/// </summary>
public sealed class FileCheckpointStore : ICheckpointStore
{
    private readonly string _directory;

    /// <summary>
    /// Initializes a new instance of <see cref="FileCheckpointStore"/>.
    /// </summary>
    /// <param name="directory">Directory where checkpoint files are stored. Created if it does not exist.</param>
    public FileCheckpointStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(string processorId, long offset, ReadOnlyMemory<byte> state, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        ct.ThrowIfCancellationRequested();

        var path = ResolvePath(processorId);
        var tempPath = path + $".tmp.{Guid.NewGuid():N}";

        try
        {
            var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            try
            {
                // 8-byte LE offset + state bytes
                var offsetBytes = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(offsetBytes, offset);
                await fs.WriteAsync(offsetBytes, ct).ConfigureAwait(false);
                await fs.WriteAsync(state, ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }
            finally
            {
                await fs.DisposeAsync().ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
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

        // Minimum: 8 bytes for offset
        if (data.Length < 8)
        {
            return null;
        }

        var offset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0, 8));
        var state = data.Length > 8 ? data[8..] : [];
        return (offset, state);
    }

    private string ResolvePath(string processorId) =>
        Path.Combine(_directory, $"{processorId}.checkpoint");
}
