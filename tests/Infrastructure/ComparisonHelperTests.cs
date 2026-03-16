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
/// This class contains unit tests for the ComparisonHelper.SafeCompareTo method.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class ComparisonHelperTests
{
    /// <summary>
    /// Test to ensure that comparing two null values returns zero.
    /// </summary>
    [Fact]
    public void SafeCompareTo_BothNull_ShouldReturnZero()
    {
        // Arrange: Two null string references.
        string? first = null;
        string? second = null;

        // Act: Compare the two null values.
        var result = ComparisonHelper.SafeCompareTo(first, second);

        // Assert: Both null should be considered equal.
        result.Should().Be(0);
    }

    /// <summary>
    /// Test to ensure that a null first value is less than a non-null second value.
    /// </summary>
    [Fact]
    public void SafeCompareTo_FirstNull_ShouldReturnNegative()
    {
        // Arrange: First is null, second is a valid string.
        string? first = null;
        string? second = "hello";

        // Act: Compare null against a non-null value.
        var result = ComparisonHelper.SafeCompareTo(first, second);

        // Assert: Null should be less than non-null.
        result.Should().BeNegative();
    }

    /// <summary>
    /// Test to ensure that a non-null first value is greater than a null second value.
    /// </summary>
    [Fact]
    public void SafeCompareTo_SecondNull_ShouldReturnPositive()
    {
        // Arrange: First is a valid string, second is null.
        string? first = "hello";
        string? second = null;

        // Act: Compare a non-null value against null.
        var result = ComparisonHelper.SafeCompareTo(first, second);

        // Assert: Non-null should be greater than null.
        result.Should().BePositive();
    }

    /// <summary>
    /// Test to ensure that two equal non-null values return zero.
    /// </summary>
    [Fact]
    public void SafeCompareTo_BothEqual_ShouldReturnZero()
    {
        // Arrange: Two equal string values.
        string? first = "hello";
        string? second = "hello";

        // Act: Compare the two equal values.
        var result = ComparisonHelper.SafeCompareTo(first, second);

        // Assert: Equal values should return zero.
        result.Should().Be(0);
    }

    /// <summary>
    /// Test to ensure that a lesser first value returns a negative result.
    /// </summary>
    [Fact]
    public void SafeCompareTo_FirstLessThanSecond_ShouldReturnNegative()
    {
        // Arrange: First is lexicographically less than second.
        string? first = "apple";
        string? second = "banana";

        // Act: Compare the two values.
        var result = ComparisonHelper.SafeCompareTo(first, second);

        // Assert: First should be less than second.
        result.Should().BeNegative();
    }

    /// <summary>
    /// Test to ensure that a greater first value returns a positive result.
    /// </summary>
    [Fact]
    public void SafeCompareTo_FirstGreaterThanSecond_ShouldReturnPositive()
    {
        // Arrange: First is lexicographically greater than second.
        string? first = "banana";
        string? second = "apple";

        // Act: Compare the two values.
        var result = ComparisonHelper.SafeCompareTo(first, second);

        // Assert: First should be greater than second.
        result.Should().BePositive();
    }

    /// <summary>
    /// Test to ensure that SafeCompareTo works correctly with the string type.
    /// </summary>
    [Fact]
    public void SafeCompareTo_WorksWithStringType()
    {
        // Arrange: Two different string values.
        string? first = "abc";
        string? second = "xyz";

        // Act: Compare the two string values.
        var result = ComparisonHelper.SafeCompareTo(first, second);

        // Assert: "abc" should be less than "xyz".
        result.Should().BeNegative();
    }

    /// <summary>
    /// Test to ensure that SafeCompareTo works correctly with the nullable int type.
    /// </summary>
    [Fact]
    public void SafeCompareTo_WorksWithIntType()
    {
        // Arrange: Two int values.
        var first = 5;
        var second = 10;

        // Act: Compare the two int values.
        var result = ComparisonHelper.SafeCompareTo(first, second);

        // Assert: 5 should be less than 10.
        result.Should().BeNegative();
    }
}
