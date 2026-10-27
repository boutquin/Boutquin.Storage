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
/// Marks a <c>partial</c> record or class as eligible for columnar time-series segment
/// storage. The source generator discovers types annotated with this attribute, selects a
/// codec per property from the CLR type (default) or the property-level
/// <see cref="ColumnEncodingAttribute"/> override, and emits a per-type field plan consumed
/// by the Phase-5 columnar segment format.
/// </summary>
/// <remarks>
/// <para><b>Requirements:</b> the annotated type must be declared <c>partial</c> so the
/// generator can emit a companion partial declaration carrying the field plan. The type must
/// also implement <c>IHasDate</c> — the columnar format is time-series-specific.</para>
/// <para><b>Compile-time-only:</b> this attribute vanishes from compiled output because of
/// <c>[Conditional("BOUTQUIN_STORAGE_GENERATOR")]</c> with a constant that is intentionally
/// never defined. The generator reads the semantic model (source), so the annotation is still
/// discovered via <c>ForAttributeWithMetadataName</c>. Consumer assemblies carry no runtime
/// trace of the attribute.</para>
/// <para><b>Relationship to <see cref="StorageSerializableAttribute"/>:</b> orthogonal. A type
/// may carry either, both, or neither. <see cref="StorageSerializableAttribute"/> drives
/// row-oriented <c>ISerializable&lt;T&gt;</c> emission; <see cref="ColumnarStorableAttribute"/>
/// drives the columnar field plan.</para>
/// </remarks>
[Conditional("BOUTQUIN_STORAGE_GENERATOR")]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class ColumnarStorableAttribute : Attribute;
