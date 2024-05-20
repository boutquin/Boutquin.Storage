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
/// Provides a robust, well-performing implementation of the IStorageFile interface.
/// </summary>
public class StorageFile : IStorageFile
{
    private readonly string _filePath;
    private readonly ReaderWriterLockSlim _lock = new();

    /// <summary>
    /// Initializes a new instance of the StorageFile class with the specified file path.
    /// </summary>
    /// <param name="filePath">The path of the file to manage.</param>
    /// <exception cref="ArgumentException">Thrown when the filePath is null, empty, or whitespace.</exception>
    public StorageFile(string filePath)
    {
        // Validate the file path to ensure it is not null, empty, or whitespace.
        // This prevents issues related to invalid file paths early on.
        Guard.AgainstNullOrWhiteSpace(() => filePath);

        _filePath = filePath;
    }

    /// <inheritdoc />
    /// <exception cref="IOException">Thrown when an I/O error occurs, such as when the file already exists and existenceHandling is set to Throw, or if access to the path is denied.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the existenceHandling parameter is out of range.</exception>
    public void Create(FileExistenceHandling existenceHandling)
    {
        _lock.EnterWriteLock();
        try
        {
            if (File.Exists(_filePath))
            {
                switch (existenceHandling)
                {
                    case FileExistenceHandling.Overwrite:
                        File.Create(_filePath).Dispose();
                        break;
                    case FileExistenceHandling.Throw:
                        throw new IOException("File already exists.");
                    case FileExistenceHandling.Skip:
                        return;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(existenceHandling), existenceHandling, null);
                }
            }
            else
            {
                File.Create(_filePath).Dispose();
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Access to the path is denied.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new IOException("The specified path is invalid.", ex);
        }
        catch (IOException ex)
        {
            throw new IOException("An I/O error occurred while creating the file.", ex);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="IOException">Thrown when an I/O error occurs, such as when access to the path is denied or the specified path is too long.</exception>
    public bool Exists()
    {
        _lock.EnterReadLock();
        try
        {
            return File.Exists(_filePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Access to the path is denied.", ex);
        }
        catch (PathTooLongException ex)
        {
            throw new IOException("The specified path is too long.", ex);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">Thrown when the specified file is not found.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs, such as when access to the path is denied.</exception>
    public Stream Open()
    {
        _lock.EnterReadLock();
        try
        {
            return new FileStream(_filePath, FileMode.Open, FileAccess.ReadWrite);
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Access to the path is denied.", ex);
        }
        catch (IOException ex)
        {
            throw new IOException("An I/O error occurred while opening the file.", ex);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">Thrown when the specified file is not found.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs, such as when access to the path is denied.</exception>
    public void Delete()
    {
        _lock.EnterWriteLock();
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
            else
            {
                throw new FileNotFoundException("The specified file was not found.");
            }
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Access to the path is denied.", ex);
        }
        catch (IOException ex)
        {
            throw new IOException("An I/O error occurred while deleting the file.", ex);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">Thrown when the specified file is not found.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs, such as when access to the path is denied.</exception>
    public long GetFileSize()
    {
        _lock.EnterReadLock();
        try
        {
            if (File.Exists(_filePath))
            {
                return new FileInfo(_filePath).Length;
            }
            throw new FileNotFoundException("File not found.");
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Access to the path is denied.", ex);
        }
        catch (IOException ex)
        {
            throw new IOException("An I/O error occurred while getting the file size.", ex);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">Thrown when the specified file is not found.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs, such as when access to the path is denied.</exception>
    public string GetFileName()
    {
        _lock.EnterReadLock();
        try
        {
            if (File.Exists(_filePath))
            {
                return Path.GetFileName(_filePath);
            }
            throw new FileNotFoundException("File not found.");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Access to the path is denied.", ex);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">Thrown when the specified file is not found.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs, such as when access to the path is denied.</exception>
    public string GetFileLocation()
    {
        _lock.EnterReadLock();
        try
        {
            if (File.Exists(_filePath))
            {
                return Path.GetDirectoryName(_filePath);
            }
            throw new FileNotFoundException("File not found.");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Access to the path is denied.", ex);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">Thrown when the specified file is not found.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs, such as when access to the path is denied.</exception>
    public byte[] ReadAllBytes()
    {
        _lock.EnterReadLock();
        try
        {
            if (File.Exists(_filePath))
            {
                return File.ReadAllBytes(_filePath);
            }
            throw new FileNotFoundException("File not found.");
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Access to the path is denied.", ex);
        }
        catch (IOException ex)
        {
            throw new IOException("An I/O error occurred while reading the file.", ex);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="IOException">Thrown when an I/O error occurs, such as when access to the path is denied.</exception>
    public void WriteAllBytes(byte[] content)
    {
        _lock.EnterWriteLock();
        try
        {
            File.WriteAllBytes(_filePath, content);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Access to the path is denied.", ex);
        }
        catch (IOException ex)
        {
            throw new IOException("An I/O error occurred while writing to the file.", ex);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">Thrown when the specified file is not found.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs, such as when access to the path is denied.</exception>
    public void AppendAllBytes(byte[] content)
    {
        _lock.EnterWriteLock();
        try
        {
            if (File.Exists(_filePath))
            {
                using var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write);
                stream.Write(content, 0, content.Length);
            }
            else
            {
                throw new FileNotFoundException("File not found.");
            }
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Access to the path is denied.", ex);
        }
        catch (IOException ex)
        {
            throw new IOException("An I/O error occurred while appending to the file.", ex);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}
