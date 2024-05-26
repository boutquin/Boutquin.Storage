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
namespace Boutquin.Storage.Infrastructure;

/// <summary>
/// Provides basic file operations.
/// </summary>
public sealed class StorageFile : IStorageFile
{
    private readonly string _filePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageFile"/> class.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <exception cref="ArgumentException">Thrown when the <paramref name="filePath"/> is null, empty, or whitespace.</exception>
    public StorageFile(string filePath)
    {
        // Validate the file path to ensure it is not null, empty, or whitespace.
        Guard.AgainstNullOrWhiteSpace(() => filePath); // Throws ArgumentException

        _filePath = filePath;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="existenceHandling"/> is not a defined enum value.</exception>
    /// <exception cref="IOException">Thrown when the file exists and <paramref name="existenceHandling"/> is <see cref="FileExistenceHandling.ThrowIfExists"/>.</exception>
    public void Create(FileExistenceHandling existenceHandling)
    {
        // Validate the existence handling to ensure it is a defined enum value.
        Guard.AgainstUndefinedEnumValue(() => existenceHandling); // Throws ArgumentOutOfRangeException

        switch (existenceHandling)
        {
            case FileExistenceHandling.Overwrite:
                File.Create(_filePath).Dispose();
                break;
            case FileExistenceHandling.DoNothingIfExists:
                if (!File.Exists(_filePath))
                {
                    File.Create(_filePath).Dispose();
                }
                break;
            case FileExistenceHandling.ThrowIfExists:
                if (File.Exists(_filePath))
                {
                    throw new IOException($"File '{_filePath}' already exists.");
                }
                File.Create(_filePath).Dispose();
                break;
            default:
                // Guard.AgainstUndefinedEnumValue should prevent this from happening.
                throw new ArgumentOutOfRangeException(nameof(existenceHandling), $"Undefined enum value: {existenceHandling}");
        }
    }

    /// <inheritdoc />
    public bool Exists()
    {
        return File.Exists(_filePath);
    }

    /// <inheritdoc />
    /// <exception cref="UnauthorizedAccessException">Thrown when the path is read-only or the caller does not have required permission.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file specified in <see cref="_filePath"/> member variable was not found.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is a zero-length string, contains only white space, or contains invalid characters.</exception>
    public Stream Open()
    {
        return new FileStream(_filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
    }

    /// <inheritdoc />
    public void Delete()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">Thrown when the file specified in <see cref="_filePath"/> member variable was not found.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    public long GetFileSize()
    {
        return new FileInfo(_filePath).Length;
    }

    /// <inheritdoc />
    public string GetFileName()
    {
        return Path.GetFileName(_filePath);
    }

    /// <inheritdoc />
    public string GetFileLocation()
    {
        return _filePath;
    }

    /// <summary>
    /// Reads a specified number of bytes from the file starting at a specified offset.
    /// </summary>
    /// <param name="offset">The offset at which to begin reading.</param>
    /// <param name="count">The number of bytes to read.</param>
    /// <returns>A byte array containing the data read from the file.</returns>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file specified in <see cref="_filePath"/> was not found.</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is a zero-length string, contains only white space, or contains invalid characters.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="offset"/> or <paramref name="count"/> is less than zero.</exception>
    public byte[] ReadBytes(int offset, int count)
    {
        Guard.AgainstNegative(() => offset); // Throws ArgumentOutOfRangeException
        Guard.AgainstNegative(() => count); // Throws ArgumentOutOfRangeException

        using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read);
        if (offset >= stream.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset is greater than the length of the file.");
        }

        stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[count];
        var bytesRead = 0;
        while (bytesRead < count)
        {
            var read = stream.Read(buffer, bytesRead, count - bytesRead);
            if (read == 0) break; // End of file reached
            bytesRead += read;
        }

        // If fewer bytes were read than requested, resize the buffer.
        if (bytesRead < count)
        {
            Array.Resize(ref buffer, bytesRead);
        }

        return buffer;
    }

    /// <summary>
    /// Asynchronously reads a specified number of bytes from the file starting at a specified offset.
    /// </summary>
    /// <param name="offset">The offset at which to begin reading.</param>
    /// <param name="count">The number of bytes to read.</param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous read operation. The value of the TResult parameter contains a byte array with the data read from the file.</returns>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file specified in <see cref="_filePath"/> was not found.</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is a zero-length string, contains only white space, or contains invalid characters.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="offset"/> or <paramref name="count"/> is less than zero.</exception>
    public async Task<byte[]> ReadBytesAsync(int offset, int count, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNegative(() => offset); // Throws ArgumentOutOfRangeException
        Guard.AgainstNegative(() => count); // Throws ArgumentOutOfRangeException

        await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (offset >= stream.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset is greater than the length of the file.");
        }

        stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[count];
        var bytesRead = 0;
        while (bytesRead < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(bytesRead, count - bytesRead), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break; // End of file reached
            bytesRead += read;
        }

        // If fewer bytes were read than requested, resize the buffer.
        if (bytesRead < count)
        {
            Array.Resize(ref buffer, bytesRead);
        }

        return buffer;
    }

    /// <inheritdoc />
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file specified in <see cref="_filePath"/> member variable was not found.</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is a zero-length string, contains only white space, or contains invalid characters.</exception>
    public byte[] ReadAllBytes()
    {
        return File.ReadAllBytes(_filePath);
    }

    /// <inheritdoc />
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file specified in <see cref="_filePath"/> member variable was not found.</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is a zero-length string, contains only white space, or contains invalid characters.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    public async Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default)
    {
        // Check for cancellation before starting the I/O operation
        cancellationToken.ThrowIfCancellationRequested();

        return await File.ReadAllBytesAsync(_filePath, cancellationToken);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="encoding"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file specified in <see cref="_filePath"/> member variable was not found.</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is a zero-length string, contains only white space, or contains invalid characters.</exception>
    public string ReadAllText(Encoding encoding)
    {
        return File.ReadAllText(_filePath, encoding);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="encoding"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file specified in <see cref="_filePath"/> member variable was not found.</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is a zero-length string, contains only white space, or contains invalid characters.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    public async Task<string> ReadAllTextAsync(Encoding encoding, CancellationToken cancellationToken = default)
    {
        // Check for cancellation before starting the I/O operation
        cancellationToken.ThrowIfCancellationRequested();

        return await File.ReadAllTextAsync(_filePath, encoding, cancellationToken);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    public void WriteAllBytes(byte[] content)
    {
        File.WriteAllBytes(_filePath, content);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    public async Task WriteAllBytesAsync(byte[] content, CancellationToken cancellationToken = default)
    {
        // Check for cancellation before starting the I/O operation
        cancellationToken.ThrowIfCancellationRequested();

        await File.WriteAllBytesAsync(_filePath, content, cancellationToken);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> or <paramref name="encoding"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    public void WriteAllText(string content, Encoding encoding)
    {
        File.WriteAllText(_filePath, content, encoding);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> or <paramref name="encoding"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    public async Task WriteAllTextAsync(string content, Encoding encoding, CancellationToken cancellationToken = default)
    {
        // Check for cancellation before starting the I/O operation
        cancellationToken.ThrowIfCancellationRequested();

        await File.WriteAllTextAsync(_filePath, content, encoding, cancellationToken);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    public void AppendAllBytes(byte[] content)
    {
        using var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.None);
        stream.Write(content, 0, content.Length);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    public async Task AppendAllBytesAsync(byte[] content, CancellationToken cancellationToken = default)
    {
        // Check for cancellation before starting the I/O operation
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(content, 0, content.Length, cancellationToken);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> or <paramref name="encoding"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    public void AppendAllText(string content, Encoding encoding)
    {
        File.AppendAllText(_filePath, content, encoding);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> or <paramref name="encoding"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    public async Task AppendAllTextAsync(string content, Encoding encoding, CancellationToken cancellationToken = default)
    {
        // Check for cancellation before starting the I/O operation
        cancellationToken.ThrowIfCancellationRequested();

        await File.AppendAllTextAsync(_filePath, content, encoding, cancellationToken);
    }
}
