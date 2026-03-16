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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// This class contains unit tests for the FileLocation and SsTableMetadata value objects.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class ValueObjectTests
{
    // =============================================
    // FileLocation Tests
    // =============================================

    /// <summary>
    /// Test to ensure that the FileLocation constructor sets properties correctly.
    /// </summary>
    [Fact]
    public void FileLocation_Constructor_ShouldSetPropertiesCorrectly()
    {
        // Arrange & Act: Create a FileLocation with specific values.
        var location = new FileLocation(100, 50);

        // Assert: Verify the properties are set correctly.
        location.Offset.Should().Be(100);
        location.Count.Should().Be(50);
    }

    /// <summary>
    /// Test to ensure that two FileLocation instances with the same values are equal.
    /// </summary>
    [Fact]
    public void FileLocation_ValueEquality_ShouldBeEqual()
    {
        // Arrange: Create two FileLocation instances with the same values.
        var location1 = new FileLocation(100, 50);
        var location2 = new FileLocation(100, 50);

        // Act & Assert: Verify that the two instances are equal.
        location1.Should().Be(location2);
        (location1 == location2).Should().BeTrue();
    }

    /// <summary>
    /// Test to ensure that two FileLocation instances with different values are not equal.
    /// </summary>
    [Fact]
    public void FileLocation_ValueInequality_ShouldNotBeEqual()
    {
        // Arrange: Create two FileLocation instances with different values.
        var location1 = new FileLocation(100, 50);
        var location2 = new FileLocation(200, 75);

        // Act & Assert: Verify that the two instances are not equal.
        location1.Should().NotBe(location2);
        (location1 != location2).Should().BeTrue();
    }

    /// <summary>
    /// Test to ensure that default FileLocation values are zero.
    /// </summary>
    [Fact]
    public void FileLocation_DefaultValues_ShouldBeZero()
    {
        // Arrange & Act: Create a default FileLocation.
        var location = default(FileLocation);

        // Assert: Verify that default values are zero.
        location.Offset.Should().Be(0);
        location.Count.Should().Be(0);
    }

    /// <summary>
    /// Test to ensure that FileLocation ToString contains property values.
    /// </summary>
    [Fact]
    public void FileLocation_ToString_ShouldContainPropertyValues()
    {
        // Arrange: Create a FileLocation with specific values.
        var location = new FileLocation(100, 50);

        // Act: Get the string representation.
        var result = location.ToString();

        // Assert: Verify that the string contains the property values.
        result.Should().Contain("100");
        result.Should().Contain("50");
    }

    // =============================================
    // SsTableMetadata Tests
    // =============================================

    /// <summary>
    /// Test to ensure that the SsTableMetadata constructor sets all properties correctly.
    /// </summary>
    [Fact]
    public void SsTableMetadata_Constructor_ShouldSetAllPropertiesCorrectly()
    {
        // Arrange: Define the expected values.
        var creationTime = new DateTime(2025, 1, 15, 10, 30, 0);
        var lastModTime = new DateTime(2025, 6, 20, 14, 45, 0);

        // Act: Create an SsTableMetadata instance.
        var metadata = new SsTableMetadata(
            EntryCount: 1000,
            FileSize: 524288L,
            CreationTime: creationTime,
            LastModificationTime: lastModTime,
            FileName: "data_001.sst",
            FileLocation: "/var/data/sst");

        // Assert: Verify all properties are set correctly.
        metadata.EntryCount.Should().Be(1000);
        metadata.FileSize.Should().Be(524288L);
        metadata.CreationTime.Should().Be(creationTime);
        metadata.LastModificationTime.Should().Be(lastModTime);
        metadata.FileName.Should().Be("data_001.sst");
        metadata.FileLocation.Should().Be("/var/data/sst");
    }

    /// <summary>
    /// Test to ensure that two SsTableMetadata instances with the same values are equal.
    /// </summary>
    [Fact]
    public void SsTableMetadata_ValueEquality_ShouldBeEqual()
    {
        // Arrange: Create two SsTableMetadata instances with the same values.
        var creationTime = new DateTime(2025, 1, 15, 10, 30, 0);
        var lastModTime = new DateTime(2025, 6, 20, 14, 45, 0);

        var metadata1 = new SsTableMetadata(1000, 524288L, creationTime, lastModTime, "data_001.sst", "/var/data/sst");
        var metadata2 = new SsTableMetadata(1000, 524288L, creationTime, lastModTime, "data_001.sst", "/var/data/sst");

        // Act & Assert: Verify that the two instances are equal.
        metadata1.Should().Be(metadata2);
        (metadata1 == metadata2).Should().BeTrue();
    }

    /// <summary>
    /// Test to ensure that two SsTableMetadata instances with different EntryCount are not equal.
    /// </summary>
    [Fact]
    public void SsTableMetadata_ValueInequality_DifferentEntryCount_ShouldNotBeEqual()
    {
        // Arrange: Create two SsTableMetadata instances with different EntryCount.
        var creationTime = new DateTime(2025, 1, 15, 10, 30, 0);
        var lastModTime = new DateTime(2025, 6, 20, 14, 45, 0);

        var metadata1 = new SsTableMetadata(1000, 524288L, creationTime, lastModTime, "data_001.sst", "/var/data/sst");
        var metadata2 = new SsTableMetadata(2000, 524288L, creationTime, lastModTime, "data_001.sst", "/var/data/sst");

        // Act & Assert: Verify that the two instances are not equal.
        metadata1.Should().NotBe(metadata2);
        (metadata1 != metadata2).Should().BeTrue();
    }

    /// <summary>
    /// Test to ensure that default SsTableMetadata values are set to their type defaults.
    /// </summary>
    [Fact]
    public void SsTableMetadata_DefaultValues_ShouldBeTypeDefaults()
    {
        // Arrange & Act: Create a default SsTableMetadata.
        var metadata = default(SsTableMetadata);

        // Assert: Verify that default values match type defaults.
        metadata.EntryCount.Should().Be(0);
        metadata.FileSize.Should().Be(0L);
        metadata.CreationTime.Should().Be(default(DateTime));
        metadata.LastModificationTime.Should().Be(default(DateTime));
        metadata.FileName.Should().BeNull();
        metadata.FileLocation.Should().BeNull();
    }
}
