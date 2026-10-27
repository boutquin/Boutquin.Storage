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
/// The structured outcome of a cross-schema compatibility check: a boolean
/// <see cref="IsCompatible"/> verdict and, when not compatible, the list of
/// <see cref="Reasons"/>. A "could not evaluate" outcome (e.g. an unparseable descriptor or an
/// absent message) is reported as <see cref="IsCompatible"/> = <c>false</c> with a descriptive
/// reason — never as an exception — so the cross-format diagnostic is a total function over its inputs.
/// </summary>
/// <param name="IsCompatible">Whether the candidate is compatible with the reference schema.</param>
/// <param name="Reasons">The incompatibilities found; empty when <see cref="IsCompatible"/> is <c>true</c>.</param>
public sealed record SchemaCompatibilityResult(bool IsCompatible, IReadOnlyList<SchemaIncompatibility> Reasons);
