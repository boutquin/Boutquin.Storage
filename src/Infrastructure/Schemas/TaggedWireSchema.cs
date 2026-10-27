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
using System.Reflection;

using Boutquin.Storage.Domain.Interfaces.Schemas;

namespace Boutquin.Storage.Infrastructure.Schemas;

/// <summary>
/// An <see cref="ISchema"/> over a source-generator <c>[StorageSerializable]</c> record's tagged-wire
/// fields. Exposes the loss-free <see cref="TaggedFields"/> that the wire-compatibility rules reason
/// over, and projects them to <see cref="ISchema.Fields"/> only so the schema can round-trip through
/// the existing <see cref="ISchemaRegistry"/>.
/// </summary>
public sealed class TaggedWireSchema : ISchema
{
    /// <summary>The loss-free tagged fields, in the order supplied (tag order by convention).</summary>
    public IReadOnlyList<TaggedWireField> TaggedFields { get; }

    /// <summary>
    /// Tag numbers retired via <c>[ReservedTag]</c> on the record. A later schema that reuses one of
    /// these tags for a live field is a wire-incompatible change (the slot once meant a different
    /// field). Empty when the record reserves no tags.
    /// </summary>
    public IReadOnlyCollection<int> ReservedTags { get; }

    /// <summary>
    /// The tagged-wire schema of each field whose type is (or is a collection of) a nested
    /// <c>[StorageSerializable]</c> record, keyed by the field's tag. A nested record's own tagged
    /// fields are part of the enclosing field's wire identity — its declared <see cref="TaggedWireField.TypeName"/>
    /// stays the same when the nested record evolves in place — so compatibility is judged through
    /// these descriptors, not the type name. Empty when no field holds a nested record.
    /// </summary>
    public IReadOnlyDictionary<int, TaggedWireSchema> NestedSchemas { get; }

    /// <summary>
    /// The ordered-wire layout of each field whose type is (or is a collection of) a nested
    /// <c>[Key]</c> record, keyed by the field's tag. A <c>[Key]</c> child is written on the flat
    /// ordered wire — positional, fixed-width per type, with no tags, names or lengths — so its
    /// declared <see cref="TaggedWireField.TypeName"/> says nothing about its bytes, and none of
    /// the tagged-wire evolution rules apply inside it: the layout must match exactly in every
    /// compatibility mode. Each value is the generator-emitted <c>OrderedWireLayout</c> string (one
    /// token per field, in wire order). Empty when no field holds a nested <c>[Key]</c> record.
    /// </summary>
    public IReadOnlyDictionary<int, string> NestedKeyLayouts { get; }

    /// <summary>
    /// Tags of the fields whose type is (or is a collection of) a record already being described
    /// when this schema was built — a recursive back-reference to this record or to an enclosing
    /// one. The referenced record's fields are not expanded again (they are compared where it was
    /// first reached), but the field is still a nested record on the wire: written
    /// length-prefixed, whatever its declared type's simple name looks like. Compatibility checks
    /// therefore treat these tags as nested-record fields, never as name-derived scalars. Each tag
    /// maps to the back-reference's target, counted in records up the describing chain: 0 is this
    /// record itself, 1 the record that holds it, and so on. Two back-references are the same wire
    /// shape only when they reach the same target; a simple name shared by two different records
    /// does not make them one. Empty when no field refers back to a record being described.
    /// </summary>
    public IReadOnlyDictionary<int, int> RecursiveTags { get; }

