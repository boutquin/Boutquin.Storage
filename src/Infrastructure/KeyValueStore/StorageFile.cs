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
namespace Boutquin.Storage.Infrastructure.KeyValueStore;

/// <summary>
/// Provides basic file operations for managing and manipulating files within a specified directory.
/// This class ensures that the file and its directory exist, and offers various methods to read, write, and delete files.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one per-path semaphore?</b> A single static <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// of <see cref="SemaphoreSlim"/> keyed by file path is the ONE lock that protects both
/// cross-instance access to the same physical file (multiple <see cref="StorageFile"/> instances
/// pointing to the same path won't corrupt each other) AND the <c>_fileStream</c> field. Every
/// method that mutates <c>_fileStream</c> — <c>Open</c>, <c>Close</c>, <c>Create</c>, the
/// <c>WriteAllBytes…</c> atomic-replace paths, and <c>Dispose</c> — holds this same per-path
/// semaphore, so the stream field is never read or disposed under one lock while mutated under
/// another. (An earlier design used a second instance-level semaphore for <c>Open</c>/<c>Close</c>,
/// which left <c>_fileStream</c> guarded by two different locks — a race where a just-opened stream
/// could be disposed out from under its user. Collapsing to the single per-path semaphore fixes it.)
/// </para>
/// <para>
/// <b>Why SemaphoreSlim(1,1) not lock/Monitor?</b> <see cref="SemaphoreSlim"/> supports async
/// waiting (<see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>), which is critical for the
/// async file operations. A <c>lock</c> statement cannot be used with <c>await</c> — holding a
/// lock across an await is both illegal (compiler error) and semantically wrong (the thread may
/// change). <c>SemaphoreSlim(1,1)</c> provides mutual exclusion with async support.
/// </para>
/// <para>
/// <b>Why create the file eagerly in the constructor?</b> The constructor calls
/// <see cref="Create(FileExistenceHandling)"/> with <see cref="FileExistenceHandling.DoNothingIfExists"/>
/// to ensure the file exists before any read/write operation. This eliminates a class of race
/// conditions where a read is attempted before the first write. The DoNothingIfExists policy makes
/// this idempotent — constructing multiple <see cref="StorageFile"/> instances for the same path
/// won't truncate existing data.
/// </para>
/// <para>
/// <b>Why is FileStream managed as a nullable field (_fileStream)?</b> The file needs to be opened
/// and closed at different times (<see cref="Open"/> / <see cref="Close"/> methods), so the stream
/// lifetime is not tied to a single method scope. The nullable field allows tracking whether a stream
/// is currently open, and <c>DisposeFileStreamIfExists</c> safely cleans up regardless of current state.
/// </para>
/// </remarks>
public sealed class StorageFile : IStorageFile, IDisposable
{
    // Why 30 seconds? File operations should complete within seconds. A 30-second timeout
    // detects genuine deadlocks (e.g., a semaphore leaked by a crash) without false positives
    // from slow I/O on degraded storage.
    private static readonly TimeSpan s_semaphoreTimeout = TimeSpan.FromSeconds(30);

    private FileStream? _fileStream;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_fileSemaphores = new();
    private int _disposed; // 0 = live, 1 = disposed; use Interlocked.Exchange to set atomically.

    // Cached on-disk size. DR-9: this field inherits StorageFile's EXISTING concurrency model — it is
    // read and written only under the same per-path semaphore (s_fileSemaphores) that already guards
    // every mutation of the file and of _fileStream. No new unsynchronised shared field is introduced.
    // It is updated precisely by every StorageFile-tracked write/append/delete and otherwise lazily
    // (re)stat'd by the FileSize getter when the cache is stale (see _cachedSizeStale), replacing the
    // prior unconditional per-access `new FileInfo(FilePath).Length` stat on the steady-state path.
    private long _fileSize;

