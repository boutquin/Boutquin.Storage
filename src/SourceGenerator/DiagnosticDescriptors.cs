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

internal static class DiagnosticDescriptors
{
    private const string Category = "Boutquin.Storage.SourceGenerator";

    public static readonly DiagnosticDescriptor UnsupportedPropertyType = new DiagnosticDescriptor(
        id: RuleIdentifiers.UnsupportedPropertyType,
        title: "Unsupported property type",
        messageFormat: "Property '{0}' on type '{1}' has unsupported type '{2}'. Supported types: int, long, string, float, double, bool, byte, char, IEnumerable<T> of those types, and [Key]/[StorageSerializable] types.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generator cannot produce serialization code for this property type.");

    public static readonly DiagnosticDescriptor TypeMustBePartial = new DiagnosticDescriptor(
        id: RuleIdentifiers.TypeMustBePartial,
        title: "Type must be partial",
        messageFormat: "Type '{0}' annotated with [Key] or [StorageSerializable] must be declared as 'partial'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The source generator adds members to this type via a partial declaration. The type must be declared partial.");

    public static readonly DiagnosticDescriptor TypeShouldBeRecordStruct = new DiagnosticDescriptor(
        id: RuleIdentifiers.TypeShouldBeRecordStruct,
        title: "Type should be a record struct",
        messageFormat: "Type '{0}' annotated with [Key] or [StorageSerializable] should be a 'record struct'. Other type kinds are supported but may produce unexpected results.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The generator is optimized for record structs. Using class, struct, or record class types is advisory.");

    public static readonly DiagnosticDescriptor DuplicateAssemblyDefaults = new DiagnosticDescriptor(
        id: RuleIdentifiers.DuplicateAssemblyDefaults,
        title: "Duplicate [StorageDefaults] attribute",
        messageFormat: "Multiple [assembly: StorageDefaults] attributes found. Only one is allowed per assembly.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Only one [assembly: StorageDefaults] attribute is allowed per project.");

    public static readonly DiagnosticDescriptor MutuallyExclusiveAttributes = new DiagnosticDescriptor(
        id: RuleIdentifiers.MutuallyExclusiveAttributes,
        title: "[Key] and [StorageSerializable] are mutually exclusive",
        messageFormat: "Type '{0}' has both [Key] and [StorageSerializable] attributes. These are mutually exclusive — use [Key] for types that are storage keys, [StorageSerializable] for values.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "[Key] and [StorageSerializable] cannot both be applied to the same type.");

    public static readonly DiagnosticDescriptor InterfaceAlreadyImplemented = new DiagnosticDescriptor(
        id: RuleIdentifiers.InterfaceAlreadyImplemented,
        title: "Interface already implemented",
        messageFormat: "Type '{0}' already implements '{1}'. The generator will skip generating that interface and its members.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The generator skips generating members for interfaces that the type already implements.");
}
