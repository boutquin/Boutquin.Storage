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
namespace Boutquin.Storage.Domain.Attributes;

/// <summary>
/// Assembly-level attribute for configuring project-wide source generator defaults.
/// Apply once per assembly: <c>[assembly: StorageDefaults(...)]</c>.
/// </summary>
/// <remarks>
/// Both StronglyTypedId (<c>[StronglyTypedIdDefaults]</c>) and Vogen (<c>[VogenDefaults]</c>)
/// provide a similar assembly-level defaults attribute as an extension point for future
/// generator configuration.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class StorageDefaultsAttribute : Attribute
{
    /// <summary>
    /// Whether the generator should produce comparison operators
    /// (<c>&lt;</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c>) for <c>[Key]</c> types.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool GenerateComparisonOperators { get; set; } = true;
}