    // True when the cached size cannot be vouched for without consulting disk — set after construction
    // and after every Open(...) (a handed-out stream may write through it), and crucially the default,
    // because StorageFile is NOT the exclusive writer of its path: other components (e.g. the SSTable
    // writer) atomically replace the file out-of-band, which StorageFile cannot observe. While stale,
    // FileSize stats and STAYS stale, so a foreign replace is always caught. Only a StorageFile-tracked
    // write (append/atomic-write/delete) — which updates _fileSize precisely — clears it, so back-to-back
    // FileSize reads immediately after such a write serve from cache. Set/cleared only under the
    // per-path semaphore (DR-9).
    private bool _cachedSizeStale = true;

    // Directory fsyncs this instance has issued for directory-entry mutations (file create/delete,
    // directory creation). Observable by tests, mirroring FileSystemObjectStore's delete-fsync count.
    internal int _directoryFsyncCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageFile"/> class.
    /// </summary>
    /// <param name="fileLocation">The directory path where the file is located.</param>
    /// <param name="fileName">The name of the file.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="fileLocation"/> or <paramref name="fileName"/> is null, empty, or whitespace.</exception>
    public StorageFile(string fileLocation, string fileName)
        : this(fileLocation, fileName, createIfMissing: true)
    {
    }

    // Immutable-table construction must distinguish an absent output from a corrupt empty file.
    // It publishes its own complete bytes atomically rather than creating an empty placeholder.
    internal StorageFile(string fileLocation, string fileName, bool createIfMissing)
    {
        Guard.AgainstNullOrWhiteSpace(() => fileLocation); // Validate the file location to ensure it is not null, empty, or whitespace.
        Guard.AgainstNullOrWhiteSpace(() => fileName); // Validate the file name to ensure it is not null, empty, or whitespace.

        FileLocation = fileLocation;
        FileName = fileName;
        FilePath = Path.Combine(FileLocation, FileName);

        // Ensure the directory exists.
        EnsureDirectoryExists(fileLocation);

        if (createIfMissing)
        {
            Create(FileExistenceHandling.DoNothingIfExists);
        }
    }

