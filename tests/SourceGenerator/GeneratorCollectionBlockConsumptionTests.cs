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

namespace Boutquin.Storage.SourceGenerator.Tests;

/// <summary>
/// A count-prefixed collection block is decoded whole: the writer backpatches the block's
/// element count and its LEN over the same elements, so a count that leaves encoded bytes
/// unread is corruption, and the emitted reader must reject it rather than silently drop the
/// trailing elements.
/// </summary>
public sealed class GeneratorCollectionBlockConsumptionTests
{
    private const string Source = """
        using Boutquin.Storage.Domain.Attributes;
        using System;
        using System.Collections.Generic;
        namespace C
        {
            [StorageSerializable] public partial record struct Inner([property: Tag(1)] int X);
            [StorageSerializable] public partial record struct OuterA([property: Tag(1)] IEnumerable<Inner> Xs, [property: Tag(2)] int After);
            [StorageSerializable] public partial record struct OuterS([property: Tag(1)] IEnumerable<string> Xs, [property: Tag(2)] int After);
            [StorageSerializable] public partial record struct OuterG([property: Tag(1)] IEnumerable<Guid> Xs, [property: Tag(2)] int After);

            public static class Make
            {
                public static OuterA A(int n) { var xs = new Inner[n]; for (var i = 0; i < n; i++) { xs[i] = new Inner(i + 1); } return new OuterA(xs, 9); }
                public static OuterS S(int n) { var xs = new string[n]; for (var i = 0; i < n; i++) { xs[i] = ((char)('a' + i)).ToString(); } return new OuterS(xs, 9); }
                public static OuterG G(int n) { var xs = new Guid[n]; for (var i = 0; i < n; i++) { xs[i] = new Guid(i + 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11); } return new OuterG(xs, 9); }
            }
        }
        """;

    // Record length (1 byte), tag-0 header + version (2), the collection's tag header (1) and its
    // 10-byte padded LEN slot put the first byte of the padded count slot at offset 14.
    private const int CountSlotOffset = 14;

    private static readonly Lazy<Assembly> s_assembly = new(CompileAndLoad);

    private static Assembly CompileAndLoad()
    {
        _ = typeof(Boutquin.Storage.Domain.Attributes.StorageSerializableAttribute).FullName;
        _ = typeof(Boutquin.Storage.Domain.Helpers.VarintHelpers).FullName;
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));
        var input = CSharpCompilation.Create(
            "CollectionBlockConsumption_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(Source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        CSharpGeneratorDriver
            .Create(new StorageSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(input, out var output, out _);
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty("the generated code must compile");
        using var ms = new MemoryStream();
        output.Emit(ms).Success.Should().BeTrue();
        ms.Position = 0;
        return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    private static object Make(string factory, int count) =>
        s_assembly.Value.GetType("C.Make")!.GetMethod(factory)!.Invoke(null, [count])!;

    private static byte[] Serialize(object value)
    {
        using var ms = new MemoryStream();
        value.GetType().GetMethod("Serialize", [typeof(Stream)])!.Invoke(value, [ms]);
        return ms.ToArray();
    }

    private static object Deserialize(Type type, byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        try
        {
            return type.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [ms])!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    private static string View(object holder)
    {
        var elements = ((System.Collections.IEnumerable)holder.GetType().GetProperty("Xs")!.GetValue(holder)!)
            .Cast<object>()
            .Select(e => e.ToString());
        return $"[{string.Join(", ", elements)}] After={holder.GetType().GetProperty("After")!.GetValue(holder)}";
    }

    // "A": annotated-collection reader (nested [StorageSerializable] elements).
    // "S", "G": count-prefixed reader (string and Guid elements).
    [Theory]
    [InlineData("A", "C.OuterA")]
    [InlineData("S", "C.OuterS")]
    [InlineData("G", "C.OuterG")]
    public void CollectionBlock_CountForgedBelowTheEncodedElements_IsRejectedAsCorrupt(string factory, string typeName)
    {
        var bytes = Serialize(Make(factory, 2));
        bytes[CountSlotOffset].Should().Be(0x82, "the padded count slot of a two-element block starts with 0x82");
        bytes[CountSlotOffset] = 0x81;

        var act = () => Deserialize(s_assembly.Value.GetType(typeName)!, bytes);

        act.Should().Throw<InvalidDataException>(
            "a block whose count leaves encoded elements unread is corrupt — silently dropping the tail loses data");
    }

    // The consumption check must not refuse a well-formed block, including the empty one whose
    // count is the block's only content.
    [Theory]
    [InlineData("A", "C.OuterA", 0)]
    [InlineData("A", "C.OuterA", 3)]
    [InlineData("S", "C.OuterS", 0)]
    [InlineData("S", "C.OuterS", 3)]
    [InlineData("G", "C.OuterG", 0)]
    [InlineData("G", "C.OuterG", 3)]
    public void CollectionBlock_WellFormed_RoundTripsEveryElement(string factory, string typeName, int count)
    {
        var value = Make(factory, count);

        var back = Deserialize(s_assembly.Value.GetType(typeName)!, Serialize(value));

        View(back).Should().Be(View(value));
    }
}
