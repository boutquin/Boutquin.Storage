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

public sealed class StorageFileTests : IDisposable
{
    // Create a temporary file path for testing purposes
    private readonly string _tempFilePath = Path.GetTempFileName();
    private const string TestContent = "Hello, World!";

    public void Dispose()
    {
        // Clean up temporary file after tests are done
        if (File.Exists(_tempFilePath))
        {
            File.Delete(_tempFilePath);
        }
    }

    [Fact]
    public void Constructor_ValidPath_ShouldNotThrow()
    {
        // Arrange & Act
        var storageFile = new StorageFile(_tempFilePath);

        // Assert
        Assert.NotNull(storageFile);
    }

    [Fact]
    public void Constructor_NullOrEmptyPath_ShouldThrowArgumentException()
    {
        // Arrange
        string nullPath = null!;
        var emptyPath = string.Empty;
        var whitespacePath = " ";

        // Act & Assert
        var ex1 = Assert.Throws<ArgumentException>(() => new StorageFile(nullPath));
        Assert.Equal("filePath", ex1.ParamName);

        var ex2 = Assert.Throws<ArgumentException>(() => new StorageFile(emptyPath));
        Assert.Equal("filePath", ex2.ParamName);

        var ex3 = Assert.Throws<ArgumentException>(() => new StorageFile(whitespacePath));
        Assert.Equal("filePath", ex3.ParamName);
    }

    [Fact]
    public void Create_FileExistenceHandlingOverwrite_ShouldOverwriteFile()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        File.WriteAllText(_tempFilePath, "Original content");

        // Act
        storageFile.Create(FileExistenceHandling.Overwrite);

