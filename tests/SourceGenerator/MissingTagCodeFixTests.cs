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
using Boutquin.Storage.SourceGenerator.CodeFixes;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;

namespace Boutquin.Storage.SourceGenerator.Tests;

// Tests for the BSSG010 code-fix provider. Drives the fix directly (not via the full
// CodeFixVerifier harness) because the BSSG010 diagnostic comes from a source generator
// rather than a DiagnosticAnalyzer; the Roslyn verifier harness is wired for analyzer
// diagnostics and would require a second DiagnosticAnalyzer wrapper. Invoking the code
// fix against a synthesized Diagnostic that mirrors the generator's message is a smaller,
// more direct surface to test.
public sealed class MissingTagCodeFixTests
{
    [Fact]
    public async Task SingleMissingTag_InsertsTagOne()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            public partial record struct Foo(string Name);
            """;

        var expected = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            public partial record struct Foo([property: Tag(1)] string Name);
            """;

        var actual = await ApplyFixForPropertyAsync(source, propertyName: "Name", typeName: "Foo", suggested: 1);
        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    [Fact]
    public async Task MissingTagBetweenDeclaredTags_UsesLowestUnused()
    {
        // Two of three parameters already declare [Tag(...)]. The middle one is missing.
        // Lowest unused tag on the record is 1 (neither sibling uses it).
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            public partial record struct Foo(
                [property: Tag(2)] string A,
                int B,
                [property: Tag(3)] string C);
            """;

        var expected = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            public partial record struct Foo(
                [property: Tag(2)] string A,
                [property: Tag(1)] int B,
                [property: Tag(3)] string C);
            """;

        var actual = await ApplyFixForPropertyAsync(source, propertyName: "B", typeName: "Foo", suggested: 1);
        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    [Fact]
    public async Task MissingTagWithGapInDeclaredTags_FillsGap()
    {
        // Siblings declare tags 1 and 3. The missing tag on B should fill the gap at 2,
        // matching the generator's "lowest unused positive" policy even though the
        // message-suggested number carried in the diagnostic was a specific integer.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            public partial record struct Foo(
                [property: Tag(1)] string A,
                int B,
                [property: Tag(3)] string C);
            """;

        var expected = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            public partial record struct Foo(
                [property: Tag(1)] string A,
                [property: Tag(2)] int B,
                [property: Tag(3)] string C);
            """;

        var actual = await ApplyFixForPropertyAsync(source, propertyName: "B", typeName: "Foo", suggested: 2);
        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    [Fact]
    public async Task MissingTagRespectsReservedTag()
    {
        // Reserved tags skip over in the suggestion. Siblings declare 1. Reserved = 2.
        // Lowest unused non-reserved tag is 3.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            [ReservedTag(2)]
            public partial record struct Foo(
                [property: Tag(1)] string A,
                int B);
            """;

        var expected = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            [ReservedTag(2)]
            public partial record struct Foo(
                [property: Tag(1)] string A,
                [property: Tag(3)] int B);
            """;

        var actual = await ApplyFixForPropertyAsync(source, propertyName: "B", typeName: "Foo", suggested: 3);
        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    [Fact]
    public async Task BodyPropertyRecord_MissingTag_RegistersFixAction()
    {
        // A body-property record (property declared in the body, not as a positional parameter)
        // also raises BSSG010. The code-fix previously bailed out (record.ParameterList is null)
        // and registered ZERO actions — a silent no-op. The fix must offer a [Tag(N)] insertion
        // on the property declaration just as it does for positional parameters.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [StorageSerializable]
            public partial record Bar
            {
                public long Volume { get; set; }
            }
            """;

        var actions = await RegisterFixActionsForPropertyAsync(source, propertyName: "Volume", typeName: "Bar", suggested: 1);

        Assert.NotEmpty(actions);
    }

    [Fact]
    public async Task BodyPropertyRecord_MissingTag_InsertsTagAttribute()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [StorageSerializable]
            public partial record Bar
            {
                public long Volume { get; set; }
            }
            """;

        var actual = await ApplyFixForPropertyAsync(source, propertyName: "Volume", typeName: "Bar", suggested: 1);

        // The inserted attribute must be a property-target [Tag(1)] on the property and the
        // result must still parse cleanly.
        Assert.Contains("[Tag(1)]", Normalize(actual));
        Assert.Contains("public long Volume", Normalize(actual));
        var parsed = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(actual);
        Assert.DoesNotContain(
            parsed.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);
    }

    // Registers all code-fix actions for a synthesized BSSG010 diagnostic and returns them.
    // Mirrors ApplyFixForPropertyAsync but collects every registered action rather than the
    // single one, so a zero-action (silent no-op) regression is observable.
    private static async Task<List<CodeAction>> RegisterFixActionsForPropertyAsync(
        string source,
        string propertyName,
        string typeName,
        int suggested)
    {
        var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", LanguageNames.CSharp)
            .AddDocument(documentId, "Test.cs", SourceText.From(source));

        var document = solution.GetDocument(documentId)!;
        var root = await document.GetSyntaxRootAsync().ConfigureAwait(true);
        var recordDecl = root!.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.RecordDeclarationSyntax>()
            .First(r => r.Identifier.Text == typeName);

        var descriptor = new DiagnosticDescriptor(
            "BSSG010",
            "Missing [Tag] on annotated property",
            $"Property '{propertyName}' on type '{typeName}' must declare a [Tag(N)]. Suggested: [property: Tag({suggested})].",
            "Boutquin.Storage.SourceGenerator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        var diagnostic = Diagnostic.Create(descriptor, Location.Create(root.SyntaxTree, recordDecl.Identifier.Span));

        var codeFix = new MissingTagCodeFix();
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            CancellationToken.None);

        await codeFix.RegisterCodeFixesAsync(context).ConfigureAwait(true);
        return actions;
    }

    // Drives the code fix against a synthesized Diagnostic that matches the BSSG010 shape.
    // The `suggested` parameter is the generator's originally suggested tag number — the
    // code fix recomputes from syntax, so this is the number the diagnostic was emitted with
    // (which the assertion expects the recomputed fix to agree with).
    private static async Task<string> ApplyFixForPropertyAsync(
        string source,
        string propertyName,
        string typeName,
        int suggested)
    {
        var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Test", "Test", LanguageNames.CSharp)
            .AddDocument(documentId, "Test.cs", SourceText.From(source));

        var document = solution.GetDocument(documentId)!;
        var root = await document.GetSyntaxRootAsync().ConfigureAwait(true);
        var recordDecl = root!.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.RecordDeclarationSyntax>()
            .First(r => r.Identifier.Text == typeName);

        // Synthesize a Diagnostic that mirrors the generator's BSSG010 emission. The code fix
        // parses the message to recover the property name; the suggested-tag portion is taken
        // from the parameter but re-derived from syntax in the fix so the number here is a
        // historical witness only.
        var descriptor = new DiagnosticDescriptor(
            "BSSG010",
            "Missing [Tag] on annotated property",
            $"Property '{propertyName}' on type '{typeName}' must declare a [Tag(N)]. Suggested: [property: Tag({suggested})].",
            "Boutquin.Storage.SourceGenerator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        var diagnostic = Diagnostic.Create(descriptor, Location.Create(root.SyntaxTree, recordDecl.Identifier.Span));

        var codeFix = new MissingTagCodeFix();
        CodeAction? registeredAction = null;
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => registeredAction = action,
            CancellationToken.None);

        await codeFix.RegisterCodeFixesAsync(context).ConfigureAwait(true);

        Assert.NotNull(registeredAction);
        var operations = await registeredAction!.GetOperationsAsync(CancellationToken.None).ConfigureAwait(true);
        var apply = (Microsoft.CodeAnalysis.CodeActions.ApplyChangesOperation)operations[0];
        var changedDocument = apply.ChangedSolution.GetDocument(documentId)!;
        var changedText = await changedDocument.GetTextAsync().ConfigureAwait(true);
        return changedText.ToString();
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").TrimEnd('\n');
}
