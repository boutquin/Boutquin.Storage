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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// This class contains unit tests for the StorageFile class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class StorageFileTests : IDisposable
{
    private readonly string _testFileLocation = Path.Combine(Directory.GetCurrentDirectory(), "StorageFileTestFiles");
    private readonly string _testFileName = "testfile.txt";
    private readonly IStorageFile _storageFile;
    private string TestFilePath => Path.Combine(_testFileLocation, _testFileName);

    public StorageFileTests()
    {
        // Arrange: Initialize the storage file instance for each test.
        _storageFile = new StorageFile(_testFileLocation, _testFileName);
    }

    /// <summary>
    /// Test to ensure that the Create method correctly creates a new file.
    /// </summary>
    [Fact]
    public void Create_ShouldCreateNewFile()
    {
        // Act: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Assert: Check that the file exists.
        Assert.True(_storageFile.Exists());
    }

    /// <summary>
    /// Test to ensure that Create throws an exception if the file already exists
    /// and FileExistenceHandling is set to ThrowIfExists.
    /// </summary>
    [Fact]
    public void Create_ShouldThrowIfFileExists()
    {
        // Arrange: Create the file first.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act & Assert: Attempt to create the file again with ThrowIfExists handling and expect an IOException.
        Assert.Throws<IOException>(() => _storageFile.Create(FileExistenceHandling.ThrowIfExists));
    }

    /// <summary>
    /// Test to ensure that the Exists method correctly identifies existing files.
    /// </summary>
    [Fact]
    public void Exists_ShouldReturnTrueIfFileExists()
    {
        // Arrange: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Check if the file exists.
        var exists = _storageFile.Exists();

        // Assert: The file should exist.
        Assert.True(exists);
    }

    /// <summary>
    /// Test to ensure that the Exists method correctly identifies non-existing files.
    /// </summary>
    [Fact]
    public void Exists_ShouldReturnFalseIfFileDoesNotExist()
    {
        // Arrange: Delete the file.
        _storageFile.Delete(FileDeletionHandling.DeleteIfExists);

        // Act: Check if the file exists.
        var exists = _storageFile.Exists();

        // Assert: The file should not exist.
        Assert.False(exists);
    }

    /// <summary>
    /// Test to ensure that the Open method correctly opens a file stream.
    /// </summary>
    [Fact]
    public void Open_ShouldOpenFileStream()
    {
        // Arrange: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Open the file.
        using var stream = _storageFile.Open(FileMode.Open);

        // Assert: The stream should be open and readable.
        Assert.NotNull(stream);
        Assert.True(stream.CanRead);
    }

    /// <summary>
    /// Test to ensure that the Delete method correctly deletes an existing file.
    /// </summary>
    [Fact]
    public void Delete_ShouldDeleteFile()
    {
        // Arrange: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Delete the file.
        _storageFile.Delete(FileDeletionHandling.DeleteIfExists);

        // Assert: The file should no longer exist.
        Assert.False(_storageFile.Exists());
    }

    /// <summary>
    /// Test to ensure that the Delete method throws an exception if the file does not exist
    /// and FileDeletionHandling is set to ThrowIfNotExists.
    /// </summary>
    [Fact]
    public void Delete_ShouldThrowIfFileDoesNotExist()
    {
        // Arrange: Delete the file first.
        if (File.Exists(_storageFile.FilePath))
        {
            File.Delete(_storageFile.FilePath);
        }

        // Act & Assert: Attempt to delete a non-existing file with ThrowIfNotExists handling and expect a FileNotFoundException.
        Assert.Throws<FileNotFoundException>(() => _storageFile.Delete(FileDeletionHandling.ThrowIfNotExists));
    }

    /// <summary>
    /// Test to ensure that the Length property returns the correct file size.
    /// </summary>
    [Fact]
    public void Length_ShouldReturnFileSize()
    {
        // Arrange: Create and write to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        _storageFile.WriteAllBytes(new byte[] { 0x01, 0x02, 0x03 });

        // Act: Get the file size.
        var length = _storageFile.FileSize;

        // Assert: The file size should match the number of bytes written.
        Assert.Equal(3, length);
    }

    /// <summary>
    /// Test to ensure that the ReadBytes method reads the correct number of bytes from the file.
    /// </summary>
    [Fact]
    public void ReadBytes_ShouldReadSpecifiedNumberOfBytes()
    {
        // Arrange: Create and write to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var content = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        _storageFile.WriteAllBytes(content);

        // Act: Read bytes from the file.
        var bytesRead = _storageFile.ReadBytes(1, 3);

        // Assert: The bytes read should match the expected sequence.
        Assert.Equal(new byte[] { 0x02, 0x03, 0x04 }, bytesRead);
    }

    /// <summary>
    /// Test to ensure that the WriteAllBytes method correctly writes bytes to the file.
    /// </summary>
    [Fact]
    public void WriteAllBytes_ShouldWriteBytesToFile()
    {
        // Arrange: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Write bytes to the file.
        var content = new byte[] { 0x01, 0x02, 0x03 };
        _storageFile.WriteAllBytes(content);

        // Assert: The file content should match the bytes written.
        var bytesRead = _storageFile.ReadAllBytes();
        Assert.Equal(content, bytesRead);
    }

    /// <summary>
    /// Test to ensure that the AppendAllBytes method correctly appends bytes to the file.
    /// </summary>
    [Fact]
    public void AppendAllBytes_ShouldAppendBytesToFile()
    {
        // Arrange: Create and write initial bytes to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var initialContent = new byte[] { 0x01, 0x02, 0x03 };
        _storageFile.WriteAllBytes(initialContent);

        // Act: Append bytes to the file.
        var additionalContent = new byte[] { 0x04, 0x05 };
        _storageFile.AppendAllBytes(additionalContent);

        // Assert: The file content should match the concatenated bytes.
        var bytesRead = _storageFile.ReadAllBytes();
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, bytesRead);
    }

    /// <summary>
    /// Test to ensure that the ReadAllText method reads the correct text content from the file.
    /// </summary>
    [Fact]
    public void ReadAllText_ShouldReadTextFromFile()
    {
        // Arrange: Create and write text to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var content = "Hello, world!";
        _storageFile.WriteAllText(content, Encoding.UTF8);

        // Act: Read text from the file.
        var textRead = _storageFile.ReadAllText(Encoding.UTF8);

        // Assert: The text read should match the text written.
        Assert.Equal(content, textRead);
    }

    /// <summary>
    /// Test to ensure that the WriteAllText method correctly writes text to the file.
    /// </summary>
    [Fact]
    public void WriteAllText_ShouldWriteTextToFile()
    {
        // Arrange: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Write text to the file.
        var content = "Hello, world!";
        _storageFile.WriteAllText(content, Encoding.UTF8);

        // Assert: The text read from the file should match the text written.
        var textRead = _storageFile.ReadAllText(Encoding.UTF8);
        Assert.Equal(content, textRead);
    }

    /// <summary>
    /// Test to ensure that the AppendAllText method correctly appends text to the file.
    /// </summary>
    [Fact]
    public void AppendAllText_ShouldAppendTextToFile()
    {
        // Arrange: Create and write initial text to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var initialContent = "Hello, ";
        _storageFile.WriteAllText(initialContent, Encoding.UTF8);

        // Act: Append text to the file.
        var additionalContent = "world!";
        _storageFile.AppendAllText(additionalContent, Encoding.UTF8);

        // Assert: The text read from the file should match the concatenated text.
        var textRead = _storageFile.ReadAllText(Encoding.UTF8);
        Assert.Equal("Hello, world!", textRead);
    }

    /// <summary>
    /// Test to ensure that the ReadBytesAsync method reads the correct number of bytes from the file asynchronously.
    /// </summary>
    [Fact]
    public async Task ReadBytesAsync_ShouldReadSpecifiedNumberOfBytesAsync()
    {
        // Arrange: Create and write to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var content = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        await _storageFile.WriteAllBytesAsync(content);

        // Act: Read bytes from the file asynchronously.
        var bytesRead = await _storageFile.ReadBytesAsync(1, 3);

        // Assert: The bytes read should match the expected sequence.
        Assert.Equal(new byte[] { 0x02, 0x03, 0x04 }, bytesRead);
    }

    /// <summary>
    /// Test to ensure that the WriteAllBytesAsync method correctly writes bytes to the file asynchronously.
    /// </summary>
    [Fact]
    public async Task WriteAllBytesAsync_ShouldWriteBytesToFileAsync()
    {
        // Arrange: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Write bytes to the file asynchronously.
        var content = new byte[] { 0x01, 0x02, 0x03 };
        await _storageFile.WriteAllBytesAsync(content);

        // Assert: The file content should match the bytes written.
        var bytesRead = _storageFile.ReadAllBytes();
        Assert.Equal(content, bytesRead);
    }

    /// <summary>
    /// Test to ensure that the AppendAllBytesAsync method correctly appends bytes to the file asynchronously.
    /// </summary>
    [Fact]
    public async Task AppendAllBytesAsync_ShouldAppendBytesToFileAsync()
    {
        // Arrange: Create and write initial bytes to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var initialContent = new byte[] { 0x01, 0x02, 0x03 };
        await _storageFile.WriteAllBytesAsync(initialContent);

        // Act: Append bytes to the file asynchronously.
        var additionalContent = new byte[] { 0x04, 0x05 };
        await _storageFile.AppendAllBytesAsync(additionalContent);

        // Assert: The file content should match the concatenated bytes.
        var bytesRead = _storageFile.ReadAllBytes();
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, bytesRead);
    }

    /// <summary>
    /// Test to ensure that the ReadAllTextAsync method reads the correct text content from the file asynchronously.
    /// </summary>
    [Fact]
    public async Task ReadAllTextAsync_ShouldReadTextFromFileAsync()
    {
        // Arrange: Create and write text to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var content = "Hello, world!";
        await _storageFile.WriteAllTextAsync(content, Encoding.UTF8);

        // Act: Read text from the file asynchronously.
        var textRead = await _storageFile.ReadAllTextAsync(Encoding.UTF8);

        // Assert: The text read should match the text written.
        Assert.Equal(content, textRead);
    }

    /// <summary>
    /// Test to ensure that the WriteAllTextAsync method correctly writes text to the file asynchronously.
    /// </summary>
    [Fact]
    public async Task WriteAllTextAsync_ShouldWriteTextToFileAsync()
    {
        // Arrange: Create the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Write text to the file asynchronously.
        var content = "Hello, world!";
        await _storageFile.WriteAllTextAsync(content, Encoding.UTF8);

        // Assert: The text read from the file should match the text written.
        var textRead = await _storageFile.ReadAllTextAsync(Encoding.UTF8);
        Assert.Equal(content, textRead);
    }

    /// <summary>
    /// Test to ensure that the AppendAllTextAsync method correctly appends text to the file asynchronously.
    /// </summary>
    [Fact]
    public async Task AppendAllTextAsync_ShouldAppendTextToFileAsync()
    {
        // Arrange: Create and write initial text to the file.
        _storageFile.Create(FileExistenceHandling.Overwrite);
        var initialContent = "Hello, ";
        await _storageFile.WriteAllTextAsync(initialContent, Encoding.UTF8);

        // Act: Append text to the file asynchronously.
        var additionalContent = "world!";
        await _storageFile.AppendAllTextAsync(additionalContent, Encoding.UTF8);

        // Assert: The text read from the file should match the concatenated text.
        var textRead = await _storageFile.ReadAllTextAsync(Encoding.UTF8);
        Assert.Equal("Hello, world!", textRead);
    }

    /// <summary>
    /// Clean up the test environment by deleting the test file after each test.
    /// </summary>
    public void Dispose()
    {
        if (_storageFile.Exists())
        {
            _storageFile.Delete(FileDeletionHandling.DeleteIfExists);
        }
        _storageFile.Dispose();
    }
}
