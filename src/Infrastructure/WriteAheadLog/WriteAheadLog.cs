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
namespace Boutquin.Storage.Infrastructure.WriteAheadLog;

/// <summary>
/// A file-based WriteAheadLog that provides durability for key-value operations.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the WriteAheadLog.</typeparam>
/// <typeparam name="TValue">The type of the values in the WriteAheadLog.</typeparam>
/// <remarks>
/// <para>
/// <b>Entry format:</b> Each WriteAheadLog entry is written as:
/// <code>
/// [4 bytes: payload length (int32, little-endian)] [N bytes: payload (key + value)] [4 bytes: CRC32 checksum (uint32, little-endian)]
/// </code>
/// The length prefix allows the reader to know exactly how many bytes to read for each entry.
/// The CRC32 checksum detects corruption (e.g., from a crash mid-write or disk error).
/// All multi-byte integers are written in little-endian format for cross-platform consistency.
/// </para>
/// <para>
/// <b>Durability guarantee:</b> Each append is fsync'd to disk before returning. This ensures that
/// once <see cref="AppendAsync"/> completes, the entry is durable even if the process crashes or
/// the OS loses power immediately afterward.
/// </para>
/// <para>
/// <b>Persistent FileStream:</b> The WriteAheadLog holds a single <see cref="FileStream"/> open for the
/// lifetime of the instance (lazily opened on first append). This avoids the overhead of opening
/// and closing the file on every append. The stream is closed on <see cref="Dispose"/> or
/// <see cref="TruncateAsync"/>.
/// </para>
/// <para>
/// <b>Recovery semantics:</b> During recovery, corrupted entries (checksum mismatch, deserialization
/// failure, or payload exceeding the maximum size) are skipped. The length-prefix format allows the
/// reader to advance past a corrupt entry because the payload bytes have already been consumed by
/// the time the checksum is validated. Only structurally incomplete entries (truncated length prefix,
/// truncated payload, or truncated checksum) cause the reader to stop, since there is no way to
/// determine entry boundaries from partial data.
/// </para>
/// <para>
/// <b>Why length-prefix + CRC32?</b> Length-prefixing enables efficient sequential reads without
/// scanning for delimiters. CRC32 provides corruption detection: if a crash occurs mid-write,
/// the partial entry will have an incorrect checksum (or insufficient bytes) and will be skipped
/// during recovery. This is the standard approach used by LevelDB, RocksDB, and similar engines.
/// </para>
/// <para>
/// <b>Why SemaphoreSlim for thread safety?</b> The WriteAheadLog must be safe for concurrent appends from
/// multiple threads. SemaphoreSlim(1,1) acts as an async-compatible mutex, ensuring only one
/// thread writes to the file at a time. Unlike <c>lock</c>, it supports <c>await</c> inside the
/// critical section.
/// </para>
/// <para>
/// <b>Why BinaryEntrySerializer?</b> The WriteAheadLog reuses the existing serialization infrastructure
/// to convert keys and values to bytes. The serialized bytes are then wrapped with the length
/// prefix and checksum before being written to disk.
/// </para>
/// </remarks>
public sealed class WriteAheadLog<TKey, TValue> : IWriteAheadLog<TKey, TValue>
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Maximum allowed payload size (256 MB). Payloads larger than this are treated as corrupt
    /// during recovery to guard against reading garbage length values.
    /// </summary>
    private const int MaxPayloadSize = 256 * 1024 * 1024;

    private readonly string _filePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private int _disposed;
    private FileStream? _appendStream;

    /// <summary>
    /// Initializes a new instance of the <see cref="WriteAheadLog{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="filePath">The file path for the WriteAheadLog file.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="filePath"/> is null or empty.</exception>
    public WriteAheadLog(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentNullException(nameof(filePath));
        }

        _filePath = filePath;

        // Ensure the directory exists.
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <inheritdoc/>
    public async Task AppendAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        // Serialize key+value into a byte array (the payload).
        using var payloadStream = new MemoryStream();
        key.Serialize(payloadStream);
        value.Serialize(payloadStream);
        var payload = payloadStream.ToArray();

        // Compute CRC32 checksum of the payload.
        var checksum = System.IO.Hashing.Crc32.HashToUInt32(payload);

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStreamOpen();

            // Write: [payload length (little-endian)][payload][crc32 (little-endian)]
            var lengthBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, payload.Length);
            await _appendStream!.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
            await _appendStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            var checksumBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(checksumBytes, checksum);
            await _appendStream.WriteAsync(checksumBytes, cancellationToken).ConfigureAwait(false);
            await _appendStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Fsync: force data to disk — the whole point of a WriteAheadLog.
            _appendStream.Flush(flushToDisk: true);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(TKey Key, TValue Value)>> RecoverAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var entries = new List<(TKey Key, TValue Value)>();

        if (!File.Exists(_filePath))
        {
            return entries;
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Close the persistent append stream before reading. The append stream is opened
            // with FileShare.None, so it must be closed to allow the read stream to open.
            // It will be lazily re-opened on the next AppendAsync call.
            CloseAppendStream();

            var fileStream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            await using (fileStream.ConfigureAwait(false))
            {
                while (fileStream.Position < fileStream.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Try to read the length prefix (4 bytes).
                    var lengthBytes = new byte[4];
                    var bytesRead = await ReadExactAsync(fileStream, lengthBytes, cancellationToken).ConfigureAwait(false);
                    if (bytesRead < 4)
                    {
                        // Incomplete length prefix — truncated entry, stop.
                        break;
                    }

                    var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                    if (payloadLength <= 0 || payloadLength > MaxPayloadSize || fileStream.Position + payloadLength + 4 > fileStream.Length)
                    {
                        // Invalid, oversized, or truncated entry — stop.
                        break;
                    }

                    // Read the payload.
                    var payload = new byte[payloadLength];
                    bytesRead = await ReadExactAsync(fileStream, payload, cancellationToken).ConfigureAwait(false);
                    if (bytesRead < payloadLength)
                    {
                        // Truncated payload — stop.
                        break;
                    }

                    // Read the checksum (4 bytes).
                    var checksumBytes = new byte[4];
                    bytesRead = await ReadExactAsync(fileStream, checksumBytes, cancellationToken).ConfigureAwait(false);
                    if (bytesRead < 4)
                    {
                        // Truncated checksum — stop.
                        break;
                    }

                    var storedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(checksumBytes);
                    var computedChecksum = System.IO.Hashing.Crc32.HashToUInt32(payload);

                    if (storedChecksum != computedChecksum)
                    {
                        // Corrupted entry — skip it and continue to the next entry.
                        // The payload bytes have already been consumed, so the stream is
                        // positioned at the start of the next entry.
                        continue;
                    }

                    // Deserialize key and value from the payload.
                    try
                    {
                        using var entryStream = new MemoryStream(payload);
                        var key = TKey.Deserialize(entryStream);
                        var value = TValue.Deserialize(entryStream);
                        entries.Add((key, value));
                    }
                    catch
                    {
                        // Deserialization failure — corrupted entry, skip and continue.
                        continue;
                    }
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }

        return entries;
    }

    /// <inheritdoc/>
    public async Task TruncateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Close the persistent append stream before truncating.
            CloseAppendStream();

            // Overwrite the file with an empty file.
            var fileStream = new FileStream(
                _filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);
            await using (fileStream.ConfigureAwait(false))
            {
                // FileMode.Create truncates the file to zero length.
                // Fsync the truncation to disk.
                fileStream.Flush(flushToDisk: true);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Reads exactly the requested number of bytes from the stream, or fewer if the stream ends.
    /// </summary>
    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }
        return totalRead;
    }

    /// <summary>
    /// Ensures the persistent append stream is open. Lazily opens it on first call.
    /// Must be called while holding the semaphore.
    /// </summary>
    private void EnsureStreamOpen()
    {
        _appendStream ??= new FileStream(
            _filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);
    }

    /// <summary>
    /// Closes the persistent append stream if it is open.
    /// Must be called while holding the semaphore.
    /// </summary>
    private void CloseAppendStream()
    {
        _appendStream?.Dispose();
        _appendStream = null;
    }

    /// <summary>
    /// Disposes the WriteAheadLog, closing the persistent file stream and releasing the semaphore.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CloseAppendStream();
            _semaphore.Dispose();
        }
    }
}
