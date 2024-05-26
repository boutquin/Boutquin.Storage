// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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

public class KeyValueSourceGeneratorTests
{
    private static Compilation CreateCompilation(string source)
    {
        return CSharpCompilation.Create("TestAssembly",
            new[] { CSharpSyntaxTree.ParseText(source) },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(KeyAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ISerializable<>).Assembly.Location),
                MetadataReference.CreateFromFile(Assembly.Load(new AssemblyName("mscorlib")).Location),
                MetadataReference.CreateFromFile(Assembly.Load(new AssemblyName("netstandard")).Location),
                MetadataReference.CreateFromFile(Assembly.Load(new AssemblyName("System.Runtime")).Location)
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private GeneratorDriver RunGenerator(Compilation compilation)
    {
        var generator = new KeyValueSourceGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);
        return driver;
    }

    [Fact]
    public void BasicSerializationTest()
    {
        string source = @"
using Boutquin.Storage.Infrastructure;
using Boutquin.Storage.Infrastructure.Attributes;

namespace TestNamespace;

[Key]
public partial record struct Key(int Value);
";
        var compilation = CreateCompilation(source);
        var driver = RunGenerator(compilation);

        // Check the generated code
        var runResult = driver.GetRunResult();
        Assert.Single(runResult.GeneratedTrees);
        var generatedCode = runResult.Results[0].GeneratedSources[0].SourceText.ToString();

        Assert.Contains("public void Serialize(BinaryWriter writer)", generatedCode);
        Assert.Contains("public static Key Deserialize(BinaryReader reader)", generatedCode);
    }

    [Fact]
    public void ComplexTypeSerializationTest()
    {
        string source = @"
using Boutquin.Storage.Infrastructure;
using Boutquin.Storage.Infrastructure.Attributes;
using System.Collections.Generic;

namespace TestNamespace;

[Serializable]
public partial record struct City(string Name, IEnumerable<Attraction> Attractions);

[Serializable]
public partial record struct Attraction(string Name);
";
        var compilation = CreateCompilation(source);
        var driver = RunGenerator(compilation);

        // Check the generated code
        var runResult = driver.GetRunResult();
        Assert.Equal(2, runResult.GeneratedTrees.Length);
        var generatedCode = runResult.Results[0].GeneratedSources[0].SourceText.ToString();

        Assert.Contains("public void Serialize(BinaryWriter writer)", generatedCode);
        Assert.Contains("public static City Deserialize(BinaryReader reader)", generatedCode);
    }

    [Fact]
    public void UnsupportedTypeTest()
    {
        string source = @"
using Boutquin.Storage.Infrastructure;
using Boutquin.Storage.Infrastructure.Attributes;

namespace TestNamespace;

[Serializable]
public partial record struct UnsupportedType(System.DateTime Date);
";
        var compilation = CreateCompilation(source);
        var driver = RunGenerator(compilation);

        // Check diagnostics
        var runResult = driver.GetRunResult();
        var diagnostics = runResult.Results[0].Diagnostics;

        Assert.Contains(diagnostics, d => d.Id == "KVSG001");
    }

    [Fact]
    public void NullValueInCollectionTest()
    {
        string source = @"
using Boutquin.Storage.Infrastructure;
using Boutquin.Storage.Infrastructure.Attributes;
using System.Collections.Generic;

namespace TestNamespace;

[Serializable]
public partial record struct City(string Name, IEnumerable<Attraction> Attractions);

[Serializable]
public partial record struct Attraction(string Name);
";
        var compilation = CreateCompilation(source);
        var driver = RunGenerator(compilation);

        // Check the generated code
        var runResult = driver.GetRunResult();
        Assert.Equal(2, runResult.GeneratedTrees.Length);
        var generatedCode = runResult.Results[0].GeneratedSources[0].SourceText.ToString();

        Assert.Contains("writer.Write(Attractions?.Count() ?? 0);", generatedCode);
        Assert.Contains("if (Attractions != null)", generatedCode);
    }
}