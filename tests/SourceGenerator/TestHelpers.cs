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
namespace Boutquin.Storage.SourceGenerator.Tests;

/// <summary>
/// Shared helpers for creating CSharpCompilation instances for generator tests.
/// </summary>
internal static class TestHelpers
{
    // Attributes source injected into every test compilation so the generator can find them.
    // In real consumer projects these come from the Domain assembly reference.
    private const string AttributeSource = """
        using System;
        using System.Diagnostics;
        namespace Boutquin.Storage.Domain.Attributes
        {
            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
            [Conditional("BOUTQUIN_STORAGE_GENERATOR")]
            public sealed class KeyAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
            [Conditional("BOUTQUIN_STORAGE_GENERATOR")]
            public sealed class StorageSerializableAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
            public sealed class StorageDefaultsAttribute : Attribute
            {
                public bool GenerateComparisonOperators { get; set; } = true;
            }
        }
        namespace Boutquin.Storage.Domain.Interfaces
        {
            public interface ISerializable<T>
            {
                void Serialize(System.IO.Stream stream);
                static abstract T Deserialize(System.IO.Stream stream);
            }
        }
        namespace Boutquin.Storage.Domain.Helpers
        {
            public static class ComparisonHelper
            {
                public static int SafeCompareTo<T>(T x, T y) where T : IComparable<T>
                    => x is null ? (y is null ? 0 : -1) : x.CompareTo(y);
            }
        }
        """;

    /// <summary>Creates a CSharpCompilation for generator tests.</summary>
    public static CSharpCompilation CreateCompilation(
        string source,
        string? assemblyName = null,
        IEnumerable<MetadataReference>? additionalReferences = null)
    {
        var sources = new[] { AttributeSource, source };
        return CreateCompilationFromSources(sources, assemblyName, additionalReferences);
    }

    /// <summary>Creates a CSharpCompilation from multiple source strings.</summary>
    public static CSharpCompilation CreateCompilationFromSources(
        IEnumerable<string> sources,
        string? assemblyName = null,
        IEnumerable<MetadataReference>? additionalReferences = null)
    {
        var syntaxTrees = sources
            .Select(s => CSharpSyntaxTree.ParseText(s, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest)))
            .ToArray();

        var references = GetBaseReferences()
            .Concat(additionalReferences ?? Enumerable.Empty<MetadataReference>())
            .ToArray();

        return CSharpCompilation.Create(
            assemblyName ?? "TestAssembly",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static IEnumerable<MetadataReference> GetBaseReferences()
    {
        // Resolve core runtime references by loading the known assemblies
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location));

        foreach (var assembly in assemblies)
        {
            yield return MetadataReference.CreateFromFile(assembly.Location);
        }

        // Ensure System.Runtime is included
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var runtimeDll = System.IO.Path.Combine(runtimeDir, "System.Runtime.dll");
        if (System.IO.File.Exists(runtimeDll))
        {
            yield return MetadataReference.CreateFromFile(runtimeDll);
        }
    }

    /// <summary>Runs the generator against a compilation and returns the driver result.</summary>
    public static (CSharpCompilation OutputCompilation, GeneratorDriverRunResult RunResult) RunGenerator(
        string source,
        string? assemblyName = null)
    {
        var compilation = CreateCompilation(source, assemblyName);
        var generator = new StorageSourceGenerator();
        var driver = CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        return ((CSharpCompilation)outputCompilation, driver.GetRunResult());
    }
}
