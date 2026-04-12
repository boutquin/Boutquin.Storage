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
/// Verifies the layer dependency graph: Domain must not depend on Infrastructure.
/// Infrastructure may depend on Domain.
/// </summary>
public sealed class DependencyTests : BaseArchitectureTest
{
    [Fact]
    public void Domain_ShouldNotDependOnInfrastructure()
    {
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOnAny("Boutquin.Storage.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"Domain must not depend on Infrastructure: [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void Infrastructure_ShouldNotDependOnTests()
    {
        var result = Types.InAssembly(InfrastructureAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Boutquin.Storage.Infrastructure.Tests",
                "Boutquin.Storage.ArchitectureTests")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"Infrastructure must not depend on test projects: [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void Domain_ShouldNotDependOnThirdPartyJsonLibraries()
    {
        // Domain should not depend on System.Text.Json or Newtonsoft.Json
        // Serialization is an Infrastructure concern
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "System.Text.Json",
                "Newtonsoft.Json")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"Domain must not depend on JSON libraries (serialization is Infrastructure): [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void Domain_ShouldNotDependOnFileSystemTypes()
    {
        // Domain interfaces should not reference concrete file system types
        // File I/O is an Infrastructure concern; Domain uses Stream as the abstraction boundary
        var result = Types.InAssembly(DomainAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "System.IO.FileStream",
                "System.IO.File",
                "System.IO.Directory")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"Domain must not depend on file system types: [{GetFailingTypes(result)}]");
    }
}
