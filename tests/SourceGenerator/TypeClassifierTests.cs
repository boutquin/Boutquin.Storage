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

// Classifier-level tests. Exercise TypeClassifier.Classify(ITypeSymbol) directly, independent
// of the StorageSourceGenerator / ColumnarSourceGenerator pipelines. The classifier is the
// single source of truth for supported type classification; these tests lock that
// table so adding a new type propagates through both emitters in lock step.
//
// EncodableTypeKind is internal to the generator assembly; test methods therefore exchange
// the kind as its enum member name string and resolve via Enum.Parse to avoid exposing the
// internal type on a public test method signature.
public sealed class TypeClassifierTests
{
    // Tier 1 numeric primitives
    [Theory]
    [InlineData("sbyte", "SByte")]
    [InlineData("byte", "Byte")]
    [InlineData("short", "Int16")]
    [InlineData("ushort", "UInt16")]
    [InlineData("int", "Int32")]
    [InlineData("uint", "UInt32")]
    [InlineData("long", "Int64")]
    [InlineData("ulong", "UInt64")]
    [InlineData("System.Int128", "Int128")]
    [InlineData("System.UInt128", "UInt128")]
    [InlineData("System.Half", "Half")]
    [InlineData("float", "Single")]
    [InlineData("double", "Double")]
    public void Tier1_Numeric_Primitives_Classify_By_Kind(string typeSyntax, string expectedKindName)
    {
        var symbol = GetPropertyType($"public {typeSyntax} P {{ get; }}");
        var descriptor = TypeClassifier.Classify(symbol);
        descriptor.Kind.Should().Be(ParseKind(expectedKindName));
    }

    // Tier 2 non-numeric primitives
    [Theory]
    [InlineData("bool", "Bool")]
    [InlineData("char", "Char")]
    [InlineData("string", "String")]
    public void Tier2_NonNumeric_Primitives_Classify_By_Kind(string typeSyntax, string expectedKindName)
    {
        var symbol = GetPropertyType($"public {typeSyntax} P {{ get; }}");
        var descriptor = TypeClassifier.Classify(symbol);
        descriptor.Kind.Should().Be(ParseKind(expectedKindName));
    }

    // Tier 3 structured BCL primitives
    [Theory]
    [InlineData("System.DateOnly", "DateOnly")]
    [InlineData("System.TimeOnly", "TimeOnly")]
    [InlineData("System.TimeSpan", "TimeSpan")]
    [InlineData("System.DateTimeOffset", "DateTimeOffset")]
    [InlineData("System.Guid", "Guid")]
    [InlineData("decimal", "Decimal")]
    public void Tier3_BCL_Primitives_Classify_By_Name(string typeSyntax, string expectedKindName)
    {
        var symbol = GetPropertyType($"public {typeSyntax} P {{ get; }}");
        var descriptor = TypeClassifier.Classify(symbol);
        descriptor.Kind.Should().Be(ParseKind(expectedKindName));
    }

    // Tier 4 enums — dispatched to underlying numeric kind.
    [Theory]
    [InlineData("byte", "Byte")]
    [InlineData("int", "Int32")]
    [InlineData("long", "Int64")]
    [InlineData("short", "Int16")]
    [InlineData("uint", "UInt32")]
    public void Tier4_Enum_Carries_Underlying_Kind(string underlying, string expectedUnderlyingKindName)
    {
        var source = $$"""
            public enum MyEnum : {{underlying}} { A, B }
            public class Holder { public MyEnum P { get; } }
            """;
        var symbol = GetPropertyType(source, holderTypeName: "Holder");
        var descriptor = TypeClassifier.Classify(symbol);
        descriptor.Kind.Should().Be(EncodableTypeKind.Enum);
        descriptor.EnumUnderlying.Should().Be(ParseKind(expectedUnderlyingKindName));
    }

