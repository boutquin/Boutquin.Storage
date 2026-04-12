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
using System.IO.Hashing;
using System.Text.Json;

using Boutquin.Storage.Domain.Interfaces.Events;

namespace Boutquin.Storage.Infrastructure.Events;

/// <summary>
/// File-backed append-only event log with length-prefixed binary format, CRC32 integrity,
/// and fsync durability.
///
/// <para><b>On-disk format per entry:</b>
/// [8 bytes: position (LE int64)] [4 bytes: payload length (LE int32)] [N bytes: JSON payload] [4 bytes: CRC32 (LE uint32)]</para>
///
/// <para><b>Recovery:</b> on construction, the log file is scanned to determine the next position.
/// Entries with checksum mismatches are skipped (per Pitfall #4: skip corrupt entries, don't stop).
/// Structurally incomplete entries (truncated header/payload/checksum) cause the reader to stop,
/// since entry boundaries cannot be determined from partial data.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 11 — "Stream Processing": append-only event logs with durable writes.</para>
/// </summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public sealed class AppendOnlyEventLog<TEvent> : IEventLog<TEvent>, IDisposable
{
    private const int PositionSize = 8;
    private const int LengthPrefixSize = 4;
    private const int ChecksumSize = 4;
    private const int HeaderSize = PositionSize + LengthPrefixSize;

    private readonly string _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions? _jsonOptions;
    private long _nextPosition;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="AppendOnlyEventLog{TEvent}"/>.
    /// Recovers position from existing file if present.
    /// </summary>
    /// <param name="filePath">Path to the log file.</param>
    /// <param name="jsonOptions">Optional JSON serializer options.</param>
    public AppendOnlyEventLog(string filePath, JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _jsonOptions = jsonOptions;

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _nextPosition = RecoverPosition();
    }

    /// <inheritdoc />
    public async ValueTask<long> AppendAsync(TEvent @event, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var payload = JsonSerializer.SerializeToUtf8Bytes(@event, _jsonOptions);
        var checksum = Crc32.HashToUInt32(payload);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var position = _nextPosition;

            var fs = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            try
            {
                // Write header: position (8 bytes LE) + payload length (4 bytes LE)
                var header = new byte[HeaderSize];
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0, PositionSize), position);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(PositionSize, LengthPrefixSize), payload.Length);

                await fs.WriteAsync(header, ct).ConfigureAwait(false);
                await fs.WriteAsync(payload, ct).ConfigureAwait(false);

                // Write CRC32 checksum (4 bytes LE)
                var checksumBytes = new byte[ChecksumSize];
                BinaryPrimitives.WriteUInt32LittleEndian(checksumBytes, checksum);
                await fs.WriteAsync(checksumBytes, ct).ConfigureAwait(false);

                fs.Flush(flushToDisk: true);
            }
            finally
            {
                await fs.DisposeAsync().ConfigureAwait(false);
            }

            _nextPosition = position + 1;
            return position;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(long Position, TEvent Event)> ReadFromAsync(
        long position, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(_filePath))
        {
            yield break;
        }

        var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            var header = new byte[HeaderSize];

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Try to read header
                if (!await ReadExactAsync(fs, header, ct).ConfigureAwait(false))
                {
                    yield break; // End of file or truncated header
                }

                var entryPosition = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0, PositionSize));
                var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(PositionSize, LengthPrefixSize));

                var payload = new byte[payloadLength];
                if (!await ReadExactAsync(fs, payload, ct).ConfigureAwait(false))
                {
                    yield break; // Truncated payload — structural corruption, stop
                }

                // Read CRC32 checksum
                var checksumBytes = new byte[ChecksumSize];
                if (!await ReadExactAsync(fs, checksumBytes, ct).ConfigureAwait(false))
                {
                    yield break; // Truncated checksum — structural corruption, stop
                }

                var storedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(checksumBytes);
                var computedChecksum = Crc32.HashToUInt32(payload);

                // Per Pitfall #4: skip corrupt entries, don't stop
                if (storedChecksum != computedChecksum)
                {
                    continue;
                }

                if (entryPosition >= position)
                {
                    var @event = JsonSerializer.Deserialize<TEvent>(payload, _jsonOptions)
                        ?? throw new InvalidOperationException($"Failed to deserialize event at position {entryPosition}.");
                    yield return (entryPosition, @event);
                }
            }
        }
        finally
        {
            await fs.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Scans the existing file to determine the next position.
    /// Skips entries with checksum mismatches; stops at structural truncation.
    /// </summary>
    private long RecoverPosition()
    {
        if (!File.Exists(_filePath))
        {
            return 0;
        }

        long maxPosition = -1;
        using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[HeaderSize];

        while (true)
        {
            // Read header
            if (!ReadExact(fs, header))
            {
                return maxPosition + 1;
            }

            var entryPosition = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0, PositionSize));
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(PositionSize, LengthPrefixSize));

            // Check that enough bytes remain for payload + checksum
            var entryRemaining = payloadLength + ChecksumSize;
            if (fs.Position + entryRemaining > fs.Length)
            {
                // Partial entry at tail — structural corruption, stop
                return maxPosition + 1;
            }

            // Read payload for checksum validation
            var payload = new byte[payloadLength];
            if (!ReadExact(fs, payload))
            {
                return maxPosition + 1;
            }

            var checksumBytes = new byte[ChecksumSize];
            if (!ReadExact(fs, checksumBytes))
            {
                return maxPosition + 1;
            }

            var storedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(checksumBytes);
            var computedChecksum = Crc32.HashToUInt32(payload);

            // Per Pitfall #4: skip corrupt entries, don't stop
            if (storedChecksum != computedChecksum)
            {
                continue;
            }

            if (entryPosition > maxPosition)
            {
                maxPosition = entryPosition;
            }
        }
    }

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes, returning false if EOF reached.</summary>
    private static async ValueTask<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            totalRead += read;
        }

        return true;
    }

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes synchronously, returning false if EOF reached.</summary>
    private static bool ReadExact(Stream stream, byte[] buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
            {
                return false;
            }

            totalRead += read;
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _writeLock.Dispose();
        }
    }
}
