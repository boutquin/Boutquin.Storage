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
using Boutquin.Storage.ArchitectureTests.Helpers;
using FluentAssertions;
using NetArchTest.Rules;

namespace Boutquin.Storage.ArchitectureTests;

/// <summary>
/// Verifies structural design rules: sealed classes, enum namespaces,
/// value object patterns, and interface segregation.
/// </summary>
public sealed class DesignRuleTests : BaseArchitectureTest
{
    [Fact]
    public void NonStaticClasses_ShouldBeSealed()
    {
        foreach (var assembly in AllAssemblies)
        {
            var result = Types.InAssembly(assembly)
                .That()
                .AreClasses()
                .And()
                .AreNotStatic()
                .And()
                .AreNotAbstract()
                .And()
                // Unsealed base classes with subclasses:
                // - AppendOnlyFileStorageEngine → AppendOnlyFileStorageEngineWithIndex
                // - InMemoryStorageIndex → InMemoryFileIndex
                .DoNotHaveNameStartingWith("AppendOnlyFileStorageEngine")
                .And()
                .DoNotHaveNameStartingWith("InMemoryStorageIndex")
                .Should()
                .BeSealed()
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                because: $"non-static non-abstract classes in {assembly.GetName().Name} must be sealed: [{GetFailingTypes(result)}]");
        }
    }

    [Fact]
    public void DomainEnumsNamespace_ShouldOnlyContainEnums()
    {
        var nonEnumTypes = DomainAssembly.GetTypes()
            .Where(t => t.Namespace == "Boutquin.Storage.Domain.Enums" && !t.IsEnum)
            .Select(t => t.FullName)
            .ToList();

        nonEnumTypes.Should().BeEmpty(
            because: "Domain.Enums namespace should only contain enums");
    }

    [Fact]
    public void DomainInterfacesNamespaces_ShouldOnlyContainInterfaces()
    {
        var nonInterfaceTypes = DomainAssembly.GetTypes()
            .Where(t =>
                t.Namespace != null
                && t.Namespace.StartsWith("Boutquin.Storage.Domain.Interfaces", StringComparison.Ordinal)
                && t.IsPublic
                && !t.IsInterface)
            .Select(t => t.FullName)
            .ToList();

        nonInterfaceTypes.Should().BeEmpty(
            because: "Domain.Interfaces namespaces should only contain interfaces");
    }

    [Fact]
    public void AllPublicTypes_ShouldNotBeNestedInOtherPublicTypes()
    {
        foreach (var assembly in AllAssemblies)
        {
            var nestedPublicTypes = assembly.GetTypes()
                .Where(t => t is { IsPublic: false, IsNestedPublic: true } && t.DeclaringType?.IsPublic == true)
                .Select(t => t.FullName)
                .ToList();

            nestedPublicTypes.Should().BeEmpty(
                because: $"public types in {assembly.GetName().Name} should not be nested inside other public types");
        }
    }

    [Fact]
    public void DomainAssembly_ShouldHaveNoExternalDependencies()
    {
        // Domain must depend only on System.* and Boutquin.Domain (the DDD building blocks package)
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Boutquin.Storage.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"Domain must not depend on Infrastructure: [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void ValueObjects_ShouldBeRecordTypes()
    {
        var valueObjectTypes = DomainAssembly.GetTypes()
            .Where(t =>
                t.Namespace == "Boutquin.Storage.Domain.ValueObjects"
                && t.IsPublic
                && !t.IsInterface)
            .ToList();

        foreach (var type in valueObjectTypes)
        {
            // Record classes have <Clone>$; record structs have compiler-generated
            // PrintMembers(StringBuilder). Check for either.
            var isRecordClass = type.GetMethod("<Clone>$",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance) != null;
            var isRecordStruct = type.IsValueType
                && type.GetMethod("PrintMembers",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    null, [typeof(System.Text.StringBuilder)], null) != null;

            (isRecordClass || isRecordStruct).Should().BeTrue(
                because: $"{type.FullName} in ValueObjects must be a record type (class or struct)");
        }
    }
}