    /// <summary>
    /// Gets the size of the file in bytes.
    /// </summary>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// long fileSize = storageFile.FileSize;
    /// Console.WriteLine($"File size: {fileSize} bytes");
    /// </code>
    /// </example>
    public long FileSize
    {
        get
        {
            var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
            WaitWithTimeout(semaphore);
            try
            {
                // When stale (the default, and after any Open or a possible out-of-band replace), stat
                // to get the true size — and STAY stale, so a later foreign write is caught too. A
                // StorageFile-tracked write clears the flag and seeds _fileSize precisely, so the
                // common "write-then-read-size" pair on the tracked path serves the read from cache.
                if (_cachedSizeStale)
                {
                    RefreshCachedFileSize();
                }

                return _fileSize;
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    /// <summary>
    /// Refreshes the cached file size from disk. MUST be called while holding the per-path semaphore;
    /// every mutating path (create, atomic replace, append, delete) calls this after the write lands
    /// so the cache stays consistent with the on-disk size under the type's existing write discipline.
    /// </summary>
    private void RefreshCachedFileSize()
        => _fileSize = File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;

    /// <summary>
    /// Fsyncs the containing directory when the operation just performed created or removed this
    /// file's directory entry. A file fsync does not persist the entry itself
    /// (Pillai et al. 2014 §3), so without this a crash can drop a newly created file with every
    /// acknowledged write in it, or resurrect a deleted one. POSIX only; Windows skips the
    /// directory barrier and its power-loss persistence requires separate qualification,
    /// matching <c>DurableFile.AtomicReplace</c>. MUST be called while holding the per-path semaphore.
    /// </summary>
    private void FsyncDirectoryIfEntryChanged(bool existedBefore)
    {
        if (OperatingSystem.IsWindows() || existedBefore == File.Exists(FilePath))
        {
            return;
        }

        DurableFile.FsyncDirectory(FilePath);
        Interlocked.Increment(ref _directoryFsyncCount);
    }

    /// <summary>
    /// Gets the name of the file.
    /// </summary>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// string fileName = storageFile.FileName;
    /// Console.WriteLine($"File name: {fileName}");
    /// </code>
    /// </example>
    public string FileName { get; }

    /// <summary>
    /// Gets the directory path where the file is located.
    /// </summary>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// string fileLocation = storageFile.FileLocation;
    /// Console.WriteLine($"File location: {fileLocation}");
    /// </code>
    /// </example>
    public string FileLocation { get; }

    /// <summary>
    /// Gets the full path of the file.
    /// </summary>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// string filePath = storageFile.FilePath;
    /// Console.WriteLine($"File Path: {filePath}");
    /// </code>
    /// </example>
    public string FilePath { get; }

    /// <summary>
    /// Creates the file based on the specified existence handling.
    /// </summary>
    /// <param name="existenceHandling">The handling strategy for file existence.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="existenceHandling"/> is not a defined enum value.</exception>
    /// <exception cref="IOException">Thrown when an error occurs while creating the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// storageFile.Create(FileExistenceHandling.Overwrite);
    /// </code>
    /// </example>
    public void Create(FileExistenceHandling existenceHandling)
    {
        Guard.AgainstUndefinedEnumValue(() => existenceHandling); // Validate the existence handling to ensure it is a defined enum value.

        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        WaitWithTimeout(semaphore);
        try
        {
            var existed = File.Exists(FilePath);
            switch (existenceHandling)
            {
                case FileExistenceHandling.Overwrite:
                    _fileStream?.Dispose();
                    _fileStream = new FileStream(FilePath, FileMode.Create, FileAccess.Write);
                    _fileStream.Dispose();
                    break;
                case FileExistenceHandling.DoNothingIfExists:
                    if (!File.Exists(FilePath))
                    {
                        _fileStream?.Dispose();
                        _fileStream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write);
                        _fileStream.Dispose();
                    }
                    break;
                case FileExistenceHandling.ThrowIfExists:
                    if (File.Exists(FilePath))
                    {
                        throw new IOException($"File '{FilePath}' already exists.");
                    }
                    _fileStream?.Dispose();
                    _fileStream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write);
                    _fileStream.Dispose();
                    break;
            }

            // Leave the cache stale after Create: the first FileSize read will stat the true size.
            // Create does not seed-and-trust the cache because the file may subsequently be replaced
            // out-of-band (e.g. an SSTable writer renames a fresh segment over this path), which the
            // stay-stale FileSize read is designed to catch.
            _cachedSizeStale = true;
            FsyncDirectoryIfEntryChanged(existed);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Checks if the file exists.
    /// </summary>
    /// <returns>True if the file exists, otherwise false.</returns>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// bool exists = storageFile.Exists();
    /// Console.WriteLine($"File exists: {exists}");
    /// </code>
    /// </example>
    public bool Exists()
    {
        return File.Exists(FilePath);
    }

    /// <summary>
    /// Opens the file with the specified mode.
    /// </summary>
    /// <param name="mode">The mode in which to open the file.</param>
    /// <returns>A stream for the opened file.</returns>
    /// <exception cref="IOException">Thrown when an error occurs while opening the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// using var stream = storageFile.Open(FileMode.OpenOrCreate);
    /// // Perform read/write operations with the stream
    /// </code>
    /// </example>
    public Stream Open(FileMode mode)
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        WaitWithTimeout(semaphore);
        try
        {
            DisposeFileStreamIfExists();
            var existed = File.Exists(FilePath);
            // Why different FileAccess for Append mode? FileMode.Append only supports FileAccess.Write
            // (attempting ReadWrite throws). For all other modes, ReadWrite is used to support both
            // reading and writing through the same stream.
            //
            // Why FileShare.None for Append mode? The handed-out append stream captures its write
            // offset at open time and the caller writes through it AFTER this method releases the
            // per-path semaphore — so a second writer handle over the same path (another
            // StorageFile instance in this process, or another process) opened in that window
            // would capture the same end offset and the two writers would overwrite each other's
            // committed bytes (the multi-writer stale-offset class fixed in AppendOnlyEventLog,
            // 2026-07-16). FileShare.None makes the second open fail fast instead: a mandatory
            // sharing violation on Windows, an exclusive OS flock on Unix — the ONLY share mode
            // that yields any write exclusion on Unix. Non-append modes keep the historical
            // FileShare.Read default.
            _fileStream = mode == FileMode.Append
                ? new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.None)
                : new FileStream(FilePath, mode, FileAccess.ReadWrite);
            // Ownership note: the returned stream is owned by the CALLER for the duration of its use —
            // the caller writes/reads and disposes it (the append-only and segmented engines do exactly
            // this under their own serialisation point). StorageFile keeps a reference in _fileStream
            // only so Close/Dispose/atomic-replace can release any lingering handle; it does NOT track
            // bytes written through the handed-out stream. Mark the cached size stale so the next
            // FileSize read resyncs from disk once, covering any out-of-band growth the caller wrote.
            _cachedSizeStale = true;
            FsyncDirectoryIfEntryChanged(existed);
            return _fileStream;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Closes the file.
    /// </summary>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// storageFile.Close();
    /// </code>
    /// </example>
    public void Close()
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        WaitWithTimeout(semaphore);
        try
        {
            DisposeFileStreamIfExists();
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Deletes the file based on the specified deletion handling.
    /// </summary>
    /// <param name="deletionHandling">The handling strategy for file deletion.</param>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist and the deletion handling is set to throw an exception.</exception>
    /// <exception cref="IOException">Thrown when an error occurs while deleting the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// storageFile.Delete(FileDeletionHandling.DeleteIfExists);
    /// </code>
    /// </example>
    public void Delete(FileDeletionHandling deletionHandling)
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        WaitWithTimeout(semaphore);
        try
        {
            if (!File.Exists(FilePath) && deletionHandling == FileDeletionHandling.ThrowIfNotExists)
            {
                throw new FileNotFoundException($"File '{FilePath}' does not exist.");
            }

            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
                FsyncDirectoryIfEntryChanged(existedBefore: true);
            }

            // The file is gone. Mark stale so the next FileSize read re-stats (it may be re-created
            // out-of-band before then); RefreshCachedFileSize handles the missing-file case as size 0.
            _fileSize = 0;
            _cachedSizeStale = true;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Reads the specified number of bytes from the file starting at the given offset.
    /// </summary>
    /// <param name="offset">The offset at which to start reading.</param>
    /// <param name="count">The number of bytes to read.</param>
    /// <returns>A byte array containing the data read from the file.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the offset is greater than the length of the file.</exception>
    /// <exception cref="IOException">Thrown when an error occurs while reading the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// byte[] data = storageFile.ReadBytes(0, 100);
    /// Console.WriteLine($"Read {data.Length} bytes from file.");
    /// </code>
    /// </example>
    public byte[] ReadBytes(long offset, long count)
    {
        Guard.AgainstNegative(() => offset); // Validate offset.
        Guard.AgainstNegative(() => count); // Validate count.
        if (count > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be between 0 and int.MaxValue.");
        }

        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        WaitWithTimeout(semaphore);
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read);
            // A zero-byte read is always satisfiable — reading nothing from an empty (or any) file
            // returns an empty buffer rather than throwing. This is the legitimate "the log is empty,
            // read nothing" call (offset 0, count 0 of a freshly-created file): the pre-fix guard
            // rejected offset >= Length, so 0 >= 0 surfaced an empty store as an error.
            if (count == 0)
            {
                return [];
            }

            if (offset >= stream.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset is greater than the length of the file.");
            }

            stream.Seek(offset, SeekOrigin.Begin);

            // Loop until the requested span is exhausted or EOF — a single Read call may
            // legally return fewer bytes than requested, and silently truncating the buffer
            // hands the caller a partial record it then mis-deserializes.
            var available = (int)Math.Min(count, stream.Length - offset);
            var buffer = new byte[available];
            stream.ReadExactly(buffer, 0, available);

            return buffer;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Asynchronously reads the specified number of bytes from the file starting at the given offset.
    /// </summary>
    /// <param name="offset">The offset at which to start reading.</param>
    /// <param name="count">The number of bytes to read.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A byte array containing the data read from the file.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the offset is greater than the length of the file.</exception>
    /// <exception cref="IOException">Thrown when an error occurs while reading the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// byte[] data = await storageFile.ReadBytesAsync(0, 100);
    /// Console.WriteLine($"Read {data.Length} bytes from file.");
    /// </code>
    /// </example>
    public async Task<byte[]> ReadBytesAsync(long offset, long count, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNegative(() => offset); // Validate offset.
        Guard.AgainstNegative(() => count); // Validate count.
        if (count > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be between 0 and int.MaxValue.");
        }

        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            await using var _ = stream.ConfigureAwait(false);
            // See ReadBytes: a zero-byte read is always satisfiable — the empty-file / empty-store
            // case returns an empty buffer instead of throwing on offset >= Length.
            if (count == 0)
            {
                return [];
            }

            if (offset >= stream.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset is greater than the length of the file.");
            }

            stream.Seek(offset, SeekOrigin.Begin);

            // Loop until the requested span is exhausted or EOF — a single ReadAsync call
            // may legally return fewer bytes than requested (more likely than on the sync
            // path), and silently truncating hands the caller a partial record.
            var available = (int)Math.Min(count, stream.Length - offset);
            var buffer = new byte[available];
            await stream.ReadExactlyAsync(buffer.AsMemory(0, available), cancellationToken).ConfigureAwait(false);

            return buffer;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Reads all bytes from the file.
    /// </summary>
    /// <returns>A byte array containing all the data read from the file.</returns>
    /// <exception cref="IOException">Thrown when an error occurs while reading the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// byte[] data = storageFile.ReadAllBytes();
    /// Console.WriteLine($"Read {data.Length} bytes from file.");
    /// </code>
    /// </example>
    public byte[] ReadAllBytes()
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        WaitWithTimeout(semaphore);
        try
        {
            return File.ReadAllBytes(FilePath);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Asynchronously reads all bytes from the file.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A byte array containing all the data read from the file.</returns>
    /// <exception cref="IOException">Thrown when an error occurs while reading the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// byte[] data = await storageFile.ReadAllBytesAsync();
    /// Console.WriteLine($"Read {data.Length} bytes from file.");
    /// </code>
    /// </example>
    public async Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default)
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await File.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Reads all text from the file using the specified encoding.
    /// </summary>
    /// <param name="encoding">The encoding to use for reading the file.</param>
    /// <returns>A string containing all the text read from the file.</returns>
    /// <exception cref="IOException">Thrown when an error occurs while reading the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// string content = storageFile.ReadAllText(Encoding.UTF8);
    /// Console.WriteLine($"File content: {content}");
    /// </code>
    /// </example>
    public string ReadAllText(Encoding encoding)
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        WaitWithTimeout(semaphore);
        try
        {
            return File.ReadAllText(FilePath, encoding);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Asynchronously reads all text from the file using the specified encoding.
    /// </summary>
    /// <param name="encoding">The encoding to use for reading the file.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A string containing all the text read from the file.</returns>
    /// <exception cref="IOException">Thrown when an error occurs while reading the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// string content = await storageFile.ReadAllTextAsync(Encoding.UTF8);
    /// Console.WriteLine($"File content: {content}");
    /// </code>
    /// </example>
    public async Task<string> ReadAllTextAsync(Encoding encoding, CancellationToken cancellationToken = default)
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await File.ReadAllTextAsync(FilePath, encoding, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Writes all bytes to the file, overwriting any existing content.
    /// </summary>
    /// <param name="content">The byte array to write to the file.</param>
    /// <exception cref="IOException">Thrown when an error occurs while writing to the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// storageFile.WriteAllBytes(new byte[] { 0x01, 0x02, 0x03 });
    /// </code>
    /// </example>
    public void WriteAllBytes(byte[] content)
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        WaitWithTimeout(semaphore);
        try
        {
            AtomicWrite(content);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Crash-atomic replace: write <paramref name="content"/> to a sibling temp file, fsync it, then
    /// rename it over the target. POSIX rename is atomic, so a crash (or any reader that bypasses this
    /// type's per-path semaphore, e.g. a recovery scan) sees either the whole old file or the whole
    /// new file — never a truncated/partial one. The fsync makes the new bytes durable before the
    /// rename publishes them. Callers must hold the per-path semaphore.
    /// </summary>
    private void AtomicWrite(byte[] content)
    {
        var tempPath = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
            {
                fs.Write(content, 0, content.Length);
                fs.Flush(flushToDisk: true);
            }

            // Release any open handle on the target so the rename succeeds cross-platform.
            _fileStream?.Dispose();
            _fileStream = null;
            // Route through DurableFile.AtomicReplace (not a bare File.Move) so the containing
            // directory is fsync'd after the rename — otherwise a crash between the rename returning
            // and the directory write landing can leave the old entry visible on restart. Every other
            // atomic-replace site in the durable path uses this helper.
            DurableFile.AtomicReplace(tempPath, FilePath);

            // The replacement is the whole new file; its size is exactly the content length.
            // Updated under the caller-held per-path semaphore (DR-9); the tracked write makes the
            // cache authoritative until the next Open or out-of-band change.
            _fileSize = content.Length;
            _cachedSizeStale = false;
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    private async Task AtomicWriteAsync(byte[] content, CancellationToken cancellationToken)
    {
        var tempPath = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write);
            await using (fs.ConfigureAwait(false))
            {
                await fs.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            if (_fileStream is not null)
            {
                await _fileStream.DisposeAsync().ConfigureAwait(false);
            }

            _fileStream = null;
            // Route through DurableFile.AtomicReplace (not a bare File.Move) so the containing
            // directory is fsync'd after the rename — otherwise a crash between the rename returning
            // and the directory write landing can leave the old entry visible on restart. Every other
            // atomic-replace site in the durable path uses this helper.
            DurableFile.AtomicReplace(tempPath, FilePath);

            // See AtomicWrite — the new file's size is the content length, updated under the
            // caller-held per-path semaphore (DR-9); the tracked write makes the cache authoritative.
            _fileSize = content.Length;
            _cachedSizeStale = false;
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best-effort cleanup — a leaked temp is harmless (a sibling .tmp-* file), never the target.
        }
    }

    /// <summary>
    /// Asynchronously writes all bytes to the file, overwriting any existing content.
    /// </summary>
    /// <param name="content">The byte array to write to the file.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <exception cref="IOException">Thrown when an error occurs while writing to the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// await storageFile.WriteAllBytesAsync(new byte[] { 0x01, 0x02, 0x03 });
    /// </code>
    /// </example>
    public async Task WriteAllBytesAsync(byte[] content, CancellationToken cancellationToken = default)
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AtomicWriteAsync(content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Writes all text to the file using the specified encoding, overwriting any existing content.
    /// </summary>
    /// <param name="content">The string to write to the file.</param>
    /// <param name="encoding">The encoding to use for writing the file.</param>
    /// <exception cref="IOException">Thrown when an error occurs while writing to the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// storageFile.WriteAllText("Hello, world!", Encoding.UTF8);
    /// </code>
    /// </example>
    public void WriteAllText(string content, Encoding encoding)
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        WaitWithTimeout(semaphore);
        try
        {
            // Route through AtomicWrite (temp file + fsync + DurableFile.AtomicReplace) rather than
            // an in-place File.WriteAllText that truncates the target before the new content lands,
            // matching WriteAllBytes. A crash mid-write then leaves the prior file intact instead of
            // a truncated one, and the directory entry is fsync'd so the replacement is durable.
            AtomicWrite(encoding.GetBytes(content));
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Asynchronously writes all text to the file using the specified encoding, overwriting any existing content.
    /// </summary>
    /// <param name="content">The string to write to the file.</param>
    /// <param name="encoding">The encoding to use for writing the file.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <exception cref="IOException">Thrown when an error occurs while writing to the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// await storageFile.WriteAllTextAsync("Hello, world!", Encoding.UTF8);
    /// </code>
    /// </example>
    public async Task WriteAllTextAsync(string content, Encoding encoding, CancellationToken cancellationToken = default)
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // See WriteAllText: durable, crash-atomic temp-file + AtomicReplace, not in-place.
            await AtomicWriteAsync(encoding.GetBytes(content), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Appends bytes to the end of the file.
    /// </summary>
    /// <param name="content">The byte array to append to the file.</param>
    /// <exception cref="IOException">Thrown when an error occurs while writing to the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// storageFile.AppendAllBytes(new byte[] { 0x04, 0x05 });
    /// </code>
    /// </example>
    public void AppendAllBytes(byte[] content)
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        WaitWithTimeout(semaphore);
        try
        {
            var existed = File.Exists(FilePath);
            using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.Write(content, 0, content.Length);
            // fsync the appended bytes before acknowledging the write — otherwise they linger in
            // the OS page cache and a crash loses an append the caller believes is durable.
            stream.Flush(flushToDisk: true);
            // Make the cache authoritative (DR-9: under the held per-path semaphore). When the
            // cached base is untrusted (fresh instance / post-Open), growing it would promote a
            // wrong base to authoritative forever — re-stat instead: the on-disk length already
            // includes the bytes just appended. A trusted base grows by exactly the appended length.
            if (_cachedSizeStale)
            {
                RefreshCachedFileSize();
            }
            else
            {
                _fileSize += content.Length;
            }
            _cachedSizeStale = false;
            FsyncDirectoryIfEntryChanged(existed);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Asynchronously appends bytes to the end of the file.
    /// </summary>
    /// <param name="content">The byte array to append to the file.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <exception cref="IOException">Thrown when an error occurs while writing to the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// await storageFile.AppendAllBytesAsync(new byte[] { 0x04, 0x05 });
    /// </code>
    /// </example>
    public async Task AppendAllBytesAsync(byte[] content, CancellationToken cancellationToken = default)
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existed = File.Exists(FilePath);
            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.None);
            await using var _ = stream.ConfigureAwait(false);
            await stream.WriteAsync(content.AsMemory(0, content.Length), cancellationToken).ConfigureAwait(false);
            // fsync the appended bytes before acknowledging — see AppendAllBytes.
            stream.Flush(flushToDisk: true);
            // Make the cache authoritative (DR-9: under the held per-path semaphore). When the
            // cached base is untrusted (fresh instance / post-Open), growing it would promote a
            // wrong base to authoritative forever — re-stat instead: the on-disk length already
            // includes the bytes just appended. A trusted base grows by exactly the appended length.
            if (_cachedSizeStale)
            {
                RefreshCachedFileSize();
            }
            else
            {
                _fileSize += content.Length;
            }
            _cachedSizeStale = false;
            FsyncDirectoryIfEntryChanged(existed);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Appends text to the end of the file using the specified encoding.
    /// </summary>
    /// <param name="content">The string to append to the file.</param>
    /// <param name="encoding">The encoding to use for writing the file.</param>
    /// <exception cref="IOException">Thrown when an error occurs while writing to the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// storageFile.AppendAllText("Hello, again!", Encoding.UTF8);
    /// </code>
    /// </example>
    public void AppendAllText(string content, Encoding encoding)
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        WaitWithTimeout(semaphore);
        try
        {
            var existed = File.Exists(FilePath);
            using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.None);
            var bytes = encoding.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            // fsync to match AppendAllBytes — the class's append contract is uniform: an
            // acknowledged append survives a crash regardless of which overload wrote it.
            stream.Flush(flushToDisk: true);
            // Make the cache authoritative (DR-9: under the held per-path semaphore). When the
            // cached base is untrusted (fresh instance / post-Open), growing it would promote a
            // wrong base to authoritative forever — re-stat instead: the on-disk length already
            // includes the bytes just appended. A trusted base grows by exactly the appended length.
            if (_cachedSizeStale)
            {
                RefreshCachedFileSize();
            }
            else
            {
                _fileSize += bytes.Length;
            }
            _cachedSizeStale = false;
            FsyncDirectoryIfEntryChanged(existed);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Asynchronously appends text to the end of the file using the specified encoding.
    /// </summary>
    /// <param name="content">The string to append to the file.</param>
    /// <param name="encoding">The encoding to use for writing the file.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <exception cref="IOException">Thrown when an error occurs while writing to the file.</exception>
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// await storageFile.AppendAllTextAsync("Hello, again!", Encoding.UTF8);
    /// </code>
    /// </example>
    public async Task AppendAllTextAsync(string content, Encoding encoding, CancellationToken cancellationToken = default)
    {
        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existed = File.Exists(FilePath);
            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.None);
            await using var _ = stream.ConfigureAwait(false);
            var bytes = encoding.GetBytes(content);
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken).ConfigureAwait(false);
            // fsync to match AppendAllBytesAsync — see AppendAllText.
            stream.Flush(flushToDisk: true);
            // Make the cache authoritative (DR-9: under the held per-path semaphore). When the
            // cached base is untrusted (fresh instance / post-Open), growing it would promote a
            // wrong base to authoritative forever — re-stat instead: the on-disk length already
            // includes the bytes just appended. A trusted base grows by exactly the appended length.
            if (_cachedSizeStale)
            {
                RefreshCachedFileSize();
            }
            else
            {
                _fileSize += bytes.Length;
            }
            _cachedSizeStale = false;
            FsyncDirectoryIfEntryChanged(existed);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Disposes the resources used by the <see cref="StorageFile"/> class.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        // Atomically claim the dispose slot; bail if another thread beat us here.
        // The house pattern: Interlocked.Exchange on an int flag (see WriteAheadLog for the
        // established pattern) prevents two concurrent Dispose calls both entering the body.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (disposing)
        {
            var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
            WaitWithTimeout(semaphore);
            try
            {
                DisposeFileStreamIfExists();
            }
            finally
            {
                semaphore.Release();
            }

            // Intentionally do NOT dispose or remove the per-path semaphore from
            // s_fileSemaphores. A concurrent caller on a different StorageFile instance
            // for the same path may be mid-AcquireAsync between GetOrAdd and Release;
            // tearing the semaphore out here would race that caller onto a disposed
            // object. Per-path semaphores are leaked intentionally: one SemaphoreSlim
            // plus its kernel handle per unique path ever seen, bounded at ~10,000
            // unique paths per process for typical workloads. Callers that rotate paths
            // (per-day segments, per-partition files) should reuse StorageFile instances
            // rather than instantiate a new one per path.
        }
    }

    private void DisposeFileStreamIfExists()
    {
        _fileStream?.Dispose();
        _fileStream = null;
    }

    /// <summary>
    /// Waits on a semaphore with a timeout. Throws <see cref="TimeoutException"/> if the
    /// semaphore cannot be acquired within <see cref="s_semaphoreTimeout"/>.
    /// </summary>
    private static void WaitWithTimeout(SemaphoreSlim semaphore)
    {
        if (!semaphore.Wait(s_semaphoreTimeout))
        {
            throw new TimeoutException(
                $"Failed to acquire file semaphore within {s_semaphoreTimeout.TotalSeconds} seconds. " +
                "This may indicate a deadlock or a leaked semaphore from a previous crash.");
        }
    }

    /// <summary>
    /// Ensures that the specified directory exists, making every newly created directory level's
    /// entry durable so a crash cannot drop the subtree (and the file created inside it).
    /// </summary>
    /// <param name="directoryPath">The path of the directory.</param>
    private void EnsureDirectoryExists(string directoryPath)
    {
        var created = DurableFile.CreateDirectoryDurable(directoryPath);
        if (!OperatingSystem.IsWindows())
        {
            _directoryFsyncCount += created;
        }
    }
}
