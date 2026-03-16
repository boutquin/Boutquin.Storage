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
using System.Diagnostics;

namespace Boutquin.Storage.Domain.Attributes;

/// <summary>
/// Specifies that a partial record struct is serializable within the storage engine.
/// The source generator will produce <c>ISerializable&lt;T&gt;</c> implementations
/// (Serialize and Deserialize) for the annotated type.
/// </summary>
/// <remarks>
/// Apply to a <c>partial record struct</c>. The type must be declared <c>partial</c>
/// so the generator can add members via a companion partial declaration.
/// <para>
/// Renamed from <c>SerializableAttribute</c> to avoid ambiguity with
/// <c>System.SerializableAttribute</c>, which is imported via <c>ImplicitUsings</c>.
/// </para>
/// <para>
/// This attribute vanishes from compiled output due to <c>[Conditional("BOUTQUIN_STORAGE_GENERATOR")]</c> —
/// the constant is intentionally never defined. <c>ForAttributeWithMetadataName</c> in the
/// generator reads the semantic model built from source (not IL), so annotated types are still
/// discovered. This keeps consumer assemblies clean of compile-time-only annotation markers.
/// </para>
/// </remarks>
[Conditional("BOUTQUIN_STORAGE_GENERATOR")]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class StorageSerializableAttribute : Attribute;
