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
/// Tests for <see cref="AvroFieldLevelCompatibilityChecker"/>.
/// </summary>
public sealed class CompatibilityCheckerTests
{
    private readonly AvroFieldLevelCompatibilityChecker _checker = new();

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
    public void Backward_RemoveRequiredField_Compatible()
    {
        // Removing a field is always backward-compatible: a new reader ignores the extra field
        // present in old data (DDIA 2e Ch. 5; finding #4). This is the asymmetry vs. Forward.
        var existing = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Email", SchemaFieldType.String, false));
        var candidate = Schema(new SchemaField("Name", SchemaFieldType.String, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeTrue();
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
    public void Forward_AddRequiredField_Compatible()
    {
        // Adding a field is always forward-compatible: an old reader skips the unknown field
        // (DDIA 2e Ch. 5; finding #4). This is the mirror of Backward's remove-is-always-safe.
        var existing = Schema(new SchemaField("Name", SchemaFieldType.String, false));
        var candidate = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Age", SchemaFieldType.Int32, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Forward).Should().BeTrue();
    }

    [Fact]
    public void Forward_RemoveRequiredField_Incompatible()
    {
        // Removing a required field IS forward-breaking: the old reader expects it and the new
        // data omits it with no default (DDIA 2e Ch. 5; finding #4). Pins the asymmetry — this
        // is the case Backward_RemoveRequiredField_Compatible allows but Forward rejects.
        var existing = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Email", SchemaFieldType.String, false));
        var candidate = Schema(new SchemaField("Name", SchemaFieldType.String, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Forward).Should().BeFalse();
    }

    // ── Optionality flips on RETAINED fields ─────────────────────────────────
    // A field present in both schemas can still break compatibility by flipping optionality:
    // optional→required breaks Backward (old data may omit the field — IsOptional means "may
    // be absent in serialized data" — and the new required reader has no default: the same
    // hazard as ADDING a required field); required→optional breaks Forward (the new writer may
    // omit the field and the old required reader has no default: the same hazard as REMOVING a
    // required field).

    [Fact]
    public void Backward_RetainedFieldOptionalToRequired_Incompatible()
    {
        var existing = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Email", SchemaFieldType.String, true));
        var candidate = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Email", SchemaFieldType.String, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeFalse();
    }

    [Fact]
    public void Forward_RetainedFieldRequiredToOptional_Incompatible()
    {
        var existing = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Email", SchemaFieldType.String, false));
        var candidate = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Email", SchemaFieldType.String, true));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Forward).Should().BeFalse();
    }

    [Fact]
    public void Backward_RetainedFieldRequiredToOptional_Compatible()
    {
        // Old data always carries the field (it was required); the new optional reader copes
        // with presence trivially. Backward-safe.
        var existing = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Email", SchemaFieldType.String, false));
        var candidate = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Email", SchemaFieldType.String, true));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeTrue();
    }

    [Fact]
    public void Forward_RetainedFieldOptionalToRequired_Compatible()
    {
        // The new writer always writes the field (it is required); the old optional reader
        // copes with presence trivially. Forward-safe.
        var existing = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Email", SchemaFieldType.String, true));
        var candidate = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Email", SchemaFieldType.String, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Forward).Should().BeTrue();
    }

    [Fact]
    public void Backward_AddRequiredField_ForwardCompatible_AsymmetryHolds()
    {
        // The mirror pin: adding a required field is Backward-incompatible but Forward-compatible.
        var existing = Schema(new SchemaField("Name", SchemaFieldType.String, false));
        var candidate = Schema(
            new SchemaField("Name", SchemaFieldType.String, false),
            new SchemaField("Age", SchemaFieldType.Int32, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeFalse();
        _checker.IsCompatible(existing, candidate, CompatibilityMode.Forward).Should().BeTrue();
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

    // ───── Type changes on retained fields (Avro writer/reader resolution) ─────

    [Fact]
    public void Backward_IncompatibleTypeChange_OnRetainedField_Incompatible()
    {
        // Changing a retained field's type to one Avro cannot resolve (Int32 → String) breaks
        // backward compatibility: the new reader cannot decode the old writer's bytes (DDIA 2e
        // Ch. 5 — a type change is only safe when Avro can convert it).
        var existing = Schema(new SchemaField("Age", SchemaFieldType.Int32, false));
        var candidate = Schema(new SchemaField("Age", SchemaFieldType.String, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeFalse();
    }

    [Fact]
    public void Forward_IncompatibleTypeChange_OnRetainedField_Incompatible()
    {
        // The forward mirror: an old String reader cannot decode a new Int32 writer's bytes.
        var existing = Schema(new SchemaField("Age", SchemaFieldType.String, false));
        var candidate = Schema(new SchemaField("Age", SchemaFieldType.Int32, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Forward).Should().BeFalse();
    }

    [Fact]
    public void Backward_NumericPromotion_OnRetainedField_Compatible()
    {
        // Avro numeric promotion: a new reader can read old int data as long (DDIA 2e Ch. 5). The
        // type-change check must allow valid promotions, not reject every type change.
        var existing = Schema(new SchemaField("Count", SchemaFieldType.Int32, false));
        var candidate = Schema(new SchemaField("Count", SchemaFieldType.Int64, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeTrue();
    }

    [Fact]
    public void Full_NumericPromotion_OnRetainedField_Incompatible_ForwardDirectionFails()
    {
        // int→long is backward-compatible (new long reader reads old int data) but NOT forward-
        // compatible (an old int reader cannot read new long data), so Full must reject it.
        var existing = Schema(new SchemaField("Count", SchemaFieldType.Int32, false));
        var candidate = Schema(new SchemaField("Count", SchemaFieldType.Int64, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Full).Should().BeFalse();
    }

    /// <summary>
    /// Pins the remaining Avro promotion-table rows (Avro spec schema resolution; DDIA 2e Ch. 5
    /// names int→long→double and string↔bytes as the blessed promotions): each numeric
    /// promotion is readable in the promoting direction (Backward), not in the reverse
    /// direction, and the symmetric string↔bytes pair is readable both ways.
    /// </summary>
    [Theory]
    [InlineData(SchemaFieldType.Int32, SchemaFieldType.Float64, true)]
    [InlineData(SchemaFieldType.Int64, SchemaFieldType.Float64, true)]
    [InlineData(SchemaFieldType.Float64, SchemaFieldType.Int32, false)]  // demotion — reader would truncate
    [InlineData(SchemaFieldType.Float64, SchemaFieldType.Int64, false)]
    [InlineData(SchemaFieldType.String, SchemaFieldType.Binary, true)]   // symmetric per the Avro spec
    [InlineData(SchemaFieldType.Binary, SchemaFieldType.String, true)]
    public void Backward_PromotionTable_OnRetainedField(SchemaFieldType writer, SchemaFieldType reader, bool compatible)
    {
        var existing = Schema(new SchemaField("F", writer, false));
        var candidate = Schema(new SchemaField("F", reader, false));

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().Be(compatible,
            $"Avro schema resolution {(compatible ? "blesses" : "does not bless")} reading writer-{writer} data as reader-{reader}");
    }
}
