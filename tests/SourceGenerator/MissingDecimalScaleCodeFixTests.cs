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

// Tests for the BSSG017 code-fix provider. Mirrors MissingTagCodeFixTests' driver pattern:
// the BSSG017 diagnostic comes from the source generator (not a DiagnosticAnalyzer), so the
// tests synthesize a Diagnostic that matches the generator's emission shape and invoke the
// fix provider directly.
public sealed class MissingDecimalScaleCodeFixTests
{
    [Fact]
    public async Task SingleMissingDecimalScale_InsertsDefaultScaleTwo()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            public partial record struct Price([property: Tag(1)] decimal Amount);
            """;

        var expected = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            public partial record struct Price([property: DecimalScale(2)][property: Tag(1)] decimal Amount);
            """;

        var actual = await ApplyFixForPropertyAsync(source, propertyName: "Amount", typeName: "Price");
        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    [Fact]
    public async Task NullableDecimalProperty_GetsDecimalScaleAttribute()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            public partial record struct Quote([property: Tag(1)] decimal? Bid);
            """;

        var expected = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            public partial record struct Quote([property: DecimalScale(2)][property: Tag(1)] decimal? Bid);
            """;

        var actual = await ApplyFixForPropertyAsync(source, propertyName: "Bid", typeName: "Quote");
        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    [Fact]
    public async Task PreservesOtherPropertiesUnchanged()
    {
        // Single-line source to keep the test focused on "fix only touches the named
        // parameter". Multi-line indent transfer is a separate concern of the fix's
        // trivia handling (covered by MissingTagCodeFixTests in greater depth).
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            public partial record struct Trade([property: Tag(1)] string Symbol, [property: Tag(2)] decimal Price, [property: Tag(3)] long Volume);
            """;

        var actual = await ApplyFixForPropertyAsync(source, propertyName: "Price", typeName: "Trade");
        // Other parameters must remain untouched.
        Assert.Contains("[property: Tag(1)] string Symbol", actual);
        Assert.Contains("[property: Tag(3)] long Volume", actual);
        // The Price parameter must gain the DecimalScale attribute.
        Assert.Contains("[property: DecimalScale(2)]", actual);
        Assert.Contains("decimal Price", actual);
    }

    [Fact]
    public async Task FixableDiagnosticIds_ContainsBSSG017Only()
    {
        var fix = new MissingDecimalScaleCodeFix();
        Assert.Contains("BSSG017", fix.FixableDiagnosticIds);
        Assert.Single(fix.FixableDiagnosticIds);
    }

    [Fact]
    public async Task GetFixAllProvider_ReturnsNull()
    {
        // FixAllProvider is intentionally null — single-document fix only per the spec.
        var fix = new MissingDecimalScaleCodeFix();
        Assert.Null(fix.GetFixAllProvider());
    }

    [Fact]
    public async Task UnrelatedDiagnostic_DoesNotRegisterFix()
    {
        // A diagnostic with a non-BSSG017 ID must not trigger the fix.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            public partial record struct Price([property: Tag(1)] decimal Amount);
            """;

        var registered = await TryApplyFixWithDiagnosticIdAsync(
            source, propertyName: "Amount", typeName: "Price",
            diagnosticId: "BSSG999");
        Assert.Null(registered);
    }

    [Fact]
    public async Task MalformedDiagnosticMessage_DoesNotRegisterFix()
    {
        // Message that doesn't match the regex — the fix provider must skip.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            public partial record struct Price([property: Tag(1)] decimal Amount);
            """;

        var registered = await TryApplyFixWithCustomMessageAsync(
            source, typeName: "Price",
            customMessage: "Some unrelated message that doesn't mention a property");
        Assert.Null(registered);
    }

    [Fact]
    public async Task NoMatchingProperty_DoesNotRegisterFix()
    {
        // Diagnostic targets a property name that doesn't exist on the record.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;

            [Key]
            public partial record struct Price([property: Tag(1)] decimal Amount);
            """;

        var registered = await TryApplyFixForMissingPropertyAsync(
            source, missingPropertyName: "DoesNotExist", typeName: "Price");
        Assert.Null(registered);
    }

