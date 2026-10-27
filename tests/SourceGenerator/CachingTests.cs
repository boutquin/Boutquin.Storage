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
    // The post-Phase-1 PropertyInfo carries an EncodableTypeDescriptor rather than a bare
    // PrimitiveKind; Phase 2 adds a Tag field for wire-format identity. Primitive helper here
    // builds one for the legacy kinds these tests lock, defaulting to Tag=1 (the lowest
    // legal tag; unique per test constructed instance).
    private static PropertyInfo Primitive(string name, string clrType, EncodableTypeKind kind, int? tag = 1)
        => new(
            name,
            clrType,
            TypeKind.Primitive,
            new EncodableTypeDescriptor(kind, clrType, null, null, null, null),
            null,
            tag,
            null,
            CollectionEncoding.Default);

    [Fact]
    public void Pipeline_Data_Models_Have_Value_Equality()
    {
        // TypeToGenerate must compare equal when all fields match — incremental caching depends on this.
        var props = new EquatableArray<PropertyInfo>(
        [
            Primitive("Value", "long", EncodableTypeKind.Int64),
        ]);
        var parents = EquatableArray<ParentTypeInfo>.Empty;

        var a = new TypeToGenerate("MyKey", "TestNamespace", "public", "record struct", true, true, false, false, false, false, props, EquatableArray<int>.Empty, parents);
        var b = new TypeToGenerate("MyKey", "TestNamespace", "public", "record struct", true, true, false, false, false, false, props, EquatableArray<int>.Empty, parents);

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void EquatableArray_Value_Equality()
    {
        var a = new EquatableArray<PropertyInfo>(
        [
            Primitive("Name", "string", EncodableTypeKind.String, tag: 1),
            Primitive("Id", "int", EncodableTypeKind.Int32, tag: 2),
        ]);
        var b = new EquatableArray<PropertyInfo>(
        [
            Primitive("Name", "string", EncodableTypeKind.String, tag: 1),
            Primitive("Id", "int", EncodableTypeKind.Int32, tag: 2),
        ]);

        Assert.True(a.Equals(b));
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void EquatableArray_Inequality_On_Different_Content()
    {
        var a = new EquatableArray<PropertyInfo>(
        [
            Primitive("Name", "string", EncodableTypeKind.String, tag: 1),
        ]);
        var b = new EquatableArray<PropertyInfo>(
        [
            Primitive("Id", "int", EncodableTypeKind.Int32, tag: 1),
        ]);

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
            generators: [generator],
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
                [IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged],
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
            generators: [generator],
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

    /// <summary>
    /// Verifies that <see cref="LocationInfo"/> satisfies value equality — two instances
    /// constructed from the same FilePath, Span, and LinePositionSpan must be equal and
    /// produce the same hash code.  This is required for incremental caching: LocationInfo
    /// is stored in pipeline model types, so it must compare equal across compilation passes
    /// for the generator's tracked steps to return Cached instead of Modified.
    /// </summary>
    [Fact]
    public void LocationInfo_Value_Equality()
    {
        var span = new TextSpan(42, 10);
        var lineSpan = new LinePositionSpan(new LinePosition(3, 5), new LinePosition(3, 15));

        var a = new LocationInfo("TestFile.cs", span, lineSpan);
        var b = new LocationInfo("TestFile.cs", span, lineSpan);
        var different = new LocationInfo("OtherFile.cs", span, lineSpan);

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.Should().NotBe(different);
    }

    /// <summary>
    /// Verifies that an <see cref="EquatableArray{T}"/> of <see cref="LocationInfo"/> values
    /// satisfies value equality, confirming LocationInfo can participate in pipeline model
    /// types wrapped by EquatableArray without breaking incremental caching.
    /// </summary>
    [Fact]
    public void EquatableArray_Of_LocationInfo_Value_Equality()
    {
        var span = new TextSpan(1, 5);
        var lineSpan = new LinePositionSpan(new LinePosition(0, 1), new LinePosition(0, 6));

        var a = new EquatableArray<LocationInfo>(
        [
            new LocationInfo("File.cs", span, lineSpan),
        ]);
        var b = new EquatableArray<LocationInfo>(
        [
            new LocationInfo("File.cs", span, lineSpan),
        ]);

        a.Equals(b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
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

    /// <summary>
    /// AC-13.2 — Incremental-caching invariant: no pipeline model field may hold a
    /// <c>Microsoft.CodeAnalysis.*</c> type.  Roslyn types are reference-equal across
    /// compilation passes only coincidentally; their presence in a model type prevents the
    /// incremental generator from caching steps across re-runs because the model will never
    /// compare equal between passes.
    ///
    /// Mutation note (what would make this test RED):
    ///   Add a field of type <c>ISymbol</c>, <c>INamedTypeSymbol</c>, <c>Location</c>,
    ///   <c>SyntaxNode</c>, or any other type whose <see cref="System.Type.Namespace"/> begins
    ///   with "Microsoft.CodeAnalysis" to any of the four pipeline model types checked here.
    ///   The generator would still compile but the tracked step would never be Cached on a
    ///   second run, silently killing incremental performance.
    /// </summary>
    [Fact]
    public void PipelineModelTypes_ContainNoRoslynTypeFields()
    {
        // The four pipeline model types that participate in the incremental pipeline.
        // Roslyn types must NOT appear as field types (including nested generic type arguments)
        // in any of them — if they do, equality-based caching breaks silently.
        var pipelineModelTypes = new[]
        {
            typeof(TypeToGenerate),
            typeof(PropertyInfo),
            typeof(EncodableTypeDescriptor),
            // ColumnarTypePlan is an inner type of ColumnarSourceGenerator
            typeof(ColumnarSourceGenerator).GetNestedType("ColumnarTypePlan", System.Reflection.BindingFlags.NonPublic),
        };

        foreach (var modelType in pipelineModelTypes)
        {
            modelType.Should().NotBeNull($"pipeline model type {modelType?.Name} must be accessible via reflection");

            var fields = modelType!.GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);

            foreach (var field in fields)
            {
                // Check the field's declared type — and, for generics, all type arguments.
                var typesToCheck = new Queue<System.Type>();
                typesToCheck.Enqueue(field.FieldType);

                while (typesToCheck.Count > 0)
                {
                    var t = typesToCheck.Dequeue();

                    (t.Namespace ?? string.Empty)
                        .Should()
                        .NotStartWith(
                            "Microsoft.CodeAnalysis",
                            because: $"field '{field.Name}' on pipeline model '{modelType.Name}' " +
                                     $"has type '{t.FullName}' from Microsoft.CodeAnalysis — " +
                                     "Roslyn types in pipeline models break incremental caching " +
                                     "(step output can never be Cached across compilation passes)");

                    if (t.IsGenericType)
                    {
                        foreach (var arg in t.GetGenericArguments())
                        {
                            typesToCheck.Enqueue(arg);
                        }
                    }
                }
            }
        }
    }
}