    // Tier 6 nullable value types wrap a Tier 1/2/3/4 inner.
    [Theory]
    [InlineData("int?", "Int32")]
    [InlineData("long?", "Int64")]
    [InlineData("double?", "Double")]
    [InlineData("bool?", "Bool")]
    [InlineData("System.DateOnly?", "DateOnly")]
    [InlineData("System.Guid?", "Guid")]
    [InlineData("decimal?", "Decimal")]
    public void Tier6_Nullable_Value_Type_Carries_Inner_Kind(string typeSyntax, string expectedInnerKindName)
    {
        var symbol = GetPropertyType($"public {typeSyntax} P {{ get; }}");
        var descriptor = TypeClassifier.Classify(symbol);
        descriptor.Kind.Should().Be(EncodableTypeKind.Nullable);
        descriptor.NullableInner.Should().Be(ParseKind(expectedInnerKindName));
    }

    // Excluded types produce Unsupported — explicitly excluded, not unknown.
    [Theory]
    [InlineData("System.DateTime")]
    [InlineData("nint")]
    [InlineData("nuint")]
    public void Excluded_Types_Classify_As_Unsupported(string typeSyntax)
    {
        var symbol = GetPropertyType($"public {typeSyntax} P {{ get; }}");
        var descriptor = TypeClassifier.Classify(symbol);
        descriptor.Kind.Should().Be(EncodableTypeKind.Unsupported);
    }

    // Reference types other than string are Unsupported.
    [Fact]
    public void Object_Reference_Type_Classifies_As_Unsupported()
    {
        var symbol = GetPropertyType("public object P { get; }");
        var descriptor = TypeClassifier.Classify(symbol);
        descriptor.Kind.Should().Be(EncodableTypeKind.Unsupported);
    }

    // FullyQualifiedTypeName must be the "global::"-stripped FQN so emitters can use it
    // without further canonicalization.
    [Fact]
    public void Descriptor_Carries_Stable_FullyQualifiedTypeName_For_Primitive()
    {
        var symbol = GetPropertyType("public int P { get; }");
        var descriptor = TypeClassifier.Classify(symbol);
        // Roslyn's FullyQualifiedFormat preserves keyword aliases for built-in types
        // ("int" rather than "System.Int32"); the classifier strips "global::" and trusts
        // Roslyn's canonicalization. Kind remains authoritative — the FQN is advisory, used
        // by emitters for string-match fallback on types without a SpecialType.
        descriptor.FullyQualifiedTypeName.Should().Be("int");
    }

    [Fact]
    public void Descriptor_Carries_Stable_FullyQualifiedTypeName_For_Tier3()
    {
        var symbol = GetPropertyType("public System.DateOnly P { get; }");
        var descriptor = TypeClassifier.Classify(symbol);
        descriptor.FullyQualifiedTypeName.Should().Be("System.DateOnly");
    }

    // Descriptor is value-equatable on every field — required for incremental pipeline caching.
    [Fact]
    public void Two_Descriptors_For_Same_Type_Are_Equal()
    {
        var a = TypeClassifier.Classify(GetPropertyType("public int P { get; }"));
        var b = TypeClassifier.Classify(GetPropertyType("public int P { get; }"));
        a.Should().Be(b);
    }

    private static EncodableTypeKind ParseKind(string name) =>
        Enum.Parse<EncodableTypeKind>(name);

    // Helper — extract the ITypeSymbol of property P on a test type "T".
    private static ITypeSymbol GetPropertyType(string memberDecl, string holderTypeName = "T")
    {
        // If memberDecl already includes a type declaration, use as-is; otherwise wrap in "class T { ... }".
        var source = memberDecl.Contains("class ") || memberDecl.Contains("struct ") || memberDecl.Contains("enum ")
            ? memberDecl
            : $"public class {holderTypeName} {{ {memberDecl} }}";

        var compilation = CSharpCompilation.Create(
            "TypeClassifierTestAsm",
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var holder = compilation.GetTypeByMetadataName(holderTypeName);
        holder.Should().NotBeNull($"test helper expected to find type '{holderTypeName}' in the compilation");
        var property = holder!.GetMembers().OfType<IPropertySymbol>().FirstOrDefault(p => p.Name == "P");
        property.Should().NotBeNull("test helper expected property 'P' on the holder type");
        return property!.Type;
    }
}