    /// <summary>
    /// Initializes a new <see cref="TaggedWireSchema"/> from a tagged-field list, the set of
    /// retired (reserved) tag numbers, and the nested-record schemas of its fields.
    /// </summary>
    /// <param name="taggedFields">The tagged fields, in tag order.</param>
    /// <param name="reservedTags">Retired tag numbers (<c>[ReservedTag]</c>); <c>null</c> ⇒ none.</param>
    /// <param name="nestedSchemas">
    /// The schema of each field (by tag) whose type is a nested <c>[StorageSerializable]</c> record;
    /// <c>null</c> ⇒ none. Copied, so later changes to the supplied dictionary do not alter the schema.
    /// </param>
    /// <param name="nestedKeyLayouts">
    /// The ordered-wire layout of each field (by tag) whose type is a nested <c>[Key]</c> record;
    /// <c>null</c> ⇒ none. Copied, so later changes to the supplied dictionary do not alter the schema.
    /// </param>
    /// <param name="recursiveTags">
    /// The tags of fields that refer back to a record being described, each mapped to its target's
    /// distance up the describing chain (see <see cref="RecursiveTags"/>); <c>null</c> ⇒ none. Copied.
    /// </param>
    public TaggedWireSchema(
        IReadOnlyList<TaggedWireField> taggedFields,
        IReadOnlyCollection<int>? reservedTags = null,
        IReadOnlyDictionary<int, TaggedWireSchema>? nestedSchemas = null,
        IReadOnlyDictionary<int, string>? nestedKeyLayouts = null,
        IReadOnlyDictionary<int, int>? recursiveTags = null)
    {
        ArgumentNullException.ThrowIfNull(taggedFields);
        TaggedFields = taggedFields;
        ReservedTags = reservedTags ?? [];
        NestedSchemas = nestedSchemas is null ? new Dictionary<int, TaggedWireSchema>() : new Dictionary<int, TaggedWireSchema>(nestedSchemas);
        NestedKeyLayouts = nestedKeyLayouts is null ? new Dictionary<int, string>() : new Dictionary<int, string>(nestedKeyLayouts);
        RecursiveTags = recursiveTags is null ? new Dictionary<int, int>() : new Dictionary<int, int>(recursiveTags);
    }

    /// <summary>
    /// Builds a <see cref="TaggedWireSchema"/> from a <c>[StorageSerializable]</c> type by reading its
    /// generator-emitted <c>TaggedWireSchemaFields</c> / <c>TaggedWireReservedTags</c> static descriptors
    /// (reflection over the static members — NOT the <c>[Tag]</c> attributes, which are <c>[Conditional]</c>
    /// and absent from IL).
    /// </summary>
    /// <typeparam name="T">A <c>[StorageSerializable]</c> record type.</typeparam>
    public static TaggedWireSchema FromType<T>() => FromType(typeof(T));

