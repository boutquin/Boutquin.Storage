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
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Interface for basic file operations.
/// </summary>
public interface IStorageFile : IFileInformation, IDisposable
{
    /// <summary>
    /// Creates a new file, optionally handling the existence of an existing file.
    /// </summary>
    /// <param name="existenceHandling">Specifies how to handle the existence of an existing file.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="existenceHandling"/> is not a defined enum value.</exception>
    /// <exception cref="IOException">Thrown when the file exists and <paramref name="existenceHandling"/> is <see cref="FileExistenceHandling.ThrowIfExists"/>.</exception>
    void Create(FileExistenceHandling existenceHandling);

    /// <summary>
    /// Checks if the file exists at the specified location.
    /// </summary>
    /// <returns>True if the file exists; otherwise, false.</returns>
    bool Exists();

    /// <summary>
    /// Opens the file for reading or writing.
    /// </summary>
    /// <param name="mode">The file mode to use when opening the file.</param>
    /// <returns>A file stream for the opened file.</returns>
    /// <exception cref="UnauthorizedAccessException">Thrown when the path is read-only or the caller does not have the required permission.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is a zero-length string, contains only white space, or contains invalid characters.</exception>
    Stream Open(FileMode mode);

    /// <summary>
    /// Closes the open file stream.
    /// </summary>
    void Close();

    /// <summary>
    /// Deletes the file.
    /// </summary>
    /// <param name="deletionHandling">Specifies how to handle the deletion of the file.</param>
    void Delete(FileDeletionHandling deletionHandling);

    /// <summary>
    /// Reads a specified number of bytes from the file at the given offset.
    /// </summary>
    /// <param name="offset">The offset in the file to start reading from.</param>
    /// <param name="count">The number of bytes to read.</param>
    /// <returns>The bytes read from the file.</returns>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is a zero-length string, contains only white space, or contains invalid characters.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="offset"/> or <paramref name="count"/> is less than zero.</exception>
    byte[] ReadBytes(int offset, int count);

    /// <summary>
    /// Reads a specified number of bytes from the file at the given offset asynchronously.
    /// </summary>
    /// <param name="offset">The offset in the file to start reading from.</param>
    /// <param name="count">The number of bytes to read.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>A task representing the asynchronous read operation. The task result contains the bytes read from the file.</returns>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is a zero-length string, contains only white space, or contains invalid characters.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="offset"/> or <paramref name="count"/> is less than zero.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    Task<byte[]> ReadBytesAsync(int offset, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the entire file content as a byte array.
    /// </summary>
    /// <returns>The file content as a byte array.</returns>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file specified in <see cref="_filePath"/> was not found.</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is a zero-length string, contains only white space, or contains invalid characters.</exception>
    byte[] ReadAllBytes();

    /// <summary>
    /// Reads the entire file content as a byte array asynchronously.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation, with a result of the file content as a byte array.</returns>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file specified in <see cref="_filePath"/> was not found.</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is a zero-length string, contains only white space, or contains invalid characters.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the entire file content as a string using the specified encoding.
    /// </summary>
    /// <param name="encoding">The encoding to use.</param>
    /// <returns>The file content as a string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="encoding"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file specified in <see cref="_filePath"/> was not found.</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is a zero-length string, contains only white space, or contains invalid characters.</exception>
    string ReadAllText(Encoding encoding);

    /// <summary>
    /// Reads the entire file content as a string asynchronously using the specified encoding.
    /// </summary>
    /// <param name="encoding">The encoding to use.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation, with a result of the file content as a string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="encoding"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the file specified in <see cref="_filePath"/> was not found.</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="ArgumentException">Thrown when the path is a zero-length string, contains only white space, or contains invalid characters.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    Task<string> ReadAllTextAsync(Encoding encoding, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a byte array to the file.
    /// </summary>
    /// <param name="content">The byte array to write.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    void WriteAllBytes(byte[] content);

    /// <summary>
    /// Writes a byte array to the file asynchronously.
    /// </summary>
    /// <param name="content">The byte array to write.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    Task WriteAllBytesAsync(byte[] content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a string to the file using the specified encoding.
    /// </summary>
    /// <param name="content">The string to write.</param>
    /// <param name="encoding">The encoding to use.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> or <paramref name="encoding"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    void WriteAllText(string content, Encoding encoding);

    /// <summary>
    /// Writes a string to the file asynchronously using the specified encoding.
    /// </summary>
    /// <param name="content">The string to write.</param>
    /// <param name="encoding">The encoding to use.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> or <paramref name="encoding"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    Task WriteAllTextAsync(string content, Encoding encoding, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a byte array to the end of the file.
    /// </summary>
    /// <param name="content">The byte array to append.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    void AppendAllBytes(byte[] content);

    /// <summary>
    /// Appends a byte array to the end of the file asynchronously.
    /// </summary>
    /// <param name="content">The byte array to append.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    Task AppendAllBytesAsync(byte[] content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a string to the end of the file using the specified encoding.
    /// </summary>
    /// <param name="content">The string to append.</param>
    /// <param name="encoding">The encoding to use.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> or <paramref name="encoding"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    void AppendAllText(string content, Encoding encoding);

    /// <summary>
    /// Appends a string to the end of the file asynchronously using the specified encoding.
    /// </summary>
    /// <param name="content">The string to append.</param>
    /// <param name="encoding">The encoding to use.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> or <paramref name="encoding"/> is null.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
    /// <exception cref="PathTooLongException">Thrown when the specified path, file name, or both exceed the system-defined maximum length.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified path is invalid (e.g., it is on an unmapped drive).</exception>
    /// <exception cref="NotSupportedException">Thrown when the path format is not supported.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    Task AppendAllTextAsync(string content, Encoding encoding, CancellationToken cancellationToken = default);
}