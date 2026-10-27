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
namespace Boutquin.Storage.SourceGenerator;

internal static class RuleIdentifiers
{
    public const string UnsupportedPropertyType = "BSSG001";
    public const string TypeMustBePartial = "BSSG002";
    public const string TypeShouldBeRecordStruct = "BSSG003";
    public const string DuplicateAssemblyDefaults = "BSSG004";
    public const string MutuallyExclusiveAttributes = "BSSG005";
    public const string InterfaceAlreadyImplemented = "BSSG006";
    public const string IncompatibleColumnEncoding = "BSSG007";
    public const string ExcludedPropertyType = "BSSG008";

    // Phase 2 — tag-validation diagnostics. BSSG009 is reserved unshipped.
    public const string MissingTag = "BSSG010";
    public const string DuplicateTag = "BSSG011";
    public const string TagOutOfRange = "BSSG012";
    public const string ReservedTagCollision = "BSSG013";

    // Phase 3 — [Key] shape diagnostics.
    public const string KeyNestedRecord = "BSSG014";
    public const string KeyCollection = "BSSG015";
    public const string KeyNestedNullable = "BSSG016";

    // Phase A2 (v2.0.0 storage hardening) — decimal-scale canonicalisation for [Key] decimals.
    public const string MissingDecimalScale = "BSSG017";

    // Phase F2 (v2.0.0 storage hardening) — [Packed] and [Unpacked] on the same property are
    // mutually exclusive; apply at most one.
    public const string ConflictingPackedUnpacked = "BSSG018";

    // [Key] string properties must be non-nullable: strings have no leaf-level null
    // discriminator in the ordered wire, so a string? key would compile clean, order fine
    // in a memtable, and fail only at flush-time Serialize.
    public const string KeyNullableString = "BSSG019";

    // [ColumnarStorable] settable public properties must map to a columnar encoding. Silent
    // omission would mean the column is never written and Rehydrate leaves the property at
    // default(T) on every read — runtime data loss from a compile-time shape mistake.
    public const string ColumnarFieldNotEncodable = "BSSG020";

    // Persistence-path hardening — an array / stackalloc / ArrayPool.Rent / Array.CreateInstance
    // whose size derives from a decoded (untrusted) length read out of a byte payload, header,
    // footer, or wire frame, with no preceding bound of that length against the bytes actually
    // remaining on the same path. The durable tooling-absorption fix for the unbounded-allocation
    // defect class the 2026-06-12 storage-hardening review closed eleven times across the codecs,
    // segment formats, footer readers, SSTable block decoder, and serializers.
    public const string UnboundedDecodedLengthAllocation = "BSSG021";

    // [ColumnarStorable] requires the target type to be a record (record class or record struct).
    // The columnar code generator emits C# `with` expressions for Rehydrate and ProjectTo; these
    // are only valid on record types. Plain class or struct targets would compile silently in the
    // generator but produce CS8858/CS9035 in the consumer build.
    public const string ColumnarStorableMustBeRecord = "BSSG022";

    // The row generator rebuilds a deserialized value with a positional constructor call over
    // every serialized property, and a projected value with `with` expressions. A type with no
    // constructor taking those properties positionally (a body-property record, a hand-written
    // constructor in another order), or a non-record class (`with` is record/struct-only), gets
    // emitted code that does not compile (CS1729 / CS1503 / CS8858). Reject it instead.
    public const string TypeNotConstructible = "BSSG025";
}
