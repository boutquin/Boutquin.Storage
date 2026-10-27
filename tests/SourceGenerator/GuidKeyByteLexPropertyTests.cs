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
using Boutquin.Storage.Domain.Helpers;

namespace Boutquin.Storage.SourceGenerator.Tests;

/// <summary>
/// Phase A3 — byte-lex invariant for <see cref="System.Guid"/>-keyed records.
/// Without A3, the generated <c>CompareTo</c> dispatched to <see cref="System.Guid.CompareTo(System.Guid)"/>,
/// which reads the first three groups as little-endian fields — disagreeing with the
/// big-endian ordered-wire encoding on most random GUIDs. A3 routes the generator through
/// <see cref="OrderedByteHelpers.CompareGuidByteLex"/> so the in-memory comparator tracks
/// the on-disk ordering, preserving SSTable binary search correctness.
/// </summary>
public sealed class GuidKeyByteLexPropertyTests
{
    private static Assembly CompileAndLoad(string source)
    {
        var asmName = "GuidKeyAsm_" + Guid.NewGuid().ToString("N");
        var compilation = TestHelpers.CreateCompilation(source, assemblyName: asmName);
        var generator = new StorageSourceGenerator();
        _ = CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        errors.Should().BeEmpty(because: "generated code must compile; got:\n" +
            string.Join("\n", errors.Select(d => d.ToString())));

        using var ms = new MemoryStream();
        var result = outputCompilation.Emit(ms);
        result.Success.Should().BeTrue();
        ms.Position = 0;
        return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    [Fact]
    public void GuidKey_ByteLex_Matches_Generated_CompareTo_For_10k_Random_Guids()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                public partial record struct GK([property: Tag(1)] System.Guid V);

                public static class GKFactory
                {
                    public static GK Make(System.Guid v) => new GK(v);
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var gkType = asm.GetType("TestNamespace.GK")!;
        var factory = asm.GetType("TestNamespace.GKFactory")!;
        var make = factory.GetMethod("Make")!;
        var compareTo = gkType.GetMethod("CompareTo", [gkType])!;

        // 10k random Guid pairs (10k-pair property-test target). Seeded for determinism.
        var rng = new Random(424242);
        var buf = new byte[16];

        for (var i = 0; i < 10_000; i++)
        {
            rng.NextBytes(buf);
            var a = new Guid(buf);
            rng.NextBytes(buf);
            var b = new Guid(buf);

            var recA = make.Invoke(null, [a])!;
            var recB = make.Invoke(null, [b])!;

            var generatedCmp = (int)compareTo.Invoke(recA, [recB])!;
            var lexCmp = ByteLexCompareGuids(a, b);

            Math.Sign(generatedCmp).Should().Be(
                Math.Sign(lexCmp),
                $"generated CompareTo on GK({{a}}, {{b}}) must agree with byte-lex of the ordered-wire encoding; got generated={generatedCmp}, lex={lexCmp}");
        }
    }

    private static int ByteLexCompareGuids(Guid a, Guid b)
    {
        Span<byte> ba = stackalloc byte[16];
        Span<byte> bb = stackalloc byte[16];
        a.TryWriteBytes(ba, bigEndian: true, out _);
        b.TryWriteBytes(bb, bigEndian: true, out _);
        return ba.SequenceCompareTo(bb);
    }
}
