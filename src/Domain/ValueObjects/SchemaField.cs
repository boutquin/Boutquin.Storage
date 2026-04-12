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
namespace Boutquin.Storage.Domain.ValueObjects;

/// <summary>
/// Describes a single field in a schema definition.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 4 — "Encoding and Evolution": field-level schema definitions.</para>
/// </summary>
/// <param name="Name">The field name.</param>
/// <param name="FieldType">The data type of the field.</param>
/// <param name="IsOptional">Whether the field may be absent in serialized data.</param>
public readonly record struct SchemaField(string Name, SchemaFieldType FieldType, bool IsOptional);
