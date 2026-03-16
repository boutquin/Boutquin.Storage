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
/// <b>Why two levels of semaphores?</b> This class uses a two-tier locking strategy:
/// (1) A static <see cref="ConcurrentDictionary{TKey, TValue}"/> of <see cref="SemaphoreSlim"/>
/// keyed by file path protects cross-instance access to the same physical file — multiple
/// <see cref="StorageFile"/> instances pointing to the same path won't corrupt each other.
/// (2) An instance-level <c>_semaphore</c> protects the <c>_fileStream</c> field from concurrent
/// access within a single instance. The static dictionary prevents file-level races; the instance
/// semaphore prevents stream-level races.
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
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_fileSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>();
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageFile"/> class.
    /// </summary>
    /// <param name="fileLocation">The directory path where the file is located.</param>
    /// <param name="fileName">The name of the file.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="fileLocation"/> or <paramref name="fileName"/> is null, empty, or whitespace.</exception>
    public StorageFile(string fileLocation, string fileName)
    {
        Guard.AgainstNullOrWhiteSpace(() => fileLocation); // Validate the file location to ensure it is not null, empty, or whitespace.
        Guard.AgainstNullOrWhiteSpace(() => fileName); // Validate the file name to ensure it is not null, empty, or whitespace.

        FileLocation = fileLocation;
        FileName = fileName;
        FilePath = Path.Combine(FileLocation, FileName);

        // Ensure the directory exists.
        EnsureDirectoryExists(fileLocation);

        // Ensure the file exists.
        Create(FileExistenceHandling.DoNothingIfExists);
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
    public long FileSize => new FileInfo(FilePath).Length;

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
        WaitWithTimeout(_semaphore);
        try
        {
            DisposeFileStreamIfExists();
            // Why different FileAccess for Append mode? FileMode.Append only supports FileAccess.Write
            // (attempting ReadWrite throws). For all other modes, ReadWrite is used to support both
            // reading and writing through the same stream.
            _fileStream = new FileStream(FilePath, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite);
            return _fileStream;
        }
        finally
        {
            _semaphore.Release();
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
        WaitWithTimeout(_semaphore);
        try
        {
            DisposeFileStreamIfExists();
        }
        finally
        {
            _semaphore.Release();
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
            }
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

        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        WaitWithTimeout(semaphore);
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read);
            if (offset >= stream.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset is greater than the length of the file.");
            }

            stream.Seek(offset, SeekOrigin.Begin);

            var buffer = new byte[count];
            var bytesRead = stream.Read(buffer, 0, (int)count);
            if (bytesRead < count)
            {
                Array.Resize(ref buffer, bytesRead);
            }

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

        var semaphore = s_fileSemaphores.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            await using var _ = stream.ConfigureAwait(false);
            if (offset >= stream.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset is greater than the length of the file.");
            }

            stream.Seek(offset, SeekOrigin.Begin);

            var buffer = new byte[(int)count];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, (int)count), cancellationToken).ConfigureAwait(false);
            if (bytesRead < count)
            {
                Array.Resize(ref buffer, bytesRead);
            }

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
            File.WriteAllBytes(FilePath, content);
        }
        finally
        {
            semaphore.Release();
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
            await File.WriteAllBytesAsync(FilePath, content, cancellationToken).ConfigureAwait(false);
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
            File.WriteAllText(FilePath, content, encoding);
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
            await File.WriteAllTextAsync(FilePath, content, encoding, cancellationToken).ConfigureAwait(false);
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
            using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.Write(content, 0, content.Length);
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
            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.None);
            await using var _ = stream.ConfigureAwait(false);
            await stream.WriteAsync(content.AsMemory(0, content.Length), cancellationToken).ConfigureAwait(false);
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
            File.AppendAllText(FilePath, content, encoding);
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
            await File.AppendAllTextAsync(FilePath, content, encoding, cancellationToken).ConfigureAwait(false);
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
        if (!_disposed)
        {
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

                // Clean up the static semaphore to prevent unbounded growth of the dictionary.
                // Why TryRemove after Release? The semaphore is released first so no one is
                // blocked. If another instance for the same path is created concurrently,
                // GetOrAdd will create a fresh semaphore — this is safe because the file
                // already exists on disk.
                if (s_fileSemaphores.TryRemove(FilePath, out var removed))
                {
                    removed.Dispose();
                }

                _semaphore.Dispose();
            }

            _disposed = true;
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
    /// Ensures that the specified directory exists.
    /// </summary>
    /// <param name="directoryPath">The path of the directory.</param>
    private static void EnsureDirectoryExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }
}