    /// <inheritdoc cref="FromType{T}()" />
    /// <param name="type">A <c>[StorageSerializable]</c> record type.</param>
    /// <exception cref="ArgumentException">The type carries no generated tagged-wire descriptor.</exception>
    /// <remarks>
    /// Each field whose type is a nested <c>[StorageSerializable]</c> record — directly or as a
    /// collection element — has that record's schema captured recursively in
    /// <see cref="NestedSchemas"/>. A recursive reference back to a record already being described
    /// is not expanded again (that record's fields are compared where it was first reached); its field's
    /// tag is listed in <see cref="RecursiveTags"/> instead, with the distance to the record it
    /// refers to. Each
    /// field whose type is a nested <c>[Key]</c> record — directly or as a collection element — has
    /// that record's ordered-wire layout captured in <see cref="NestedKeyLayouts"/>.
    /// </remarks>
    public static TaggedWireSchema FromType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return FromType(type, new List<Type>());
    }

    private static TaggedWireSchema FromType(Type type, List<Type> describing)
    {
        const string fieldsMember = "TaggedWireSchemaFields";
        var fieldsProperty = type.GetProperty(fieldsMember, BindingFlags.Public | BindingFlags.Static)
            ?? throw new ArgumentException(
                $"'{type.FullName}' is not a [StorageSerializable] type: no static '{fieldsMember}' "
                + "descriptor was found. Ensure the type is annotated and the source generator has run.",
                nameof(type));

        var fields = (IReadOnlyList<TaggedWireField>)fieldsProperty.GetValue(null)!;

        var reservedRaw = type
            .GetProperty("TaggedWireReservedTags", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null);
        // Hard cast with null-check: a soft `as` cast silently returns null when the member
        // exists but carries an unexpected type (e.g. IReadOnlyList<uint>), producing a schema
        // that behaves as if no reserved tags exist — a silent correctness bug. An
        // InvalidOperationException here names the type so the caller can diagnose the mismatch.
        IReadOnlyList<int>? reserved = reservedRaw is null
            ? null
            : reservedRaw as IReadOnlyList<int>
              ?? throw new InvalidOperationException(
                  $"'{type.FullName}'.TaggedWireReservedTags exists but returns "
                  + $"'{reservedRaw.GetType().FullName}' instead of IReadOnlyList<int>.");

        describing.Add(type);
        var nested = new Dictionary<int, TaggedWireSchema>();
        var keyLayouts = new Dictionary<int, string>();
        var recursive = new Dictionary<int, int>();
        foreach (var field in fields)
        {
            var propertyType = type.GetProperty(field.Name, BindingFlags.Public | BindingFlags.Instance)?.PropertyType;
            var nestedType = NestedTypeOf(propertyType, fieldsMember);
            if (nestedType is not null)
            {
                var target = describing.LastIndexOf(nestedType);
                if (target >= 0)
                {
                    // A back-reference is still a nested record on the wire; recording the tag keeps
                    // the checkers from classifying it by its (possibly scalar-looking) type name, and
                    // recording how far up the chain it points keeps a retarget to a different record
                    // of the same simple name from passing as the same shape.
                    recursive[field.Tag] = describing.Count - 1 - target;
                }
                else
                {
                    nested[field.Tag] = FromType(nestedType, describing);
                }
            }

            var keyType = NestedTypeOf(propertyType, OrderedWireLayoutMember);
            if (keyType is not null)
            {
                keyLayouts[field.Tag] = keyType.GetProperty(OrderedWireLayoutMember, BindingFlags.Public | BindingFlags.Static)!.GetValue(null) as string
                    ?? throw new InvalidOperationException(
                        $"'{keyType.FullName}'.{OrderedWireLayoutMember} exists but does not return a string.");
            }
        }

        describing.RemoveAt(describing.Count - 1);
        return new TaggedWireSchema(fields, reserved, nested, keyLayouts, recursive);
    }

    private const string OrderedWireLayoutMember = "OrderedWireLayout";

    // The generated record type a field's value is encoded as — identified by the static
    // descriptor member it carries (TaggedWireSchemaFields for [StorageSerializable],
    // OrderedWireLayout for [Key]) — looking through collection element types; null when the
    // field holds no such record.
    private static Type? NestedTypeOf(Type? propertyType, string descriptorMember)
    {
        var current = propertyType;
        while (current is not null && current != typeof(string))
        {
            if (current.GetProperty(descriptorMember, BindingFlags.Public | BindingFlags.Static) is not null)
            {
                return current;
            }

            var enumerable = current.IsGenericType && current.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? current
                : current.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            current = current.IsArray ? current.GetElementType() : enumerable?.GetGenericArguments()[0];
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Lossy, by design: the tagless seven-value <see cref="SchemaFieldType"/> cannot carry a tag or
    /// the generator's full 22-type set. This projection exists only so a tagged-wire schema can be
    /// stored in and retrieved from an <see cref="ISchemaRegistry"/>; compatibility decisions read
    /// <see cref="TaggedFields"/>, never this projection. Tag order is preserved as list order.
    /// The projection is computed once on first access and cached — the schema is immutable, so
    /// re-running the per-field mapping on every property read is pure allocation churn.
    /// </remarks>
    public IReadOnlyList<SchemaField> Fields => _fields ??=
        [.. TaggedFields.Select(f => new SchemaField(f.Name, TaggedWireTypeMapping.Map(f.TypeName), f.IsOptional))];

    private IReadOnlyList<SchemaField>? _fields;
}
