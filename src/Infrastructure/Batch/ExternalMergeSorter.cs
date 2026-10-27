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

/// <summary>Sorts a finite stream using bounded sorted runs and a bounded fan-in merge.</summary>
/// <typeparam name="T">Item type whose codec must preserve its ordering key.</typeparam>
/// <remarks><para>At most maxItemsInMemory input objects plus mergeFanIn decoded heads are
/// retained at a time. Payloads are limited separately; the size of an arbitrary object graph is
/// the caller's responsibility. Run metadata is O(number of runs), not O(input rows).</para>
/// <para>Intermediate runs use length-prefixed private scratch files. Multi-pass merging bounds
/// open readers instead of opening every run at once. Runs are not durable database files.
/// Each enumeration owns a unique subdirectory and deletes it on success, failure or disposal;
/// a process crash can leave an orphan directory for the operator to remove.</para>
/// <para>O(N log N) comparisons, O(N log_f(N/M)) spill I/O. Equal keys are retained; stability is
/// not promised. Independent enumerations are safe if the supplied comparer and codecs are safe.</para>
/// <para>Reference: DDIA, batch processing, external sorting and merge joins.</para></remarks>
public sealed class ExternalMergeSorter<T> : IExternalSorter<T>
{
    private readonly string _scratchDirectory;
    private readonly IComparer<T> _comparer;
    private readonly Func<T, byte[]> _encode;
    private readonly Func<ReadOnlyMemory<byte>, T> _decode;
    private readonly int _maxItems;
    private readonly int _fanIn;
    private readonly int _maxRecordBytes;

    /// <summary>Creates a sorter with explicit run, merge and encoded-record limits.</summary>
    /// <param name="scratchDirectory">Parent directory; unrelated files are never removed.</param>
    /// <param name="comparer">Total deterministic ordering.</param>
    /// <param name="encode">Encodes an item; must not return null.</param>
    /// <param name="decode">Decodes exactly one item from its encoded bytes.</param>
    /// <param name="maxItemsInMemory">Maximum input items in a sorted run, positive.</param>
    /// <param name="mergeFanIn">Maximum concurrently open run readers, at least two.</param>
    /// <param name="maxRecordBytes">Maximum encoded item bytes, positive.</param>
    public ExternalMergeSorter(string scratchDirectory, IComparer<T> comparer, Func<T, byte[]> encode,
        Func<ReadOnlyMemory<byte>, T> decode, int maxItemsInMemory = 4096, int mergeFanIn = 16,
        int maxRecordBytes = 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);
        ArgumentNullException.ThrowIfNull(comparer);
        ArgumentNullException.ThrowIfNull(encode);
        ArgumentNullException.ThrowIfNull(decode);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItemsInMemory);
        ArgumentOutOfRangeException.ThrowIfLessThan(mergeFanIn, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecordBytes);
        _scratchDirectory = Path.GetFullPath(scratchDirectory);
        _comparer = comparer;
        _encode = encode;
        _decode = decode;
        _maxItems = maxItemsInMemory;
        _fanIn = mergeFanIn;
        _maxRecordBytes = maxRecordBytes;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<T> SortAsync(IAsyncEnumerable<T> input,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();
        var owned = Path.Combine(_scratchDirectory, "sort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owned);
        try
        {
            var runs = new List<string>();
            var chunk = new List<T>(_maxItems);
            var nextRun = 0;
            await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                chunk.Add(item);
                if (chunk.Count == _maxItems)
                {
                    var path = Path.Combine(owned, (nextRun++).ToString(CultureInfo.InvariantCulture));
                    await WriteRunAsync(path, chunk, ct).ConfigureAwait(false);
                    runs.Add(path);
                    chunk.Clear();
                }
            }
            if (chunk.Count > 0)
            {
                var path = Path.Combine(owned, (nextRun++).ToString(CultureInfo.InvariantCulture));
                await WriteRunAsync(path, chunk, ct).ConfigureAwait(false);
                runs.Add(path);
                chunk.Clear();
            }
            while (runs.Count > _fanIn)
            {
                var nextPass = new List<string>();
                foreach (var group in runs.Chunk(_fanIn))
                {
                    var output = Path.Combine(owned, (nextRun++).ToString(CultureInfo.InvariantCulture));
                    var writer = OpenWrite(output);
                    await using (writer.ConfigureAwait(false))
                    {
                        await foreach (var item in MergeAsync(group, ct).ConfigureAwait(false))
                        {
                            await WriteItemAsync(writer, item, ct).ConfigureAwait(false);
                        }
                    }
                    nextPass.Add(output);
                    foreach (var path in group) { File.Delete(path); }
                }
                runs = nextPass;
            }
            await foreach (var item in MergeAsync(runs, ct).ConfigureAwait(false)) { yield return item; }
        }
        finally { Directory.Delete(owned, recursive: true); }
    }

    private async Task WriteRunAsync(string path, List<T> chunk, CancellationToken ct)
    {
        chunk.Sort(_comparer);
        var writer = OpenWrite(path);
        await using var lifetime = writer.ConfigureAwait(false);
        foreach (var item in chunk) { await WriteItemAsync(writer, item, ct).ConfigureAwait(false); }
    }

    private async ValueTask WriteItemAsync(Stream writer, T item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var payload = _encode(item) ?? throw new InvalidOperationException("The codec returned null.");
        if (payload.Length > _maxRecordBytes) { throw new InvalidDataException("Encoded item exceeds the spill-record limit."); }
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await writer.WriteAsync(header, ct).ConfigureAwait(false);
        await writer.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<T> MergeAsync(IReadOnlyList<string> paths,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var readers = new List<FileStream>();
        var queue = new PriorityQueue<(T Value, int Reader), T>(_comparer);
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                var reader = new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                readers.Add(reader);
                var head = await ReadItemAsync(reader, ct).ConfigureAwait(false);
                if (head.Found) { queue.Enqueue((head.Value!, i), head.Value!); }
            }
            while (queue.TryDequeue(out var head, out _))
            {
                ct.ThrowIfCancellationRequested();
                yield return head.Value;
                var next = await ReadItemAsync(readers[head.Reader], ct).ConfigureAwait(false);
                if (next.Found) { queue.Enqueue((next.Value!, head.Reader), next.Value!); }
            }
        }
        finally
        {
            foreach (var reader in readers) { await reader.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private async ValueTask<(bool Found, T? Value)> ReadItemAsync(FileStream reader, CancellationToken ct)
    {
        if (reader.Position == reader.Length) { return (false, default); }
        var header = new byte[4];
        await reader.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > _maxRecordBytes || length > reader.Length - reader.Position)
        {
            throw new InvalidDataException("Invalid spill record length.");
        }
        var payload = new byte[length];
        await reader.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return (true, _decode(payload));
    }

    private static FileStream OpenWrite(string path) => new(path, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
}
