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

// Central pipeline intermediate type. All data extracted from the semantic model at transform
// time — no SyntaxNode, ISymbol, Compilation, or Location references anywhere in this type or
// its nested types. This ensures the incremental pipeline can cache correctly: two
// TypeToGenerate values with the same fields compare equal across compilations.
internal readonly record struct TypeToGenerate(
    string Name,
    string? Namespace,       // null for global namespace
    string Accessibility,    // "public" or "internal"
    string TypeKeyword,      // user's keyword: "record struct" / "record class" / "struct" / "class" — the generated partial repeats it verbatim, else CS0261 keyword conflict
    bool IsKey,
    bool IsRecordStruct,     // true if declared as "record struct" — synthesizes Equals/==/!= automatically
    bool SkipComparable,     // true if IComparable<T> already implemented (BSSG006)
    bool SkipEquatable,      // true if IEquatable<T> already implemented (BSSG006)
    bool SkipSerializable,   // true if ISerializable<T> already implemented (BSSG006)
    bool SkipProjectable,    // true if IProjectable<TSelf> is explicitly in the base list — the generator defers to the user's implementation
    EquatableArray<PropertyInfo> Properties,
    // Tag numbers retired via [ReservedTag(N)] on the type, sorted ascending for deterministic
    // emission. Surfaced at runtime in the TaggedWireReservedTags descriptor so the schema-registry
    // bridge can reject a later schema that reuses a retired slot.
    EquatableArray<int> ReservedTags,
    EquatableArray<ParentTypeInfo> ParentTypes) : IEquatable<TypeToGenerate>
{
    // True for both "record struct" and "record class": the C# compiler auto-synthesizes
    // Equals/GetHashCode/==/!=/IEquatable<T> for any record kind, so the generator must NOT
    // re-emit them (CS0102/CS0111) or re-declare IEquatable<T> in the base list. Computed from
    // TypeKeyword so it stays consistent with the emitted declaration. Derived (no backing
    // field), so it does not affect record-struct value equality / caching.
    public bool IsRecord => TypeKeyword.StartsWith("record", global::System.StringComparison.Ordinal);
}

// Defaults configuration from [assembly: StorageDefaults(...)].
internal readonly record struct StorageDefaultsConfig(
    bool GenerateComparisonOperators) : IEquatable<StorageDefaultsConfig>
{
    public static readonly StorageDefaultsConfig Default = new(GenerateComparisonOperators: true);
}
