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
    bool IsKey,
    bool IsRecordStruct,     // true if declared as "record struct" — synthesizes Equals/==/!= automatically
    bool SkipComparable,     // true if IComparable<T> already implemented (BSSG006)
    bool SkipEquatable,      // true if IEquatable<T> already implemented (BSSG006)
    bool SkipSerializable,   // true if ISerializable<T> already implemented (BSSG006)
    EquatableArray<PropertyInfo> Properties,
    EquatableArray<ParentTypeInfo> ParentTypes) : IEquatable<TypeToGenerate>;

// Defaults configuration from [assembly: StorageDefaults(...)].
internal readonly record struct StorageDefaultsConfig(
    bool GenerateComparisonOperators) : IEquatable<StorageDefaultsConfig>
{
    public static readonly StorageDefaultsConfig Default = new StorageDefaultsConfig(GenerateComparisonOperators: true);
}
