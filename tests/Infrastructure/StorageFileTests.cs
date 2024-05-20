
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

using System;
using System.IO;

using Xunit;

/// <summary>
/// This class contains unit tests for the StorageFile class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public class StorageFileTests : IDisposable
{
    private const string TestDirectory = "TestFiles";
    private readonly string _testFilePath;

    /// <summary>
    /// Initializes a new instance of the StorageFileTests class.
    /// Ensures the test directory is clean before each test.
    /// </summary>
    public StorageFileTests()
    {
        Directory.CreateDirectory(TestDirectory);
        _testFilePath = Path.Combine(TestDirectory, "testfile.txt");
        CleanupTestFiles();
    }

    /// <summary>
    /// Cleans up the test environment after each test.
    /// </summary>
    public void Dispose()
    {
        CleanupTestFiles();
    }

    private void CleanupTestFiles()
    {
        if (File.Exists(_testFilePath))
        {
            File.Delete(_testFilePath);
        }
    }

    /// <summary>
    /// Test to ensure that the Create method correctly creates a new file.
    /// </summary>
    [Fact]
    public void Create_ShouldCreateNewFile()
    {
        // Arrange: Create a new StorageFile instance.
        var storageFile = new StorageFile(_testFilePath);

        // Act: Create the file with Overwrite handling.
        storageFile.Create(FileExistenceHandling.Overwrite);

        // Assert: Check that the file was created.
        Assert.True(File.Exists(_testFilePath));
    }

    /// <summary>
    /// Test to ensure that the Create method throws an exception if the file already exists and existenceHandling is set to Throw.
    /// </summary>
    [Fact]
    public void Create_ShouldThrowIfFileExistsAndExistenceHandlingIsThrow()
    {
        // Arrange: Create a new StorageFile instance and create the file.
        var storageFile = new StorageFile(_testFilePath);
        storageFile.Create(FileExistenceHandling.Overwrite);

        // Act & Assert: Attempt to create the file again with Throw handling and expect an IOException.
        var exception = Assert.Throws<IOException>(() => storageFile.Create(FileExistenceHandling.Throw));
        Assert.Equal("File already exists.", exception.InnerException?.Message ?? exception.Message);
    }

    /// <summary>
    /// Test to ensure that the Exists method correctly checks if the file exists.
    /// </summary>
    [Fact]
    public void Exists_ShouldReturnTrueIfFileExists()
    {
        // Arrange: Create a new StorageFile instance and create the file.
        var storageFile = new StorageFile(_testFilePath);
        storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Check if the file exists.
        var exists = storageFile.Exists();

        // Assert: The file should exist.
        Assert.True(exists);
    }

    /// <summary>
    /// Test to ensure that the Exists method returns false if the file does not exist.
    /// </summary>
    [Fact]
    public void Exists_ShouldReturnFalseIfFileDoesNotExist()
    {
        // Arrange: Create a new StorageFile instance.
        var storageFile = new StorageFile(_testFilePath);

        // Act: Check if the file exists.
        var exists = storageFile.Exists();

        // Assert: The file should not exist.
        Assert.False(exists);
    }

    /// <summary>
    /// Test to ensure that the Open method opens an existing file.
    /// </summary>
    [Fact]
    public void Open_ShouldOpenExistingFile()
    {
        // Arrange: Create a new StorageFile instance and create the file.
        var storageFile = new StorageFile(_testFilePath);
        storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Open the file.
        using var stream = storageFile.Open();

        // Assert: The stream should be open and readable.
        Assert.NotNull(stream);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
    }

    /// <summary>
    /// Test to ensure that the Open method throws an exception if the file does not exist.
    /// </summary>
    [Fact]
    public void Open_ShouldThrowIfFileDoesNotExist()
    {
        // Arrange: Create a new StorageFile instance.
        var storageFile = new StorageFile(_testFilePath);

        // Act & Assert: Attempt to open the file and expect a FileNotFoundException.
        var exception = Assert.Throws<FileNotFoundException>(() => storageFile.Open());
        Assert.StartsWith("Could not find file '", exception.Message);
    }

    /// <summary>
    /// Test to ensure that the Delete method deletes an existing file.
    /// </summary>
    [Fact]
    public void Delete_ShouldDeleteExistingFile()
    {
        // Arrange: Create a new StorageFile instance and create the file.
        var storageFile = new StorageFile(_testFilePath);
        storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Delete the file.
        storageFile.Delete();

        // Assert: The file should no longer exist.
        Assert.False(File.Exists(_testFilePath));
    }

    /// <summary>
    /// Test to ensure that the Delete method throws an exception if the file does not exist.
    /// </summary>
    [Fact]
    public void Delete_ShouldThrowIfFileDoesNotExist()
    {
        // Arrange: Create a new StorageFile instance.
        var storageFile = new StorageFile(_testFilePath);

        // Act & Assert: Attempt to delete the file and expect a FileNotFoundException.
        var exception = Assert.Throws<FileNotFoundException>(() => storageFile.Delete());
        Assert.Equal("The specified file was not found.", exception.Message);
    }

    /// <summary>
    /// Test to ensure that the GetFileSize method returns the correct file size.
    /// </summary>
    [Fact]
    public void GetFileSize_ShouldReturnCorrectFileSize()
    {
        // Arrange: Create a new StorageFile instance and create the file.
        var storageFile = new StorageFile(_testFilePath);
        storageFile.Create(FileExistenceHandling.Overwrite);
        File.WriteAllBytes(_testFilePath, new byte[] { 0x01, 0x02, 0x03 });

        // Act: Get the file size.
        var fileSize = storageFile.GetFileSize();

        // Assert: The file size should be 3 bytes.
        Assert.Equal(3, fileSize);
    }

    /// <summary>
    /// Test to ensure that the GetFileSize method throws an exception if the file does not exist.
    /// </summary>
    [Fact]
    public void GetFileSize_ShouldThrowIfFileDoesNotExist()
    {
        // Arrange: Create a new StorageFile instance.
        var storageFile = new StorageFile(_testFilePath);

        // Act & Assert: Attempt to get the file size and expect a FileNotFoundException.
        var exception = Assert.Throws<FileNotFoundException>(() => storageFile.GetFileSize());
        Assert.Equal("File not found.", exception.Message);
    }

    /// <summary>
    /// Test to ensure that the GetFileName method returns the correct file name.
    /// </summary>
    [Fact]
    public void GetFileName_ShouldReturnCorrectFileName()
    {
        // Arrange: Create a new StorageFile instance and create the file.
        var storageFile = new StorageFile(_testFilePath);
        storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Get the file name.
        var fileName = storageFile.GetFileName();

        // Assert: The file name should be "testfile.txt".
        Assert.Equal("testfile.txt", fileName);
    }

    /// <summary>
    /// Test to ensure that the GetFileName method throws an exception if the file does not exist.
    /// </summary>
    [Fact]
    public void GetFileName_ShouldThrowIfFileDoesNotExist()
    {
        // Arrange: Create a new StorageFile instance.
        var storageFile = new StorageFile(_testFilePath);

        // Act & Assert: Attempt to get the file name and expect a FileNotFoundException.
        var exception = Assert.Throws<FileNotFoundException>(() => storageFile.GetFileName());
        Assert.Equal("File not found.", exception.Message);
    }

    /// <summary>
    /// Test to ensure that the GetFileLocation method returns the correct file location.
    /// </summary>
    [Fact]
    public void GetFileLocation_ShouldReturnCorrectFileLocation()
    {
        // Arrange: Create a new StorageFile instance and create the file.
        var storageFile = new StorageFile(_testFilePath);
        storageFile.Create(FileExistenceHandling.Overwrite);

        // Act: Get the file location.
        var fileLocation = storageFile.GetFileLocation();

        // Assert: The file location should be the test directory.
        Assert.Equal(TestDirectory, fileLocation);
    }

    /// <summary>
    /// Test to ensure that the GetFileLocation method throws an exception if the file does not exist.
    /// </summary>
    [Fact]
    public void GetFileLocation_ShouldThrowIfFileDoesNotExist()
    {
        // Arrange: Create a new StorageFile instance.
        var storageFile = new StorageFile(_testFilePath);

        // Act & Assert: Attempt to get the file location and expect a FileNotFoundException.
        var exception = Assert.Throws<FileNotFoundException>(() => storageFile.GetFileLocation());
        Assert.Equal("File not found.", exception.Message);
    }

    /// <summary>
    /// Test to ensure that the ReadAllBytes method reads the entire file content.
    /// </summary>
    [Fact]
    public void ReadAllBytes_ShouldReadEntireFileContent()
    {
        // Arrange: Create a new StorageFile instance and create the file with some content.
        var storageFile = new StorageFile(_testFilePath);
        storageFile.Create(FileExistenceHandling.Overwrite);
        var content = new byte[] { 0x01, 0x02, 0x03 };
        File.WriteAllBytes(_testFilePath, content);

        // Act: Read the entire file content.
        var result = storageFile.ReadAllBytes();

        // Assert: The read content should match the original content.
        Assert.Equal(content, result);
    }

    /// <summary>
    /// Test to ensure that the ReadAllBytes method throws an exception if the file does not exist.
    /// </summary>
    [Fact]
    public void ReadAllBytes_ShouldThrowIfFileDoesNotExist()
    {
        // Arrange: Create a new StorageFile instance.
        var storageFile = new StorageFile(_testFilePath);

        // Act & Assert: Attempt to read the file content and expect a FileNotFoundException.
        var exception = Assert.Throws<FileNotFoundException>(() => storageFile.ReadAllBytes());
        Assert.Equal("File not found.", exception.Message);
    }

    /// <summary>
    /// Test to ensure that the WriteAllBytes method writes the entire byte array to the file.
    /// </summary>
    [Fact]
    public void WriteAllBytes_ShouldWriteEntireByteArrayToFile()
    {
        // Arrange: Create a new StorageFile instance.
        var storageFile = new StorageFile(_testFilePath);
        var content = new byte[] { 0x01, 0x02, 0x03 };

        // Act: Write the byte array to the file.
        storageFile.WriteAllBytes(content);

        // Assert: The file content should match the written content.
        var result = File.ReadAllBytes(_testFilePath);
        Assert.Equal(content, result);
    }

    /// <summary>
    /// Test to ensure that the WriteAllBytes method throws an exception if access to the path is denied.
    /// </summary>
    [Fact]
    public void WriteAllBytes_ShouldThrowIfAccessToPathIsDenied()
    {
        // Arrange: Create a new StorageFile instance with an invalid path.
        var storageFile = new StorageFile(@"C:\Windows\System32\testfile.txt");
        var content = new byte[] { 0x01, 0x02, 0x03 };

        // Act & Assert: Attempt to write the byte array to the file and expect an IOException.
        var exception = Assert.Throws<IOException>(() => storageFile.WriteAllBytes(content));
        Assert.Contains("Access to the path is denied.", exception.Message);
    }

    /// <summary>
    /// Test to ensure that the AppendAllBytes method appends the byte array to the end of the file.
    /// </summary>
    [Fact]
    public void AppendAllBytes_ShouldAppendByteArrayToEndOfFile()
    {
        // Arrange: Create a new StorageFile instance and create the file with some initial content.
        var storageFile = new StorageFile(_testFilePath);
        storageFile.Create(FileExistenceHandling.Overwrite);
        var initialContent = new byte[] { 0x01, 0x02, 0x03 };
        File.WriteAllBytes(_testFilePath, initialContent);
        var additionalContent = new byte[] { 0x04, 0x05, 0x06 };

        // Act: Append the byte array to the end of the file.
        storageFile.AppendAllBytes(additionalContent);

        // Assert: The file content should match the concatenated content.
        var expectedContent = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        var result = File.ReadAllBytes(_testFilePath);
        Assert.Equal(expectedContent, result);
    }

    /// <summary>
    /// Test to ensure that the AppendAllBytes method throws an exception if the file does not exist.
    /// </summary>
    [Fact]
    public void AppendAllBytes_ShouldThrowIfFileDoesNotExist()
    {
        // Arrange: Create a new StorageFile instance.
        var storageFile = new StorageFile(_testFilePath);
        var content = new byte[] { 0x01, 0x02, 0x03 };

        // Act & Assert: Attempt to append the byte array to the file and expect a FileNotFoundException.
        var exception = Assert.Throws<FileNotFoundException>(() => storageFile.AppendAllBytes(content));
        Assert.Equal("File not found.", exception.Message);
    }
}
