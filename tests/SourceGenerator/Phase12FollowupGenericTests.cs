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
/// Phase-12 follow-up — exercises <c>StorageSourceGenerator.BuildConstraints</c> at
/// L798–845. The method runs over every type-parameter symbol on a containing type and
/// emits the matching <c>where T : ...</c> clause; the constraint-detection branches
/// (<c>HasReferenceTypeConstraint</c>, <c>HasValueTypeConstraint</c>,
/// <c>HasUnmanagedTypeConstraint</c>, <c>HasNotNullConstraint</c>,
/// <c>HasConstructorConstraint</c>, the <c>ConstraintTypes</c> loop) are
/// unreachable from a non-generic outer type. Each test below nests a
/// <c>[Key]</c> or <c>[StorageSerializable]</c> record inside a generic outer with one
/// or more constraints, then asserts the generator produces compilable output and emits
/// the expected <c>where</c> clause.
/// </summary>
public sealed class Phase12FollowupGenericTests
{
    /// <summary>
    /// Generic outer with <c>where T : class, IComparable&lt;T&gt;</c> exercises both
    /// <c>HasReferenceTypeConstraint</c> (L809 → "class") and the <c>ConstraintTypes</c>
    /// foreach (L829 → "System.IComparable&lt;T&gt;"). The generator emits the partial
    /// declaration with the original constraint chain so the synthesized type still
    /// compiles inside its containing generic.
    /// </summary>
    [Fact]
    public void Generic_KeyRecordNestedInClassConstrainedOuter_GeneratesCompilableSource()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace GenericClassConstraint
            {
                public partial class Outer<T> where T : class, System.IComparable<T>
                {
                    [Key]
                    public partial record struct InnerKey([property: Tag(1)] long Value);
                }
            }
            """;
        var (output, _) = TestHelpers.RunGenerator(source);
        AssertNoErrors(output);

        var emitted = GetEmittedSource(output, "InnerKey");
        emitted.Should().Contain("class Outer<T>",
            "the generated source must wrap InnerKey in its containing generic Outer<T>");
        emitted.Should().Contain("where T : class",
            "BuildConstraints must surface the 'class' reference-type constraint (L809)");
        emitted.Should().Contain("System.IComparable",
            "BuildConstraints must surface IComparable<T> via the ConstraintTypes loop (L829)");
    }

    /// <summary>
    /// Generic outer with <c>where T : struct, new()</c> exercises
    /// <c>HasValueTypeConstraint</c> (L814 → "struct") and
    /// <c>HasConstructorConstraint</c> (L834 → "new()"). The two constraints are
    /// emitted in the order BuildConstraints walks the symbol's flags.
    /// </summary>
    [Fact]
    public void Generic_StorageSerializableRecordNestedInStructConstrainedOuter_GeneratesCompilableSource()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace GenericStructConstraint
            {
                public partial class Outer<T> where T : struct
                {
                    [StorageSerializable]
                    public partial record struct Inner([property: Tag(1)] long Value);
                }
            }
            """;
        var (output, _) = TestHelpers.RunGenerator(source);
        AssertNoErrors(output);

        var emitted = GetEmittedSource(output, "Inner");
        emitted.Should().Contain("class Outer<T>");
        emitted.Should().Contain("where T : struct",
            "BuildConstraints must surface the 'struct' value-type constraint (L814)");
    }

    /// <summary>
    /// Generic outer with <c>where T : unmanaged</c> exercises
    /// <c>HasUnmanagedTypeConstraint</c> (L819 → "unmanaged"). The unmanaged constraint
    /// implies <c>struct</c> in C# 7.3+ but Roslyn surfaces it through a distinct flag
    /// — BuildConstraints must emit the textual form "unmanaged" rather than the
    /// implied "struct".
    /// </summary>
    [Fact]
    public void Generic_KeyRecordNestedInUnmanagedConstrainedOuter_GeneratesCompilableSource()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace GenericUnmanagedConstraint
            {
                public partial class Outer<T> where T : unmanaged
                {
                    [Key]
                    public partial record struct InnerKey([property: Tag(1)] long Value);
                }
            }
            """;
        var (output, _) = TestHelpers.RunGenerator(source);
        AssertNoErrors(output);

        var emitted = GetEmittedSource(output, "InnerKey");
        emitted.Should().Contain("class Outer<T>");
        emitted.Should().Contain("unmanaged",
            "BuildConstraints must surface the 'unmanaged' constraint (L819)");
    }

    /// <summary>
    /// Generic outer with <c>where T : notnull</c> exercises
    /// <c>HasNotNullConstraint</c> (L824 → "notnull"). Distinct from the
    /// reference-type constraint at L809 — Roslyn raises both flags only when both
    /// constraints are present; here only HasNotNullConstraint is true.
    /// </summary>
    [Fact]
    public void Generic_StorageSerializableRecordNestedInNotnullConstrainedOuter_GeneratesCompilableSource()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace GenericNotnullConstraint
            {
                public partial class Outer<T> where T : notnull
                {
                    [StorageSerializable]
                    public partial record struct Inner([property: Tag(1)] long Value);
                }
            }
            """;
        var (output, _) = TestHelpers.RunGenerator(source);
        AssertNoErrors(output);

        var emitted = GetEmittedSource(output, "Inner");
        emitted.Should().Contain("class Outer<T>");
        emitted.Should().Contain("notnull",
            "BuildConstraints must surface the 'notnull' constraint (L824)");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void AssertNoErrors(Microsoft.CodeAnalysis.CSharp.CSharpCompilation compilation)
    {
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToList();
        errors.Should().BeEmpty(
            "the generator's output must compile inside its containing generic outer; got:\n"
                + string.Join("\n", errors.Select(d => d.ToString())));
    }

    private static string GetEmittedSource(Microsoft.CodeAnalysis.CSharp.CSharpCompilation output, string innerName)
    {
        // Find the syntax tree whose path contains the inner type name (the generator
        // emits one .g.cs file per annotated type with the type name embedded in the path).
        var tree = output.SyntaxTrees.FirstOrDefault(t =>
            t.FilePath.Contains(innerName, StringComparison.Ordinal));
        tree.Should().NotBeNull(
            $"the generator must emit a .g.cs source for '{innerName}'");
        return tree!.GetText().ToString();
    }
}
