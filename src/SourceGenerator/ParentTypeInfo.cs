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

// Holds the data needed to wrap a nested type in its parent type declaration.
// Extracted from the semantic model at transform time — no SyntaxNode or ISymbol references.
internal readonly record struct ParentTypeInfo(
    string Name,
    string Kind,       // "class", "struct", "record", "record class", "record struct"
    string Modifiers,  // e.g. "public partial"
    string? GenericParameters,  // e.g. "<T, U>"
    string? Constraints)        // e.g. "where T : class"
    : IEquatable<ParentTypeInfo>;
