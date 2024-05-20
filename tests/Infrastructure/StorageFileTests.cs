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
        var fileSystem = new StorageFile(_tempFilePath);

        // Assert
        Assert.NotNull(fileSystem);
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
        var fileSystem = new StorageFile(_tempFilePath);
        File.WriteAllText(_tempFilePath, "Original content");

        // Act
        fileSystem.Create(FileExistenceHandling.Overwrite);

        // Assert
        Assert.Empty(File.ReadAllText(_tempFilePath));
    }

    [Fact]
    public void Create_FileExistenceHandlingDoNothingIfExists_ShouldNotOverwriteFile()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        File.WriteAllText(_tempFilePath, "Original content");

        // Act
        fileSystem.Create(FileExistenceHandling.DoNothingIfExists);

        // Assert
        Assert.Equal("Original content", File.ReadAllText(_tempFilePath));
    }

    [Fact]
    public void Create_FileExistenceHandlingThrowIfExists_ShouldThrowIOException()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        File.WriteAllText(_tempFilePath, "Original content");

        // Act & Assert
        var ex = Assert.Throws<IOException>(() => fileSystem.Create(FileExistenceHandling.ThrowIfExists));
        Assert.Equal($"File '{_tempFilePath}' already exists.", ex.Message);
    }

    [Fact]
    public void Exists_FileExists_ShouldReturnTrue()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        File.WriteAllText(_tempFilePath, "Content");

        // Act
        var exists = fileSystem.Exists();

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public void Exists_FileDoesNotExist_ShouldReturnFalse()
    {
        // Arrange
        var fileSystem = new StorageFile("nonexistentfile.tmp");

        // Act
        var exists = fileSystem.Exists();

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public void Open_ValidPath_ShouldOpenFileStream()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);

        // Act
        using var stream = fileSystem.Open();

        // Assert
        Assert.NotNull(stream);
    }

    [Fact]
    public void Delete_FileExists_ShouldDeleteFile()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        File.WriteAllText(_tempFilePath, "Content");

        // Act
        fileSystem.Delete();

        // Assert
        Assert.False(File.Exists(_tempFilePath));
    }

    [Fact]
    public void Delete_FileDoesNotExist_ShouldNotThrow()
    {
        // Arrange
        var fileSystem = new StorageFile("nonexistentfile.tmp");

        // Act & Assert
        var exception = Record.Exception(() => fileSystem.Delete());
        Assert.Null(exception);
    }

    [Fact]
    public void GetFileSize_FileExists_ShouldReturnCorrectSize()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        var content = "12345"u8.ToArray();
        File.WriteAllBytes(_tempFilePath, content);

        // Act
        var size = fileSystem.GetFileSize();

        // Assert
        Assert.Equal(5, size);
    }

    [Fact]
    public void GetFileName_ShouldReturnFileName()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);

        // Act
        var fileName = fileSystem.GetFileName();

        // Assert
        Assert.Equal(Path.GetFileName(_tempFilePath), fileName);
    }

    [Fact]
    public void GetFileLocation_ShouldReturnFilePath()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);

        // Act
        var filePath = fileSystem.GetFileLocation();

        // Assert
        Assert.Equal(_tempFilePath, filePath);
    }

    [Fact]
    public void ReadAllBytes_FileExists_ShouldReturnCorrectContent()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        var content = "Content"u8.ToArray();
        File.WriteAllBytes(_tempFilePath, content);

        // Act
        var result = fileSystem.ReadAllBytes();

        // Assert
        Assert.Equal(content, result);
    }

    [Fact]
    public async Task ReadAllBytesAsync_FileExists_ShouldReturnCorrectContent()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        var content = "Content"u8.ToArray();
        await File.WriteAllBytesAsync(_tempFilePath, content);

        // Act
        var result = await fileSystem.ReadAllBytesAsync();

        // Assert
        Assert.Equal(content, result);
    }

    [Fact]
    public void ReadAllText_FileExists_ShouldReturnCorrectContent()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        var content = "Content";
        File.WriteAllText(_tempFilePath, content, Encoding.UTF8);

        // Act
        var result = fileSystem.ReadAllText(Encoding.UTF8);

        // Assert
        Assert.Equal(content, result);
    }

    [Fact]
    public async Task ReadAllTextAsync_FileExists_ShouldReturnCorrectContent()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        var content = "Content";
        await File.WriteAllTextAsync(_tempFilePath, content, Encoding.UTF8);

        // Act
        var result = await fileSystem.ReadAllTextAsync(Encoding.UTF8);

        // Assert
        Assert.Equal(content, result);
    }

    [Fact]
    public void WriteAllBytes_ShouldWriteContentToFile()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        var content = "Content"u8.ToArray();

        // Act
        fileSystem.WriteAllBytes(content);

        // Assert
        var fileContent = File.ReadAllBytes(_tempFilePath);
        Assert.Equal(content, fileContent);
    }

    [Fact]
    public async Task WriteAllBytesAsync_ShouldWriteContentToFile()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        var content = "Content"u8.ToArray();

        // Act
        await fileSystem.WriteAllBytesAsync(content);

        // Assert
        var fileContent = await File.ReadAllBytesAsync(_tempFilePath);
        Assert.Equal(content, fileContent);
    }

    [Fact]
    public void WriteAllText_ShouldWriteContentToFile()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        var content = "Content";

        // Act
        fileSystem.WriteAllText(content, Encoding.UTF8);

        // Assert
        var fileContent = File.ReadAllText(_tempFilePath, Encoding.UTF8);
        Assert.Equal(content, fileContent);
    }

    [Fact]
    public async Task WriteAllTextAsync_ShouldWriteContentToFile()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        var content = "Content";

        // Act
        await fileSystem.WriteAllTextAsync(content, Encoding.UTF8);

        // Assert
        var fileContent = await File.ReadAllTextAsync(_tempFilePath, Encoding.UTF8);
        Assert.Equal(content, fileContent);
    }

    [Fact]
    public void AppendAllBytes_ShouldAppendContentToFile()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        var initialContent = "Initial"u8.ToArray();
        var appendContent = "Append"u8.ToArray();
        File.WriteAllBytes(_tempFilePath, initialContent);

        // Act
        fileSystem.AppendAllBytes(appendContent);

        // Assert
        var expectedContent = "InitialAppend"u8.ToArray();
        var fileContent = File.ReadAllBytes(_tempFilePath);
        Assert.Equal(expectedContent, fileContent);
    }

    [Fact]
    public async Task AppendAllBytesAsync_ShouldAppendContentToFile()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        var initialContent = "Initial"u8.ToArray();
        var appendContent = "Append"u8.ToArray();
        await File.WriteAllBytesAsync(_tempFilePath, initialContent);

        // Act
        await fileSystem.AppendAllBytesAsync(appendContent);

        // Assert
        var expectedContent = "InitialAppend"u8.ToArray();
        var fileContent = await File.ReadAllBytesAsync(_tempFilePath);
        Assert.Equal(expectedContent, fileContent);
    }

    [Fact]
    public void AppendAllText_ShouldAppendContentToFile()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        var initialContent = "Initial";
        var appendContent = "Append";
        File.WriteAllText(_tempFilePath, initialContent, Encoding.UTF8);

        // Act
        fileSystem.AppendAllText(appendContent, Encoding.UTF8);

        // Assert
        var expectedContent = "InitialAppend";
        var fileContent = File.ReadAllText(_tempFilePath, Encoding.UTF8);
        Assert.Equal(expectedContent, fileContent);
    }

    [Fact]
    public async Task AppendAllTextAsync_ShouldAppendContentToFile()
    {
        // Arrange
        var fileSystem = new StorageFile(_tempFilePath);
        var initialContent = "Initial";
        var appendContent = "Append";
        await File.WriteAllTextAsync(_tempFilePath, initialContent, Encoding.UTF8);

        // Act
        await fileSystem.AppendAllTextAsync(appendContent, Encoding.UTF8);

        // Assert
        var expectedContent = "InitialAppend";
        var fileContent = await File.ReadAllTextAsync(_tempFilePath, Encoding.UTF8);
        Assert.Equal(expectedContent, fileContent);
    }
}
