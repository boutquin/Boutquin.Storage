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
using Boutquin.Storage.Infrastructure.Schemas;

namespace Boutquin.Storage.Infrastructure.Tests.Schemas;

/// <summary>
/// Tests for <see cref="FieldLevelCompatibilityChecker"/>.
/// </summary>
public sealed class CompatibilityCheckerTests
{
    private readonly FieldLevelCompatibilityChecker _checker = new();

    private static SimpleSchema Schema(params SchemaField[] fields) => new(fields);

    [Fact]
    public void None_AlwaysCompatible()
    {
        var existing = Schema(new SchemaField("A", SchemaFieldType.String, false));
        var candidate = Schema(); // empty

        _checker.IsCompatible(existing, candidate, CompatibilityMode.None).Should().BeTrue();
    }

    [Fact]
    public void Backward_AddOptionalField_Compatible()
    {
        var existing = Schema(new SchemaField("Name", SchemaFieldType.String, false));
        var candidate = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Age", SchemaFieldType.Int32, true));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeTrue();
    }

    [Fact]
    public void Backward_AddRequiredField_Incompatible()
    {
        var existing = Schema(new SchemaField("Name", SchemaFieldType.String, false));
        var candidate = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Age", SchemaFieldType.Int32, false)); // Required new field

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeFalse();
    }

    [Fact]
    public void Backward_RemoveRequiredField_Incompatible()
    {
        var existing = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Email", SchemaFieldType.String, false));
        var candidate = Schema(new SchemaField("Name", SchemaFieldType.String, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeFalse();
    }

    [Fact]
    public void Backward_RemoveOptionalField_Compatible()
    {
        var existing = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Nickname", SchemaFieldType.String, true));
        var candidate = Schema(new SchemaField("Name", SchemaFieldType.String, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeTrue();
    }

    [Fact]
    public void Forward_AddOptionalField_Compatible()
    {
        var existing = Schema(new SchemaField("Name", SchemaFieldType.String, false));
        var candidate = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Age", SchemaFieldType.Int32, true));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Forward).Should().BeTrue();
    }

    [Fact]
    public void Forward_AddRequiredField_Incompatible()
    {
        var existing = Schema(new SchemaField("Name", SchemaFieldType.String, false));
        var candidate = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Age", SchemaFieldType.Int32, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Forward).Should().BeFalse();
    }

    [Fact]
    public void Forward_RemoveOptionalField_Compatible()
    {
        var existing = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Nickname", SchemaFieldType.String, true));
        var candidate = Schema(new SchemaField("Name", SchemaFieldType.String, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Forward).Should().BeTrue();
    }

    [Fact]
    public void Full_AddOptionalField_Compatible()
    {
        var existing = Schema(new SchemaField("Name", SchemaFieldType.String, false));
        var candidate = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Age", SchemaFieldType.Int32, true));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Full).Should().BeTrue();
    }

    [Fact]
    public void Full_AddRequiredField_Incompatible()
    {
        var existing = Schema(new SchemaField("Name", SchemaFieldType.String, false));
        var candidate = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Age", SchemaFieldType.Int32, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Full).Should().BeFalse();
    }
}
