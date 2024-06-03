// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
/// Provides basic file operations.
/// </summary>
public sealed class StorageFile : IStorageFile
{
    private FileStream? _fileStream;

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageFile"/> class.
    /// </summary>
    /// <param name="fileLocation">The directory path where the file is located.</param>
    /// <param name="fileName">The name of the file.</param>
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

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// long fileSize = storageFile.Length;
    /// Console.WriteLine($"File size: {fileSize} bytes");
    /// </code>
    /// </example>
    public long FileSize => new FileInfo(FilePath).Length;

    /// <inheritdoc />
    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// string fileName = storageFile.FileName;
    /// Console.WriteLine($"File name: {fileName}");
    /// </code>
    /// </example>
    public string FileName { get; }

    /// <inheritdoc />
    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// string fileLocation = storageFile.FileLocation;
    /// Console.WriteLine($"File location: {fileLocation}");
    /// </code>
    /// </example>
    public string FileLocation { get; }

    /// <inheritdoc />
    /// <inheritdoc />
    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// string filePath = storageFile.FilePath;
    /// Console.WriteLine($"File Path: {filePath}");
    /// </code>
    /// </example>
    public string FilePath { get; }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// storageFile.Create(FileExistenceHandling.Overwrite);
    /// </code>
    /// </example>
    public void Create(FileExistenceHandling existenceHandling)
    {
        Guard.AgainstUndefinedEnumValue(() => existenceHandling); // Validate the existence handling to ensure it is a defined enum value.

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

    /// <inheritdoc />
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

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// using var stream = storageFile.Open(FileMode.OpenOrCreate);
    /// // Perform read/write operations with the stream
    /// </code>
    /// </example>
    public Stream Open(FileMode mode)
    {
        _fileStream?.Dispose();
        _fileStream = new FileStream(FilePath, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite);
        return _fileStream;
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// storageFile.Close();
    /// </code>
    /// </example>
    public void Close()
    {
        _fileStream?.Dispose();
        _fileStream = null;
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// storageFile.Delete(FileDeletionHandling.DeleteIfExists);
    /// </code>
    /// </example>
    public void Delete(FileDeletionHandling deletionHandling)
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

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// byte[] data = storageFile.ReadBytes(0, 100);
    /// Console.WriteLine($"Read {data.Length} bytes from file.");
    /// </code>
    /// </example>
    public byte[] ReadBytes(int offset, int count)
    {
        Guard.AgainstNegative(() => offset); // Validate offset.
        Guard.AgainstNegative(() => count); // Validate count.

        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read);
        if (offset >= stream.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset is greater than the length of the file.");
        }

        stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[count];
        var bytesRead = stream.Read(buffer, 0, count);
        if (bytesRead < count)
        {
            Array.Resize(ref buffer, bytesRead);
        }

        return buffer;
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// byte[] data = await storageFile.ReadBytesAsync(0, 100);
    /// Console.WriteLine($"Read {data.Length} bytes from file.");
    /// </code>
    /// </example>
    public async Task<byte[]> ReadBytesAsync(int offset, int count, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNegative(() => offset); // Validate offset.
        Guard.AgainstNegative(() => count); // Validate count.

        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (offset >= stream.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset is greater than the length of the file.");
        }

        stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[count];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken);
        if (bytesRead < count)
        {
            Array.Resize(ref buffer, bytesRead);
        }

        return buffer;
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// byte[] data = storageFile.ReadAllBytes();
    /// Console.WriteLine($"Read {data.Length} bytes from file.");
    /// </code>
    /// </example>
    public byte[] ReadAllBytes()
    {
        return File.ReadAllBytes(FilePath);
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// byte[] data = await storageFile.ReadAllBytesAsync();
    /// Console.WriteLine($"Read {data.Length} bytes from file.");
    /// </code>
    /// </example>
    public async Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await File.ReadAllBytesAsync(FilePath, cancellationToken);
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// string content = storageFile.ReadAllText(Encoding.UTF8);
    /// Console.WriteLine($"File content: {content}");
    /// </code>
    /// </example>
    public string ReadAllText(Encoding encoding)
    {
        return File.ReadAllText(FilePath, encoding);
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// string content = await storageFile.ReadAllTextAsync(Encoding.UTF8);
    /// Console.WriteLine($"File content: {content}");
    /// </code>
    /// </example>
    public async Task<string> ReadAllTextAsync(Encoding encoding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await File.ReadAllTextAsync(FilePath, encoding, cancellationToken);
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// storageFile.WriteAllBytes(new byte[] { 0x01, 0x02, 0x03 });
    /// </code>
    /// </example>
    public void WriteAllBytes(byte[] content)
    {
        File.WriteAllBytes(FilePath, content);
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// await storageFile.WriteAllBytesAsync(new byte[] { 0x01, 0x02, 0x03 });
    /// </code>
    /// </example>
    public async Task WriteAllBytesAsync(byte[] content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllBytesAsync(FilePath, content, cancellationToken);
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// storageFile.WriteAllText("Hello, world!", Encoding.UTF8);
    /// </code>
    /// </example>
    public void WriteAllText(string content, Encoding encoding)
    {
        File.WriteAllText(FilePath, content, encoding);
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// await storageFile.WriteAllTextAsync("Hello, world!", Encoding.UTF8);
    /// </code>
    /// </example>
    public async Task WriteAllTextAsync(string content, Encoding encoding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(FilePath, content, encoding, cancellationToken);
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// storageFile.AppendAllBytes(new byte[] { 0x04, 0x05 });
    /// </code>
    /// </example>
    public void AppendAllBytes(byte[] content)
    {
        using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.None);
        stream.Write(content, 0, content.Length);
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// await storageFile.AppendAllBytesAsync(new byte[] { 0x04, 0x05 });
    /// </code>
    /// </example>
    public async Task AppendAllBytesAsync(byte[] content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(content.AsMemory(0, content.Length), cancellationToken);
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// storageFile.AppendAllText("Hello, again!", Encoding.UTF8);
    /// </code>
    /// </example>
    public void AppendAllText(string content, Encoding encoding)
    {
        File.AppendAllText(FilePath, content, encoding);
    }

    /// <inheritdoc />
    /// <example>
    /// <code>
    /// var storageFile = new StorageFile(@"C:\TEMP", "example.txt");
    /// await storageFile.AppendAllTextAsync("Hello, again!", Encoding.UTF8);
    /// </code>
    /// </example>
    public async Task AppendAllTextAsync(string content, Encoding encoding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await File.AppendAllTextAsync(FilePath, content, encoding, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _fileStream?.Dispose();
    }

    /// <summary>
    /// Ensures that the specified directory exists.
    /// </summary>
    /// <param name="directoryPath">The path of the directory.</param>
    private void EnsureDirectoryExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }
}
