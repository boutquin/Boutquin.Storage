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

/// <summary>Classifies how a property is serialized.</summary>
internal enum TypeKind
{
    /// <summary>One of the 8 supported primitive types (int, long, string, etc.).</summary>
    Primitive,
    /// <summary>A type annotated with [Key] or [StorageSerializable].</summary>
    AnnotatedType,
    /// <summary>IEnumerable&lt;T&gt; where T is a supported primitive.</summary>
    PrimitiveCollection,
    /// <summary>IEnumerable&lt;T&gt; where T is an annotated type.</summary>
    AnnotatedCollection,
    /// <summary>Type not supported by the generator — will produce BSSG001.</summary>
    Unsupported,
}

/// <summary>The 8 supported primitive kinds, used to select BinaryWriter/BinaryReader methods.</summary>
internal enum PrimitiveKind
{
    Int,
    Long,
    String,
    Float,
    Double,
    Bool,
    Byte,
    Char,
}

// Property classification record struct — extracted from the semantic model at transform time
// so the CodeGenerator never needs the semantic model. All fields are plain value types.
internal readonly record struct PropertyInfo(
    string Name,
    string TypeName,
    TypeKind Kind,
    PrimitiveKind? Primitive,
    // For AnnotatedType/AnnotatedCollection: the fully qualified type name used in generated code
    string? AnnotatedTypeName) : IEquatable<PropertyInfo>;
