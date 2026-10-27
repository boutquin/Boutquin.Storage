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
using System.Runtime.Loader;

namespace Boutquin.Storage.SourceGenerator.Tests;

/// <summary>
/// Contract: a non-nullable nested-record field whose tag is absent reads back as the record's
/// <c>default</c> (null for a record class, the zero-initialised value for a record struct), and
/// that value writes back as an absent tag, so bytes written under an older schema survive a
/// read-then-write migration byte-identically. Before this contract the writer serialised the
/// default child unguarded, throwing for values the generated reader itself produces.
/// </summary>
public sealed class NestedChildAbsentTagContractTests
{
    private const string Records = """
        #nullable enable
        using System.Collections.Generic;
        using Boutquin.Storage.Domain.Attributes;
        namespace P;
        [StorageSerializable]
        public partial record class ChildC([property: Tag(1)] int X);
        [StorageSerializable]
        public partial record struct ChildS([property: Tag(1)] string Name, [property: Tag(2)] IEnumerable<int> Values);
        [StorageSerializable]
        public partial record struct V1([property: Tag(1)] int Id);
        [StorageSerializable]
        public partial record struct V2C([property: Tag(1)] int Id, [property: Tag(2)] ChildC C);
        [StorageSerializable]
        public partial record struct V2S([property: Tag(1)] int Id, [property: Tag(2)] ChildS S);
        [StorageSerializable]
        public partial record struct ExactChild([property: Tag(1)] double D, [property: Tag(2)] float F,
            [property: Tag(3)] System.Half H, [property: Tag(4)] decimal M, [property: Tag(5)] System.DateTimeOffset T);
        [StorageSerializable]
        public partial record struct ExactOuter([property: Tag(1)] ExactChild Child);
        [StorageSerializable]
        public partial record struct CustomChild([property: Tag(1)] int Value) {
            public bool Equals(CustomChild other) => true;
            public override int GetHashCode() => 0;
        }
        [StorageSerializable]
        public partial record struct CustomOuter([property: Tag(1)] CustomChild Child);
        """;

    private static Assembly Load()
    {
        _ = typeof(Boutquin.Storage.Domain.Attributes.TagAttribute).FullName;
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create("AbsentChild_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(Records, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        _ = CSharpGeneratorDriver.Create(new StorageSourceGenerator().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        using var ms = new MemoryStream();
        output.Emit(ms).Success.Should().BeTrue();
        ms.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    private static byte[] Ser(object r)
    {
        using var s = new MemoryStream();
        r.GetType().GetMethod("Serialize", [typeof(Stream)])!.Invoke(r, [s]);
        return s.ToArray();
    }

    private static object De(Type t, byte[] b)
    {
        using var s = new MemoryStream(b);
        return t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static, [typeof(Stream)])!.Invoke(null, [s])!;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ValueEqualToDefault_ChildPreservesRepresentation(int field)
    {
        var asm = Load();
        var childType = asm.GetType("P.ExactChild")!;
        var timestamp = new DateTimeOffset(1, 1, 1, 5, 0, 0, TimeSpan.FromHours(5));
        object[] values = [0d, 0f, (Half)0, 0m, default(DateTimeOffset)];
        object[] special = [-0.0d, -0.0f, BitConverter.Int16BitsToHalf(unchecked((short)0x8000)), 0.00m, timestamp];
        values[field] = special[field];
        var child = Activator.CreateInstance(childType, values)!;
        var outerType = asm.GetType("P.ExactOuter")!;
        var outer = Activator.CreateInstance(outerType, child)!;

        var back = outerType.GetProperty("Child")!.GetValue(De(outerType, Ser(outer)))!;

        Ser(back).Should().Equal(Ser(child), "nested values must retain the same representation as direct serialization");
        BitConverter.DoubleToInt64Bits((double)childType.GetProperty("D")!.GetValue(back)!).Should().Be(BitConverter.DoubleToInt64Bits((double)values[0]));
        BitConverter.SingleToInt32Bits((float)childType.GetProperty("F")!.GetValue(back)!).Should().Be(BitConverter.SingleToInt32Bits((float)values[1]));
        BitConverter.HalfToInt16Bits((Half)childType.GetProperty("H")!.GetValue(back)!).Should().Be(BitConverter.HalfToInt16Bits((Half)values[2]));
        decimal.GetBits((decimal)childType.GetProperty("M")!.GetValue(back)!).Should().Equal(decimal.GetBits((decimal)values[3]));
        ((DateTimeOffset)childType.GetProperty("T")!.GetValue(back)!).EqualsExact((DateTimeOffset)values[4]).Should().BeTrue();
    }

    [Fact]
    public void CustomEquality_DoesNotDiscardStoredChildState()
    {
        var asm = Load();
        var child = Activator.CreateInstance(asm.GetType("P.CustomChild")!, 17)!;
        var outerType = asm.GetType("P.CustomOuter")!;
        var outer = Activator.CreateInstance(outerType, child)!;

        var back = outerType.GetProperty("Child")!.GetValue(De(outerType, Ser(outer)))!;

        back.GetType().GetProperty("Value")!.GetValue(back).Should().Be(17);
    }

    [Theory]
    [InlineData("P.V2C")]
    [InlineData("P.V2S")]
    public void AbsentNonNullableChild_ReadsAsDefault_AndWritesBackByteIdentical(string typeName)
    {
        var asm = Load();
        var oldBytes = Ser(Activator.CreateInstance(asm.GetType("P.V1")!, 9)!);

        var migrated = De(asm.GetType(typeName)!, oldBytes);

        Func<byte[]> writeBack = () => Ser(migrated);
        writeBack.Should().NotThrow("the value the generated reader produces for a missing tag must be writable")
            .Which.Should().Equal(oldBytes, "a default child writes an absent tag, the value the reader maps back to default");
    }

    [Theory]
    [InlineData("P.V2C", "P.ChildC", new object[] { 5 })]
    public void PresentNonNullableChild_StillRoundTrips(string typeName, string childName, object[] childArgs)
    {
        var asm = Load();
        var child = Activator.CreateInstance(asm.GetType(childName)!, childArgs)!;
        var value = Activator.CreateInstance(asm.GetType(typeName)!, 9, child)!;

        var back = De(asm.GetType(typeName)!, Ser(value));

        back.Should().Be(value);
    }

    [Fact]
    public void PresentNonNullableStructChild_WithEmptyMembers_StillRoundTrips()
    {
        var asm = Load();
        var child = Activator.CreateInstance(asm.GetType("P.ChildS")!, "", new List<int>())!;
        var value = Activator.CreateInstance(asm.GetType("P.V2S")!, 9, child)!;

        var bytes = Ser(value);
        var back = De(asm.GetType("P.V2S")!, bytes);

        Ser(back).Should().Equal(bytes);
        bytes.Length.Should().BeGreaterThan(Ser(Activator.CreateInstance(asm.GetType("P.V1")!, 9)!).Length,
            "a non-default child is written, not elided");
    }
}
