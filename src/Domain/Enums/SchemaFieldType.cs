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
namespace Boutquin.Storage.Domain.Enums;

/// <summary>
/// Supported field types in a schema definition.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 4 — "Encoding and Evolution": common wire types in schema-aware encoding formats.</para>
/// </summary>
public enum SchemaFieldType
{
    /// <summary>UTF-8 encoded string.</summary>
    String,

    /// <summary>32-bit signed integer.</summary>
    Int32,

    /// <summary>64-bit signed integer.</summary>
    Int64,

    /// <summary>64-bit IEEE 754 floating point.</summary>
    Float64,

    /// <summary>Boolean value.</summary>
    Boolean,

    /// <summary>Opaque binary data.</summary>
    Binary,

    /// <summary>Date and time with timezone offset.</summary>
    Timestamp,
}
