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
using System.Reflection;

using NetArchTest.Rules;

namespace Boutquin.Storage.ArchitectureTests.Helpers;

/// <summary>
/// Base class for architecture tests, providing assembly references
/// and a helper method for extracting failing type names.
/// </summary>
public abstract class BaseArchitectureTest
{
    /// <summary>Gets the Domain assembly.</summary>
    protected static Assembly DomainAssembly =>
        typeof(Domain.Interfaces.IBloomFilter<>).Assembly;

    /// <summary>Gets the Infrastructure assembly.</summary>
    protected static Assembly InfrastructureAssembly =>
        typeof(Infrastructure.DataStructures.BloomFilter<>).Assembly;

    /// <summary>All source assemblies.</summary>
    protected static IEnumerable<Assembly> AllAssemblies =>
        [DomainAssembly, InfrastructureAssembly];

    /// <summary>
    /// Returns a comma-separated list of failing type names from a test result.
    /// </summary>
    protected static string GetFailingTypes(TestResult result) =>
        result.FailingTypes != null
            ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
            : string.Empty;
}
