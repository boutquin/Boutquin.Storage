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
using System.IO.Compression;
using Boutquin.Storage.Infrastructure.Serialization;

namespace Boutquin.Storage.Infrastructure.SortedStringTable;

/// <summary>
/// Implements a Sorted String Table (SSTable) backed by a <see cref="StorageFile"/>.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the SSTable.</typeparam>
/// <typeparam name="TValue">The type of the values in the SSTable.</typeparam>
/// <remarks>
/// <para>
/// <b>Thread safety:</b> This class is not thread-safe. External synchronization is required
/// for concurrent access.
/// </para>
/// <para>
/// <b>On-disk format:</b> Entries are serialized sequentially using <see cref="BinaryEntrySerializer{TKey, TValue}"/>.
/// There is no header, footer, or per-entry checksum. The sparse index is maintained in memory only
/// and rebuilt on construction or after <see cref="Write"/>.
/// </para>
/// <para>
/// <b>Atomic writes:</b> <see cref="Write"/> uses a write-to-temp-then-rename pattern. Data is first
/// written to a temporary file (*.tmp), then atomically renamed to the final path. This ensures that
/// a crash mid-write cannot leave the SSTable in a partially written state — the previous version
/// remains intact until the new version is fully written and fsynced.
/// </para>
/// <para>
/// <b>Why delegate IStorageFile to an internal StorageFile?</b> The <see cref="ISortedStringTable{TKey, TValue}"/>
/// interface extends <see cref="IStorageFile"/>, which has many file-operation members. Rather than re-implementing
/// every file method, this class wraps a <see cref="KeyValueStore.StorageFile"/> instance and delegates all
/// <see cref="IStorageFile"/> members to it. The SSTable-specific logic (Write, TryGetValue, Merge, etc.)
/// is implemented directly.
/// </para>
/// <para>
/// <b>Why a sparse index?</b> A full index would map every key to its file offset, consuming memory proportional
/// to the number of entries. A sparse index stores only every Nth key's offset (controlled by
/// <c>sparseIndexInterval</c>), reducing memory usage. Lookups binary-search the sparse index to find the
/// candidate segment, then scan linearly within that segment. This trades a small amount of read latency
/// for significantly lower memory overhead.
/// </para>
/// <para>
/// <b>Why binary search on the sparse index?</b> The sparse index is sorted (because the SSTable data is
/// written in sorted order). Binary search gives O(log N) lookup on the index entries, followed by a
/// bounded linear scan of at most <c>sparseIndexInterval</c> entries on disk.
/// </para>
/// </remarks>
public sealed class SortedStringTable<TKey, TValue> : ISortedStringTable<TKey, TValue>
    where TKey : IComparable<TKey>, ISerializable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    private readonly KeyValueStore.StorageFile _storageFile;
    private readonly BinaryEntrySerializer<TKey, TValue> _serializer;
    private readonly int _sparseIndexInterval;
    private readonly bool _enableCompression;

    /// <summary>
    /// In-memory sparse index: maps sampled keys to their byte offsets in the data file.
    /// </summary>
    private readonly List<(TKey Key, long Offset)> _sparseIndex = [];

    /// <summary>
    /// Tracks the number of entries written to the SSTable.
    /// </summary>
    private int _entryCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="SortedStringTable{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="fileLocation">The directory path where the SSTable file is stored.</param>
    /// <param name="fileName">The name of the SSTable file.</param>
    /// <param name="sparseIndexInterval">
    /// The interval at which keys are sampled into the sparse index. For example, a value of 4 means
    /// every 4th key is indexed. Lower values improve lookup speed at the cost of more memory.
    /// Recommended: 16–128 for production. For an SSTable with 100K entries, interval=64 produces
    /// ~1,500 index entries (~50 KB of memory). Interval=16 gives faster lookups (fewer entries
    /// to scan between index points) but 4× the memory.
    /// </param>
    /// <param name="enableCompression">
    /// When true, data is compressed with GZip before writing to disk and decompressed on read.
    /// The sparse index offsets refer to the uncompressed stream positions. Compressed files are
    /// fully decompressed into memory before lookup.
    /// </param>
    public SortedStringTable(string fileLocation, string fileName, int sparseIndexInterval = 4, bool enableCompression = false)
    {
        Guard.AgainstNullOrWhiteSpace(() => fileLocation);
        Guard.AgainstNullOrWhiteSpace(() => fileName);
        Guard.AgainstNegativeOrZero(() => sparseIndexInterval);

        _storageFile = new KeyValueStore.StorageFile(fileLocation, fileName);
        _serializer = new BinaryEntrySerializer<TKey, TValue>();
        _sparseIndexInterval = sparseIndexInterval;
        _enableCompression = enableCompression;

        // If a companion .idx file exists, load the persisted sparse index.
        var idxPath = GetIndexFilePath();
        if (File.Exists(idxPath) && _storageFile.Exists())
        {
            LoadSparseIndex(idxPath);
        }
    }

    /// <inheritdoc/>
    public void Write(IEnumerable<KeyValuePair<TKey, TValue>> items)
    {
        Guard.AgainstNullOrDefault(() => items);

        // Materialize the enumerable so we can validate sort order before writing.
        var itemsList = items as IList<KeyValuePair<TKey, TValue>> ?? items.ToList();

        // Validate that input is strictly sorted by key (each key must be greater than the previous).
        // The SSTable on-disk format requires sorted order for binary search on the sparse index
        // and for correct merge behavior. Rejecting unsorted input (rather than silently sorting)
        // makes ordering bugs visible at the call site.
        for (var i = 1; i < itemsList.Count; i++)
        {
            if (itemsList[i].Key.CompareTo(itemsList[i - 1].Key) <= 0)
            {
                throw new ArgumentException(
                    $"Items must be sorted in strictly ascending key order. " +
                    $"Key at index {i} is not greater than key at index {i - 1}.",
                    nameof(items));
            }
        }

        var newSparseIndex = new List<(TKey Key, long Offset)>();
        var newEntryCount = 0;

        // Write to a temporary file first, then atomically rename to the final path.
        var finalPath = _storageFile.FilePath;
        var tempPath = finalPath + ".tmp";

        // Serialize entries to a memory stream to capture offsets for the sparse index.
        using var memoryStream = new MemoryStream();
        var index = 0;
        foreach (var item in itemsList)
        {
            if (index % _sparseIndexInterval == 0)
            {
                newSparseIndex.Add((item.Key, memoryStream.Position));
            }

            _serializer.WriteEntry(memoryStream, item.Key, item.Value);

            index++;
            newEntryCount++;
        }

        // Write the serialized data to the temp file, optionally compressing.
        using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            memoryStream.Position = 0;
            if (_enableCompression)
            {
                using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal, leaveOpen: true);
                memoryStream.CopyTo(gzipStream);
            }
            else
            {
                memoryStream.CopyTo(fileStream);
            }

            fileStream.Flush(flushToDisk: true);
        }

        // Atomic rename: replaces the old file only after the new file is fully written and fsynced.
        File.Move(tempPath, finalPath, overwrite: true);

        // Update in-memory state only after the file is safely on disk.
        _sparseIndex.Clear();
        _sparseIndex.AddRange(newSparseIndex);
        _entryCount = newEntryCount;

        // Persist the sparse index to a companion .idx file.
        SaveSparseIndex(GetIndexFilePath());
    }

    /// <inheritdoc/>
    public bool TryGetValue(TKey key, out TValue value)
    {
        Guard.AgainstNullOrDefault(() => key);

        value = default!;

        if (_sparseIndex.Count == 0)
        {
            return false;
        }

        // Binary search the sparse index to find the segment that may contain the key.
        var segmentIndex = FindSegmentIndex(key);
        var segmentStart = _sparseIndex[segmentIndex].Offset;

        // Open the data stream — decompress if necessary. When compressed, the file is
        // fully decompressed into a MemoryStream so that we can seek to the correct offset.
        using var dataStream = OpenDataStream();

        // Determine the end of the segment to scan.
        long segmentEnd;
        if (segmentIndex + 1 < _sparseIndex.Count)
        {
            segmentEnd = _sparseIndex[segmentIndex + 1].Offset;
        }
        else
        {
            segmentEnd = dataStream.Length;
        }

        // Linear scan within the segment.
        dataStream.Seek(segmentStart, SeekOrigin.Begin);

        while (dataStream.Position < segmentEnd && _serializer.CanRead(dataStream))
        {
            try
            {
                var entry = _serializer.ReadEntry(dataStream);
                if (entry == null)
                {
                    break;
                }

                var comparison = entry.Value.Key.CompareTo(key);
                if (comparison == 0)
                {
                    value = entry.Value.Value;
                    return true;
                }

                // Since data is sorted, if we've passed the key, it's not here.
                if (comparison > 0)
                {
                    break;
                }
            }
            catch
            {
                // Corrupt or unreadable entry — treat as not found.
                break;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public void Merge(ISortedStringTable<TKey, TValue> otherTable)
    {
        Guard.AgainstNullOrDefault(() => otherTable);

        // Read all entries from this table.
        var thisEntries = ReadAllEntries();

        // Read all entries from the other table.
        var otherEntries = ReadAllEntriesFrom(otherTable);

        // Sorted merge: combine both sorted sequences, with other table's entries taking precedence
        // for duplicate keys (last-writer-wins).
        var merged = MergeSorted(thisEntries, otherEntries);

        // Re-write this table with the merged data.
        Write(merged);
    }

    /// <inheritdoc/>
    public int GetEntryCount() => _entryCount;

    /// <inheritdoc/>
    public DateTime GetCreationTime() => File.GetCreationTime(_storageFile.FilePath);

    /// <inheritdoc/>
    public DateTime GetLastModificationTime() => File.GetLastWriteTime(_storageFile.FilePath);

    // ---- IStorageFile delegation ----

    /// <inheritdoc/>
    public string FilePath => _storageFile.FilePath;

    /// <inheritdoc/>
    public long FileSize => _storageFile.FileSize;

    /// <inheritdoc/>
    public string FileName => _storageFile.FileName;

    /// <inheritdoc/>
    public string FileLocation => _storageFile.FileLocation;

    /// <inheritdoc/>
    public void Create(FileExistenceHandling existenceHandling) => _storageFile.Create(existenceHandling);

    /// <inheritdoc/>
    public bool Exists() => _storageFile.Exists();

    /// <inheritdoc/>
    public Stream Open(FileMode mode) => _storageFile.Open(mode);

    /// <inheritdoc/>
    public void Close() => _storageFile.Close();

    /// <inheritdoc/>
    public void Delete(FileDeletionHandling deletionHandling) => _storageFile.Delete(deletionHandling);

    /// <inheritdoc/>
    public byte[] ReadBytes(long offset, long count) => _storageFile.ReadBytes(offset, count);

    /// <inheritdoc/>
    public Task<byte[]> ReadBytesAsync(long offset, long count, CancellationToken cancellationToken = default) =>
        _storageFile.ReadBytesAsync(offset, count, cancellationToken);

    /// <inheritdoc/>
    public byte[] ReadAllBytes() => _storageFile.ReadAllBytes();

    /// <inheritdoc/>
    public Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default) =>
        _storageFile.ReadAllBytesAsync(cancellationToken);

    /// <inheritdoc/>
    public string ReadAllText(Encoding encoding) => _storageFile.ReadAllText(encoding);

    /// <inheritdoc/>
    public Task<string> ReadAllTextAsync(Encoding encoding, CancellationToken cancellationToken = default) =>
        _storageFile.ReadAllTextAsync(encoding, cancellationToken);

    /// <inheritdoc/>
    public void WriteAllBytes(byte[] content) => _storageFile.WriteAllBytes(content);

    /// <inheritdoc/>
    public Task WriteAllBytesAsync(byte[] content, CancellationToken cancellationToken = default) =>
        _storageFile.WriteAllBytesAsync(content, cancellationToken);

    /// <inheritdoc/>
    public void WriteAllText(string content, Encoding encoding) => _storageFile.WriteAllText(content, encoding);

    /// <inheritdoc/>
    public Task WriteAllTextAsync(string content, Encoding encoding, CancellationToken cancellationToken = default) =>
        _storageFile.WriteAllTextAsync(content, encoding, cancellationToken);

    /// <inheritdoc/>
    public void AppendAllBytes(byte[] content) => _storageFile.AppendAllBytes(content);

    /// <inheritdoc/>
    public Task AppendAllBytesAsync(byte[] content, CancellationToken cancellationToken = default) =>
        _storageFile.AppendAllBytesAsync(content, cancellationToken);

    /// <inheritdoc/>
    public void AppendAllText(string content, Encoding encoding) => _storageFile.AppendAllText(content, encoding);

    /// <inheritdoc/>
    public Task AppendAllTextAsync(string content, Encoding encoding, CancellationToken cancellationToken = default) =>
        _storageFile.AppendAllTextAsync(content, encoding, cancellationToken);

    /// <inheritdoc/>
    public void Dispose() => _storageFile.Dispose();

    // ---- Private helpers ----

    /// <summary>
    /// Finds the sparse index entry index for the segment that may contain the given key.
    /// Uses binary search on the sorted sparse index.
    /// </summary>
    private int FindSegmentIndex(TKey key)
    {
        var lo = 0;
        var hi = _sparseIndex.Count - 1;
        var result = 0;

        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var comparison = _sparseIndex[mid].Key.CompareTo(key);

            if (comparison <= 0)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Opens the data stream, decompressing if necessary. Returns a seekable stream.
    /// </summary>
    private Stream OpenDataStream()
    {
        if (!_enableCompression)
        {
            return _storageFile.Open(FileMode.Open);
        }

        // Decompress the entire file into a MemoryStream for seekable access.
        using var fileStream = _storageFile.Open(FileMode.Open);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        var memStream = new MemoryStream();
        gzipStream.CopyTo(memStream);
        memStream.Position = 0;
        return memStream;
    }

    /// <summary>
    /// Reads all key-value entries from this SSTable.
    /// </summary>
    private List<KeyValuePair<TKey, TValue>> ReadAllEntries()
    {
        var entries = new List<KeyValuePair<TKey, TValue>>();

        if (_entryCount == 0)
        {
            return entries;
        }

        using var stream = OpenDataStream();
        stream.Seek(0, SeekOrigin.Begin);

        while (_serializer.CanRead(stream))
        {
            var entry = _serializer.ReadEntry(stream);
            if (entry == null)
            {
                break;
            }

            entries.Add(new KeyValuePair<TKey, TValue>(entry.Value.Key, entry.Value.Value));
        }

        return entries;
    }

    /// <summary>
    /// Reads all key-value entries from another SSTable by iterating its known keys.
    /// </summary>
    private static List<KeyValuePair<TKey, TValue>> ReadAllEntriesFrom(ISortedStringTable<TKey, TValue> otherTable)
    {
        // We need to read raw entries from the other table. Since the interface doesn't expose
        // an enumerator, we read the file bytes via the IStorageFile interface and deserialize.
        var entries = new List<KeyValuePair<TKey, TValue>>();
        var serializer = new BinaryEntrySerializer<TKey, TValue>();

        var bytes = otherTable.ReadAllBytes();
        using var stream = new MemoryStream(bytes);

        while (serializer.CanRead(stream))
        {
            var entry = serializer.ReadEntry(stream);
            if (entry == null)
            {
                break;
            }

            entries.Add(new KeyValuePair<TKey, TValue>(entry.Value.Key, entry.Value.Value));
        }

        return entries;
    }

    /// <summary>
    /// Performs a sorted merge of two sorted sequences. When both contain the same key,
    /// the entry from <paramref name="other"/> takes precedence (last-writer-wins).
    /// </summary>
    private static List<KeyValuePair<TKey, TValue>> MergeSorted(
        List<KeyValuePair<TKey, TValue>> current,
        List<KeyValuePair<TKey, TValue>> other)
    {
        var result = new List<KeyValuePair<TKey, TValue>>();
        var i = 0;
        var j = 0;

        while (i < current.Count && j < other.Count)
        {
            var comparison = current[i].Key.CompareTo(other[j].Key);
            if (comparison < 0)
            {
                result.Add(current[i]);
                i++;
            }
            else if (comparison > 0)
            {
                result.Add(other[j]);
                j++;
            }
            else
            {
                // Duplicate key: other table wins.
                result.Add(other[j]);
                i++;
                j++;
            }
        }

        while (i < current.Count)
        {
            result.Add(current[i]);
            i++;
        }

        while (j < other.Count)
        {
            result.Add(other[j]);
            j++;
        }

        return result;
    }

    /// <summary>
    /// Returns the file path for the companion sparse index file (.idx).
    /// </summary>
    private string GetIndexFilePath()
    {
        return Path.ChangeExtension(_storageFile.FilePath, ".idx");
    }

    /// <summary>
    /// Persists the sparse index to a companion .idx file.
    /// Format: [4 bytes: entry count][foreach entry: [serialized key bytes][8 bytes: offset (long, little-endian)]]
    /// </summary>
    private void SaveSparseIndex(string idxPath)
    {
        if (_sparseIndex.Count == 0 && _entryCount == 0)
        {
            return;
        }

        using var stream = new FileStream(idxPath, FileMode.Create, FileAccess.Write, FileShare.None);

        // Write entry count (total data entries, not sparse index entries).
        var countBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(countBytes, _entryCount);
        stream.Write(countBytes);

        // Write sparse index entry count.
        var sparseCountBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(sparseCountBytes, _sparseIndex.Count);
        stream.Write(sparseCountBytes);

        // Write each sparse index entry.
        foreach (var (key, offset) in _sparseIndex)
        {
            key.Serialize(stream);
            var offsetBytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(offsetBytes, offset);
            stream.Write(offsetBytes);
        }

        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Loads the sparse index from a companion .idx file.
    /// </summary>
    private void LoadSparseIndex(string idxPath)
    {
        using var stream = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Read entry count.
        var countBytes = new byte[4];
        if (stream.Read(countBytes) < 4)
        {
            return;
        }

        _entryCount = BinaryPrimitives.ReadInt32LittleEndian(countBytes);

        // Read sparse index entry count.
        var sparseCountBytes = new byte[4];
        if (stream.Read(sparseCountBytes) < 4)
        {
            return;
        }

        var sparseCount = BinaryPrimitives.ReadInt32LittleEndian(sparseCountBytes);

        _sparseIndex.Clear();
        for (var i = 0; i < sparseCount; i++)
        {
            var key = TKey.Deserialize(stream);
            var offsetBytes = new byte[8];
            if (stream.Read(offsetBytes) < 8)
            {
                break;
            }

            var offset = BinaryPrimitives.ReadInt64LittleEndian(offsetBytes);
            _sparseIndex.Add((key, offset));
        }
    }
}