    // ── Driver helpers ───────────────────────────────────────────────────────

    private static async Task<string> ApplyFixForPropertyAsync(
        string source,
        string propertyName,
        string typeName)
    {
        var (document, action) = await BuildAndRegisterAsync(source, typeName,
            (root, recordDecl) => BuildBSSG017Diagnostic(root, recordDecl, propertyName, typeName));
        Assert.NotNull(action);
        var operations = await action!.GetOperationsAsync(CancellationToken.None).ConfigureAwait(true);
        var apply = (ApplyChangesOperation)operations[0];
        var changedDocument = apply.ChangedSolution.GetDocument(document.Id)!;
        var changedText = await changedDocument.GetTextAsync().ConfigureAwait(true);
        return changedText.ToString();
    }

    private static async Task<CodeAction?> TryApplyFixWithDiagnosticIdAsync(
        string source,
        string propertyName,
        string typeName,
        string diagnosticId)
    {
        DiagnosticDescriptor BuildDescriptor(string property, string type) =>
            new(diagnosticId,
                "Title",
                $"Property '{property}' on [Key] type '{type}' must declare [DecimalScale(N)]: scale is missing or out of range.",
                "Boutquin.Storage.SourceGenerator",
                DiagnosticSeverity.Error,
                isEnabledByDefault: true);

        var (_, action) = await BuildAndRegisterAsync(source, typeName,
            (root, recordDecl) => BuildDiagnostic(root, recordDecl, BuildDescriptor(propertyName, typeName)));
        return action;
    }

    private static async Task<CodeAction?> TryApplyFixWithCustomMessageAsync(
        string source,
        string typeName,
        string customMessage)
    {
        var descriptor = new DiagnosticDescriptor(
            "BSSG017",
            "Title",
            customMessage,
            "Boutquin.Storage.SourceGenerator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        var (_, action) = await BuildAndRegisterAsync(source, typeName,
            (root, recordDecl) => BuildDiagnostic(root, recordDecl, descriptor));
        return action;
    }

    private static async Task<CodeAction?> TryApplyFixForMissingPropertyAsync(
        string source,
        string missingPropertyName,
        string typeName)
    {
        var (_, action) = await BuildAndRegisterAsync(source, typeName,
            (root, recordDecl) => BuildBSSG017Diagnostic(root, recordDecl, missingPropertyName, typeName));
        return action;
    }

    private static async Task<(Document Document, CodeAction? Action)> BuildAndRegisterAsync(
        string source,
        string typeName,
        Func<SyntaxNode, Microsoft.CodeAnalysis.CSharp.Syntax.RecordDeclarationSyntax, Diagnostic> diagnosticBuilder)
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

        var diagnostic = diagnosticBuilder(root, recordDecl);

        var codeFix = new MissingDecimalScaleCodeFix();
        CodeAction? registeredAction = null;
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => registeredAction = action,
            CancellationToken.None);

        await codeFix.RegisterCodeFixesAsync(context).ConfigureAwait(true);
        return (document, registeredAction);
    }

    private static Diagnostic BuildBSSG017Diagnostic(
        SyntaxNode root,
        Microsoft.CodeAnalysis.CSharp.Syntax.RecordDeclarationSyntax recordDecl,
        string propertyName,
        string typeName)
    {
        var descriptor = new DiagnosticDescriptor(
            "BSSG017",
            "Missing [DecimalScale]",
            $"Property '{propertyName}' on [Key] type '{typeName}' must declare [DecimalScale(N)]: scale is missing or out of range.",
            "Boutquin.Storage.SourceGenerator",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
        return BuildDiagnostic(root, recordDecl, descriptor);
    }

    private static Diagnostic BuildDiagnostic(
        SyntaxNode root,
        Microsoft.CodeAnalysis.CSharp.Syntax.RecordDeclarationSyntax recordDecl,
        DiagnosticDescriptor descriptor)
        => Diagnostic.Create(descriptor, Location.Create(root.SyntaxTree, recordDecl.Identifier.Span));

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").TrimEnd('\n');
}
