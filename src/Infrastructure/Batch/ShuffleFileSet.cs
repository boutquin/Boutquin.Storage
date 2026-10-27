// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System.Globalization;

using Boutquin.Storage.Domain.Interfaces.Batch;

namespace Boutquin.Storage.Infrastructure.Batch;

/// <summary>Owns temporary length-framed partition files for grouping and co-partitioned joins.</summary>
/// <typeparam name="T">Row type with deterministic serialization.</typeparam>
/// <remarks>One output handle is open at a time; each row opens its destination for append,
/// favoring bounded descriptors over throughput. File count is partitionCount. The caller owns
/// this set and must dispose it after all readers finish. It is not safe to dispose during reads.
/// Files are private scratch data, not a durable shuffle service. A process crash leaves orphans.</remarks>
public sealed class ShuffleFileSet<T> : IDisposable
{
    private readonly string _ownedDirectory;
    private readonly Func<ReadOnlyMemory<byte>, T> _decode;
    private readonly int _maxRecordBytes;
    private bool _disposed;
    private ShuffleFileSet(string ownedDirectory, int partitions, Func<ReadOnlyMemory<byte>, T> decode, int maxRecordBytes)
    {
        _ownedDirectory = ownedDirectory;
        PartitionCount = partitions;
        _decode = decode;
        _maxRecordBytes = maxRecordBytes;
    }
    /// <summary>Gets the fixed number of partitions, including empty partitions.</summary>
    public int PartitionCount { get; }

    /// <summary>Writes each input row to exactly one partition; deletes scratch files on failure.</summary>
    /// <param name="input">Finite input consumed once.</param>
    /// <param name="scratchDirectory">Parent for a private owned subdirectory.</param>
    /// <param name="partitioner">Routes canonical equal keys together.</param>
    /// <param name="partitionCount">Positive output-file count.</param>
    /// <param name="encode">Row encoder.</param>
    /// <param name="decode">Matching row decoder.</param>
    /// <param name="maxRecordBytes">Positive encoded-record bound.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The owned partition set after all input is written.</returns>
    public static async ValueTask<ShuffleFileSet<T>> CreateAsync(IAsyncEnumerable<T> input, string scratchDirectory,
        IShufflePartitioner<T> partitioner, int partitionCount, Func<T, byte[]> encode,
        Func<ReadOnlyMemory<byte>, T> decode, int maxRecordBytes = 1024 * 1024, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);
        ArgumentNullException.ThrowIfNull(partitioner);
        ArgumentNullException.ThrowIfNull(encode);
        ArgumentNullException.ThrowIfNull(decode);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecordBytes);
        ct.ThrowIfCancellationRequested();
        var owned = Path.Combine(Path.GetFullPath(scratchDirectory), "shuffle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owned);
        var result = new ShuffleFileSet<T>(owned, partitionCount, decode, maxRecordBytes);
        try
        {
            for (var i = 0; i < partitionCount; i++) { using var empty = File.Create(result.PathFor(i)); }
            await foreach (var row in input.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var partition = partitioner.GetPartition(row, partitionCount);
                if (partition < 0 || partition >= partitionCount) { throw new InvalidOperationException("Partitioner returned an invalid partition."); }
                var payload = encode(row) ?? throw new InvalidOperationException("The codec returned null.");
                if (payload.Length > maxRecordBytes) { throw new InvalidDataException("Encoded row exceeds the shuffle-record limit."); }
                var writer = new FileStream(result.PathFor(partition), FileMode.Append, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous);
                await using (writer.ConfigureAwait(false))
                {
                    var header = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                    await writer.WriteAsync(header, ct).ConfigureAwait(false);
                    await writer.WriteAsync(payload, ct).ConfigureAwait(false);
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>Streams one partition, preserving its input order.</summary>
    /// <param name="partition">Zero-based partition.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Rows decoded from that partition.</returns>
    public async IAsyncEnumerable<T> ReadPartitionAsync(int partition, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(partition);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(partition, PartitionCount);
        var reader = new FileStream(PathFor(partition), FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var lifetime = reader.ConfigureAwait(false);
        while (reader.Position < reader.Length)
        {
            ct.ThrowIfCancellationRequested();
            var header = new byte[4];
            await reader.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < 0 || length > _maxRecordBytes || length > reader.Length - reader.Position)
            {
                throw new InvalidDataException("Invalid shuffle record length.");
            }
            var payload = new byte[length];
            await reader.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
            yield return _decode(payload);
        }
    }
    /// <summary>Deletes only the private partition directory; dispose readers first.</summary>
    public void Dispose()
    {
        if (_disposed) { return; }
        Directory.Delete(_ownedDirectory, recursive: true);
        _disposed = true;
    }
    private string PathFor(int partition) => Path.Combine(_ownedDirectory, partition.ToString(CultureInfo.InvariantCulture));
}
