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
using Boutquin.Storage.Domain.Interfaces.Schemas;

namespace Boutquin.Storage.Infrastructure.Schemas;

/// <summary>
/// An <see cref="ISchemaCompatibilityChecker"/> over <see cref="TaggedWireSchema"/> instances that
/// applies the Protobuf-family wire-compatibility rules: fields are identified by tag (not name or
/// order), unknown tags are skipped by readers, and absent tags default-fill. Adding a tag and
/// omitting a writer's tag are therefore safe in every mode; a stable tag whose type leaves its
/// wire-compatibility class, and the reuse of a tag retired with <c>[ReservedTag]</c>, are fatal in
/// every mode. A field holding a nested <c>[StorageSerializable]</c> record is judged by the same
/// rules applied recursively to the nested record's own schema
/// (<see cref="TaggedWireSchema.NestedSchemas"/>). A field holding a nested <c>[Key]</c> record is
/// written on the positional, tagless ordered wire, so its layout
/// (<see cref="TaggedWireSchema.NestedKeyLayouts"/>) must be identical in every mode. An enum field is additionally judged by the
/// integer type it is encoded as (<see cref="TaggedWireField.WireTypeName"/>), since its name stays
/// the same when its underlying type changes. Within a varint numeric family the modes
/// DIVERGE on width changes: widening is backward-safe only (the new, wider reader fills in missing bits of old bytes) and narrowing is
/// forward-safe only (the old, wider reader decodes every value the new narrower writer produces);
/// the losing direction throws <see cref="OverflowException"/> at read time on out-of-range stored
/// values because the generated readers decode through <c>checked((intN)…)</c>. <c>Full</c>
/// therefore requires equal widths. Plugs straight into <see cref="ISchemaRegistry"/> via the
/// existing bool-returning contract.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026),
/// Ch. 5 — "Encoding and Evolution": Protobuf field tags, unknown-field skipping, and the
/// int32→int64 truncation hazard ("if old code reads data written by new code … it will be
/// truncated").</para>
/// </summary>
public sealed class TaggedWireCompatibilityChecker : ISchemaCompatibilityChecker
{
    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// Thrown when either schema is not a <see cref="TaggedWireSchema"/> — this checker reasons over
    /// tag metadata that only a tagged-wire schema carries.
    /// </exception>
    public bool IsCompatible(ISchema existing, ISchema candidate, CompatibilityMode mode)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);

        if (mode == CompatibilityMode.None)
        {
            return true;
        }

        return AreCompatible(AsTagged(existing, nameof(existing)), AsTagged(candidate, nameof(candidate)), mode);
    }

    private static bool AreCompatible(TaggedWireSchema existingTagged, TaggedWireSchema candidateTagged, CompatibilityMode mode)
    {
        var existingByTag = existingTagged.TaggedFields.ToDictionary(f => f.Tag);

        foreach (var field in candidateTagged.TaggedFields)
        {
            // Reusing a tag the prior schema retired with [ReservedTag] is fatal: the slot once
            // meant a different field, so old bytes under that tag would be misread.
            if (existingTagged.ReservedTags.Contains(field.Tag))
            {
                return false;
            }

            // A tag carried by both schemas whose type is not wire-compatible under this mode is
            // fatal (covers a repurposed tag, a cross-class change, and a numeric width change in
            // the direction the mode protects). Names may change freely — identity is the tag.
            if (existingByTag.TryGetValue(field.Tag, out var existingField)
                && !IsFieldCompatible(existingTagged, existingField, candidateTagged, field, mode))
            {
                return false;
            }
        }

        // Protobuf-family: an added tag is skipped by old readers and a removed tag default-fills
        // for new readers, so add/remove are compatible in every checked mode.
        return true;
    }

    // Compatibility is decided by the WIRE-level decode class, not the declared type name. Comparing
    // names would reject a wire-compatible widening such as int→long (both zig-zag varint) —
    // contradicting Protobuf's varint rule (DDIA 2e Ch. 5). The framing WireType alone is not
    // enough either: distinct, non-interchangeable types share one framing (int/uint both Varint;
    // guid/decimal/int128/uint128 all Fixed128; string/datetimeoffset both LengthPrefixed). Class
    // equality is the necessary gate; within the varint numeric families the mode then decides the
    // direction a width change is allowed in:
    //   Backward — the CANDIDATE (new) schema's readers read data written by EXISTING (old)
    //   writers, so the candidate must be at least as wide (widening OK, narrowing fatal: the
    //   generated checked((intN)…) reads overflow on out-of-range old values).
    //   Forward — EXISTING (old) readers read data written by CANDIDATE (new) writers, so the
    //   candidate must be no wider (narrowing OK, widening fatal for the same reason mirrored).
    //   Full — both directions: equal width only.
    // A nested [StorageSerializable] record is a non-numeric class whose declared type name stays
    // the same while its own tagged fields change (in place, or under a moved namespace), and the
    // generated reader skips a nested tag whose wire type no longer matches — so class equality
    // says nothing about it. Its own schema is compared recursively under the same mode instead;
    // a nested descriptor on only one side fails closed.
    private static bool IsFieldCompatible(
        TaggedWireSchema existingSchema,
        TaggedWireField existingField,
        TaggedWireSchema candidateSchema,
        TaggedWireField candidateField,
        CompatibilityMode mode)
    {
        var existingClass = TaggedWireTypeMapping.CompatibilityClassOf(existingField.TypeName);
        var candidateClass = TaggedWireTypeMapping.CompatibilityClassOf(candidateField.TypeName);
        if (!string.Equals(existingClass, candidateClass, StringComparison.Ordinal))
        {
            return false;
        }

        // An enum keeps its name while its underlying integer type — the bytes the encoder
        // actually writes — changes signedness or width, so beyond the declared-name gate above the
        // encoded type (TaggedWireField.WireTypeName) must pass the same class and width rules a
        // raw integer field does. For every non-enum field the encoded type is the declared type.
        var existingWire = existingField.WireTypeName ?? existingField.TypeName;
        var candidateWire = candidateField.WireTypeName ?? candidateField.TypeName;
        if (!string.Equals(
            TaggedWireTypeMapping.CompatibilityClassOf(existingWire),
            TaggedWireTypeMapping.CompatibilityClassOf(candidateWire),
            StringComparison.Ordinal))
        {
            return false;
        }

        // The nested-record descriptors are consulted BEFORE the name-derived numeric width: a
        // generated record may legally be named Long, Int32, UInt64, … and its simple name would
        // otherwise route it into the varint width rule, which never reads its layout or schema.
        // A nested [Key] record is written on the ordered wire: positional, fixed-width, no
        // tags to skip and no type information to disagree with, so any layout change makes old
        // bytes decode to different values silently. Its layout must be identical in every mode.
        var existingIsKey = existingSchema.NestedKeyLayouts.TryGetValue(existingField.Tag, out var existingLayout);
        var candidateIsKey = candidateSchema.NestedKeyLayouts.TryGetValue(candidateField.Tag, out var candidateLayout);
        if (existingIsKey || candidateIsKey)
        {
            // A layout on only one side is unverifiable and fails closed.
            return existingIsKey && candidateIsKey
                && string.Equals(existingLayout, candidateLayout, StringComparison.Ordinal);
        }

        // A nested [StorageSerializable] record: its own tagged fields decide. A nested schema on
        // only one side fails closed.
        var existingIsRecord = existingSchema.NestedSchemas.TryGetValue(existingField.Tag, out var existingNested);
        var candidateIsRecord = candidateSchema.NestedSchemas.TryGetValue(candidateField.Tag, out var candidateNested);
        var existingIsBackRef = existingSchema.RecursiveTags.TryGetValue(existingField.Tag, out var existingTarget);
        var candidateIsBackRef = candidateSchema.RecursiveTags.TryGetValue(candidateField.Tag, out var candidateTarget);
        if (existingIsRecord || candidateIsRecord || existingIsBackRef || candidateIsBackRef)
        {
            // A recursive back-reference is a nested record whose fields are compared where it was
            // first reached, so two back-references agree here when they reach the same record of
            // the describing chain (the chains are aligned, because the comparison reached this field
            // through the same nesting on both sides). A back-reference retargeted to another record,
            // or against anything else (a scalar, or a different nested record), is unverifiable and
            // fails closed.
            if (existingIsBackRef || candidateIsBackRef)
            {
                return existingIsBackRef && candidateIsBackRef && existingTarget == candidateTarget;
            }

            return existingIsRecord && candidateIsRecord && AreCompatible(existingNested!, candidateNested!, mode);
        }

        var existingWidth = TaggedWireTypeMapping.NumericWidthBits(existingWire);
        if (existingWidth == 0)
        {
            // Non-numeric scalar class: class equality means identical wire identity.
            return true;
        }

        var candidateWidth = TaggedWireTypeMapping.NumericWidthBits(candidateWire);
        return mode switch
        {
            CompatibilityMode.Backward => candidateWidth >= existingWidth,
            CompatibilityMode.Forward => candidateWidth <= existingWidth,
            CompatibilityMode.Full => candidateWidth == existingWidth,
            // None short-circuits in IsCompatible; treat any future mode conservatively.
            _ => candidateWidth == existingWidth,
        };
    }

    private static TaggedWireSchema AsTagged(ISchema schema, string paramName)
        => schema as TaggedWireSchema
           ?? throw new ArgumentException(
               $"{nameof(TaggedWireCompatibilityChecker)} requires {nameof(TaggedWireSchema)} instances; "
               + $"got {schema.GetType().Name}.",
               paramName);
}