        // Assert
        Assert.Empty(File.ReadAllText(_tempFilePath));
    }

    [Fact]
    public void Create_FileExistenceHandlingDoNothingIfExists_ShouldNotOverwriteFile()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        File.WriteAllText(_tempFilePath, "Original content");

        // Act
        storageFile.Create(FileExistenceHandling.DoNothingIfExists);

        // Assert
        Assert.Equal("Original content", File.ReadAllText(_tempFilePath));
    }

    [Fact]
    public void Create_FileExistenceHandlingThrowIfExists_ShouldThrowIOException()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        File.WriteAllText(_tempFilePath, "Original content");

        // Act & Assert
        var ex = Assert.Throws<IOException>(() => storageFile.Create(FileExistenceHandling.ThrowIfExists));
        Assert.Equal($"File '{_tempFilePath}' already exists.", ex.Message);
    }

    [Fact]
    public void Exists_FileExists_ShouldReturnTrue()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        File.WriteAllText(_tempFilePath, "Content");

        // Act
        var exists = storageFile.Exists();

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public void Exists_FileDoesNotExist_ShouldReturnFalse()
    {
        // Arrange
        var storageFile = new StorageFile("nonexistentfile.tmp");

        // Act
        var exists = storageFile.Exists();

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public void Open_ValidPath_ShouldOpenFileStream()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);

        // Act
        using (var stream = storageFile.Open())
        {
            // Assert
            Assert.NotNull(stream);
        }
    }

    [Fact]
    public void Delete_FileExists_ShouldDeleteFile()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        File.WriteAllText(_tempFilePath, "Content");

        // Act
        storageFile.Delete();

        // Assert
        Assert.False(File.Exists(_tempFilePath));
    }

    [Fact]
    public void Delete_FileDoesNotExist_ShouldNotThrow()
    {
        // Arrange
        var storageFile = new StorageFile("nonexistentfile.tmp");

        // Act & Assert
        var exception = Record.Exception(() => storageFile.Delete());
        Assert.Null(exception);
    }

    [Fact]
    public void GetFileSize_FileExists_ShouldReturnCorrectSize()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        var content = "12345"u8.ToArray();
        File.WriteAllBytes(_tempFilePath, content);

        // Act
        var size = storageFile.GetFileSize();

        // Assert
        Assert.Equal(5, size);
    }

    [Fact]
    public void GetFileName_ShouldReturnFileName()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);

        // Act
        var fileName = storageFile.GetFileName();

        // Assert
        Assert.Equal(Path.GetFileName(_tempFilePath), fileName);
    }

    [Fact]
    public void GetFileLocation_ShouldReturnFilePath()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);

        // Act
        var filePath = storageFile.GetFileLocation();

        // Assert
        Assert.Equal(_tempFilePath, filePath);
    }

    /// <summary>
    /// Tests the normal case for reading bytes from a file.
    /// </summary>
    [Fact]
    public async Task ReadBytes_NormalCase_ReturnsCorrectBytes()
    {
        var storageFile = new StorageFile(_tempFilePath);
        var bytes = Encoding.UTF8.GetBytes(TestContent);
        await File.WriteAllTextAsync(_tempFilePath, TestContent);
        var result = storageFile.ReadBytes(0, bytes.Length);

        Assert.Equal(TestContent, Encoding.UTF8.GetString(result));
    }

    /// <summary>
    /// Tests reading bytes with an offset.
    /// </summary>
    [Fact]
    public void ReadBytes_WithOffset_ReturnsCorrectBytes()
    {
        var storageFile = new StorageFile(_tempFilePath);
        File.WriteAllText(_tempFilePath, TestContent);
        var result = storageFile.ReadBytes(7, 5);

        Assert.Equal("World", Encoding.UTF8.GetString(result));
    }

    /// <summary>
    /// Tests reading bytes with a negative offset.
    /// </summary>
    [Fact]
    public void ReadBytes_NegativeOffset_ThrowsArgumentOutOfRangeException()
    {
        var storageFile = new StorageFile(_tempFilePath);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => storageFile.ReadBytes(-1, 5));
        Assert.Equal("Parameter 'offset' cannot be negative. (Parameter 'offset')", exception.Message);
    }

    /// <summary>
    /// Tests reading bytes with a negative count.
    /// </summary>
    [Fact]
    public void ReadBytes_NegativeCount_ThrowsArgumentOutOfRangeException()
    {
        var storageFile = new StorageFile(_tempFilePath);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => storageFile.ReadBytes(0, -1));
        Assert.Equal("Parameter 'count' cannot be negative. (Parameter 'count')", exception.Message);
    }

    /// <summary>
    /// Tests reading bytes from a non-existent file.
    /// </summary>
    [Fact]
    public void ReadBytes_FileNotFound_ThrowsFileNotFoundException()
    {
        var storageFile = new StorageFile("nonexistentfile.bin");

        var exception = Assert.Throws<FileNotFoundException>(() => storageFile.ReadBytes(0, 5));
        Assert.StartsWith("Could not find file '", exception.Message);
    }

    /// <summary>
    /// Tests the normal case for asynchronously reading bytes from a file.
    /// </summary>
    [Fact]
    public async Task ReadBytesAsync_NormalCase_ReturnsCorrectBytes()
    {
        var storageFile = new StorageFile(_tempFilePath);
        var bytes = Encoding.UTF8.GetBytes(TestContent);
        await File.WriteAllTextAsync(_tempFilePath, TestContent);
        var result = await storageFile.ReadBytesAsync(0, bytes.Length);

        Assert.Equal(TestContent, Encoding.UTF8.GetString(result));
    }

    /// <summary>
    /// Tests asynchronously reading bytes with an offset.
    /// </summary>
    [Fact]
    public async Task ReadBytesAsync_WithOffset_ReturnsCorrectBytes()
    {
        var storageFile = new StorageFile(_tempFilePath);
        await File.WriteAllTextAsync(_tempFilePath, TestContent);
        var result = await storageFile.ReadBytesAsync(7, 5);

        Assert.Equal("World", Encoding.UTF8.GetString(result));
    }

    /// <summary>
    /// Tests asynchronously reading bytes with a negative offset.
    /// </summary>
    [Fact]
    public async Task ReadBytesAsync_NegativeOffset_ThrowsArgumentOutOfRangeException()
    {
        var storageFile = new StorageFile(_tempFilePath);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storageFile.ReadBytesAsync(-1, 5));
        Assert.Equal("Parameter 'offset' cannot be negative. (Parameter 'offset')", exception.Message);
    }

    /// <summary>
    /// Tests asynchronously reading bytes with a negative count.
    /// </summary>
    [Fact]
    public async Task ReadBytesAsync_NegativeCount_ThrowsArgumentOutOfRangeException()
    {
        var storageFile = new StorageFile(_tempFilePath);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storageFile.ReadBytesAsync(0, -1));
        Assert.Equal("Parameter 'count' cannot be negative. (Parameter 'count')", exception.Message);
    }

    /// <summary>
    /// Tests asynchronously reading bytes from a non-existent file.
    /// </summary>
    [Fact]
    public async Task ReadBytesAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        var storageFile = new StorageFile("nonexistentfile.bin");

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => storageFile.ReadBytesAsync(0, 5));
        Assert.StartsWith("Could not find file '", exception.Message);
    }
    [Fact]
    public void ReadAllBytes_FileExists_ShouldReturnCorrectContent()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        var content = "Content"u8.ToArray();
        File.WriteAllBytes(_tempFilePath, content);

        // Act
        var result = storageFile.ReadAllBytes();

        // Assert
        Assert.Equal(content, result);
    }

    [Fact]
    public async Task ReadAllBytesAsync_FileExists_ShouldReturnCorrectContent()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        var content = "Content"u8.ToArray();
        await File.WriteAllBytesAsync(_tempFilePath, content);

        // Act
        var result = await storageFile.ReadAllBytesAsync();

        // Assert
        Assert.Equal(content, result);
    }

    [Fact]
    public void ReadAllText_FileExists_ShouldReturnCorrectContent()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        var content = "Content";
        File.WriteAllText(_tempFilePath, content, Encoding.UTF8);

        // Act
        var result = storageFile.ReadAllText(Encoding.UTF8);

        // Assert
        Assert.Equal(content, result);
    }

    [Fact]
    public async Task ReadAllTextAsync_FileExists_ShouldReturnCorrectContent()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        var content = "Content";
        await File.WriteAllTextAsync(_tempFilePath, content, Encoding.UTF8);

        // Act
        var result = await storageFile.ReadAllTextAsync(Encoding.UTF8);

        // Assert
        Assert.Equal(content, result);
    }

    [Fact]
    public void WriteAllBytes_ShouldWriteContentToFile()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        var content = "Content"u8.ToArray();

        // Act
        storageFile.WriteAllBytes(content);

        // Assert
        var fileContent = File.ReadAllBytes(_tempFilePath);
        Assert.Equal(content, fileContent);
    }

    [Fact]
    public async Task WriteAllBytesAsync_ShouldWriteContentToFile()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        var content = "Content"u8.ToArray();

        // Act
        await storageFile.WriteAllBytesAsync(content);

        // Assert
        var fileContent = await File.ReadAllBytesAsync(_tempFilePath);
        Assert.Equal(content, fileContent);
    }

    [Fact]
    public void WriteAllText_ShouldWriteContentToFile()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        var content = "Content";

        // Act
        storageFile.WriteAllText(content, Encoding.UTF8);

        // Assert
        var fileContent = File.ReadAllText(_tempFilePath, Encoding.UTF8);
        Assert.Equal(content, fileContent);
    }

    [Fact]
    public async Task WriteAllTextAsync_ShouldWriteContentToFile()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        var content = "Content";

        // Act
        await storageFile.WriteAllTextAsync(content, Encoding.UTF8);

        // Assert
        var fileContent = await File.ReadAllTextAsync(_tempFilePath, Encoding.UTF8);
        Assert.Equal(content, fileContent);
    }

    [Fact]
    public void AppendAllBytes_ShouldAppendContentToFile()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        var initialContent = "Initial"u8.ToArray();
        var appendContent = "Append"u8.ToArray();
        File.WriteAllBytes(_tempFilePath, initialContent);

        // Act
        storageFile.AppendAllBytes(appendContent);

        // Assert
        var expectedContent = "InitialAppend"u8.ToArray();
        var fileContent = File.ReadAllBytes(_tempFilePath);
        Assert.Equal(expectedContent, fileContent);
    }

    [Fact]
    public async Task AppendAllBytesAsync_ShouldAppendContentToFile()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        var initialContent = "Initial"u8.ToArray();
        var appendContent = "Append"u8.ToArray();
        await File.WriteAllBytesAsync(_tempFilePath, initialContent);

        // Act
        await storageFile.AppendAllBytesAsync(appendContent);

        // Assert
        var expectedContent = "InitialAppend"u8.ToArray();
        var fileContent = await File.ReadAllBytesAsync(_tempFilePath);
        Assert.Equal(expectedContent, fileContent);
    }

    [Fact]
    public void AppendAllText_ShouldAppendContentToFile()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        var initialContent = "Initial";
        var appendContent = "Append";
        File.WriteAllText(_tempFilePath, initialContent, Encoding.UTF8);

        // Act
        storageFile.AppendAllText(appendContent, Encoding.UTF8);

        // Assert
        var expectedContent = "InitialAppend";
        var fileContent = File.ReadAllText(_tempFilePath, Encoding.UTF8);
        Assert.Equal(expectedContent, fileContent);
    }

    [Fact]
    public async Task AppendAllTextAsync_ShouldAppendContentToFile()
    {
        // Arrange
        var storageFile = new StorageFile(_tempFilePath);
        var initialContent = "Initial";
        var appendContent = "Append";
        await File.WriteAllTextAsync(_tempFilePath, initialContent, Encoding.UTF8);

        // Act
        await storageFile.AppendAllTextAsync(appendContent, Encoding.UTF8);

        // Assert
        var expectedContent = "InitialAppend";
        var fileContent = await File.ReadAllTextAsync(_tempFilePath, Encoding.UTF8);
        Assert.Equal(expectedContent, fileContent);
    }
}
