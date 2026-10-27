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
/// One reason a candidate schema is incompatible with a reference schema: the offending
/// <see cref="Tag"/> (0 when the problem is not tag-specific, e.g. a missing message) and a
/// human-readable <see cref="Reason"/>.
/// </summary>
/// <param name="Tag">The tag the problem concerns, or 0 when not tag-specific.</param>
/// <param name="Reason">A human-readable description of the incompatibility.</param>
public sealed record SchemaIncompatibility(int Tag, string Reason);
