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
namespace Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

/// <summary>
/// One entry in the source-generator-emitted columnar field plan for a type marked with
/// <c>[ColumnarStorable]</c>: the property name and the encoding the generator selected
/// for it (from either the type-based default or the property-level
/// <c>[ColumnEncoding]</c> override).
/// </summary>
/// <param name="Name">Property name as declared on the source type.</param>
/// <param name="Encoding">Encoding the source generator selected for this property.</param>
/// <remarks>
/// Surfaced through a static <c>ColumnarFieldPlan</c> property on each generated partial
/// type extension; read by the Phase-5 columnar segment format at construction time to route
/// each column to its codec without reflection.
/// </remarks>
public readonly record struct ColumnarFieldDescriptor(string Name, ColumnEncoding Encoding);
