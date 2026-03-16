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

public sealed class CachingTests
{
    [Fact]
    public void Pipeline_Data_Models_Have_Value_Equality()
    {
        // TypeToGenerate must compare equal when all fields match — incremental caching depends on this.
        var props = new EquatableArray<PropertyInfo>(new[]
        {
            new PropertyInfo("Value", "long", TypeKind.Primitive, PrimitiveKind.Long, null)
        });
        var parents = EquatableArray<ParentTypeInfo>.Empty;

        var a = new TypeToGenerate("MyKey", "TestNamespace", "public", true, true, false, false, false, props, parents);
        var b = new TypeToGenerate("MyKey", "TestNamespace", "public", true, true, false, false, false, props, parents);

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void EquatableArray_Value_Equality()
    {
        var a = new EquatableArray<PropertyInfo>(new[]
        {
            new PropertyInfo("Name", "string", TypeKind.Primitive, PrimitiveKind.String, null),
            new PropertyInfo("Id", "int", TypeKind.Primitive, PrimitiveKind.Int, null),
        });
        var b = new EquatableArray<PropertyInfo>(new[]
        {
            new PropertyInfo("Name", "string", TypeKind.Primitive, PrimitiveKind.String, null),
            new PropertyInfo("Id", "int", TypeKind.Primitive, PrimitiveKind.Int, null),
        });

        Assert.True(a.Equals(b));
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void EquatableArray_Inequality_On_Different_Content()
    {
        var a = new EquatableArray<PropertyInfo>(new[]
        {
            new PropertyInfo("Name", "string", TypeKind.Primitive, PrimitiveKind.String, null),
        });
        var b = new EquatableArray<PropertyInfo>(new[]
        {
            new PropertyInfo("Id", "int", TypeKind.Primitive, PrimitiveKind.Int, null),
        });

        Assert.False(a.Equals(b));
    }

    [Fact]
    public void StorageDefaultsConfig_Value_Equality()
    {
        var a = new StorageDefaultsConfig(true);
        var b = new StorageDefaultsConfig(true);
        var c = new StorageDefaultsConfig(false);

        a.Should().Be(b);
        a.Should().NotBe(c);
    }

    [Fact]
    public void Second_Run_Returns_Cached_Or_Unchanged()
    {
        // Run the generator twice on the same compilation and verify the second run's
        // tracked steps return Cached or Unchanged (not Modified).
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct MyKey(long Value);
            """;

        var compilation = TestHelpers.CreateCompilation(source);
        var generator = new StorageSourceGenerator().AsSourceGenerator();

        // First run
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { generator },
            driverOptions: new GeneratorDriverOptions(
                disabledOutputs: default,
                trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);

        // Second run — same compilation
        driver = driver.RunGenerators(compilation);
        var runResult = driver.GetRunResult();

        // All tracked steps in the second run should be Cached or Unchanged
        var secondRunSteps = runResult.Results
            .SelectMany(r => r.TrackedSteps)
            .SelectMany(kvp => kvp.Value)
            .SelectMany(s => s.Outputs);

        foreach (var (_, reason) in secondRunSteps)
        {
            reason.Should().BeOneOf(
                new[] { IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged },
                "step should be Cached or Unchanged on second run, but was {0}", reason);
        }
    }

    [Fact]
    public void Modified_Source_Produces_Modified_Step()
    {
        const string source1 = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct MyKey(long Value);
            """;

        const string source2 = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct MyKey(long Value, string Name);
            """;

        var compilation1 = TestHelpers.CreateCompilation(source1);
        var generator = new StorageSourceGenerator().AsSourceGenerator();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { generator },
            driverOptions: new GeneratorDriverOptions(
                disabledOutputs: default,
                trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation1);

        // Second run with modified source
        var compilation2 = TestHelpers.CreateCompilation(source2);
        driver = driver.RunGenerators(compilation2);
        var runResult = driver.GetRunResult();

        // At least one step should report Modified
        var allReasons = runResult.Results
            .SelectMany(r => r.TrackedSteps)
            .SelectMany(kvp => kvp.Value)
            .SelectMany(s => s.Outputs)
            .Select(o => o.Reason)
            .ToList();

        allReasons.Should().Contain(IncrementalStepRunReason.Modified);
    }

    [Fact]
    public void ParentTypeInfo_Value_Equality()
    {
        var a = new ParentTypeInfo("Outer", "class", "public partial", null, null);
        var b = new ParentTypeInfo("Outer", "class", "public partial", null, null);
        var c = new ParentTypeInfo("Inner", "class", "public partial", null, null);

        a.Should().Be(b);
        a.Should().NotBe(c);
    }
}
