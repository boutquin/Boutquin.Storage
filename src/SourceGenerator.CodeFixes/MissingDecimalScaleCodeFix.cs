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
namespace Boutquin.Storage.SourceGenerator.CodeFixes;

/// <summary>
/// Roslyn <see cref="CodeFixProvider"/> for BSSG017 (missing or out-of-range
/// <c>[DecimalScale(N)]</c> on a <c>decimal</c> / <c>decimal?</c> property of a
/// <c>[Key]</c> record). Inserts <c>[property: DecimalScale(2)]</c> on the flagged
/// parameter — scale 2 is a neutral default that consumers can adjust to their
/// domain (e.g., 4 for FX prices, 8 for crypto, 0 for integral quantities).
/// </summary>
/// <remarks>
/// Ships alongside <see cref="MissingTagCodeFix"/> in
/// <c>Boutquin.Storage.SourceGenerator.CodeFixes.dll</c>. Single-document fix only;
/// <see cref="FixAllProvider"/> is a follow-up.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MissingDecimalScaleCodeFix))]
[Shared]
public sealed class MissingDecimalScaleCodeFix : CodeFixProvider
{
    private const string BSSG017 = "BSSG017";
    private const int DefaultScale = 2;

    private static readonly System.Text.RegularExpressions.Regex s_messagePattern =
        new(
            @"^Property '(?<name>[^']+)' on \[Key\] type '[^']+' must declare \[DecimalScale\(N\)\]",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <inheritdoc/>
    public sealed override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(BSSG017);

    /// <inheritdoc/>
    public sealed override FixAllProvider? GetFixAllProvider() => null;

    /// <inheritdoc/>
    public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            if (!FixableDiagnosticIds.Contains(diagnostic.Id))
            {
                continue;
            }

            var match = s_messagePattern.Match(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
            if (!match.Success)
            {
                continue;
            }

            var propertyName = match.Groups["name"].Value;

            var diagnosticSpan = diagnostic.Location.SourceSpan;
            var node = root.FindNode(diagnosticSpan);
            var recordDecl = node.FirstAncestorOrSelf<TypeDeclarationSyntax>(
                t => t is RecordDeclarationSyntax or StructDeclarationSyntax or ClassDeclarationSyntax);
            if (recordDecl is not RecordDeclarationSyntax record || record.ParameterList is null)
            {
                continue;
            }

            var parameter = record.ParameterList.Parameters
                .FirstOrDefault(p => string.Equals(p.Identifier.Text, propertyName, System.StringComparison.Ordinal));
            if (parameter is null)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: $"Add [property: DecimalScale({DefaultScale})] to '{propertyName}'",
                    createChangedDocument: ct => AddDecimalScaleAsync(context.Document, root, parameter, DefaultScale, ct),
                    equivalenceKey: $"AddDecimalScale:{propertyName}:{DefaultScale}"),
                diagnostic);
        }
    }

    private static Task<Document> AddDecimalScaleAsync(
        Document document,
        SyntaxNode root,
        ParameterSyntax parameter,
        int scale,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var attribute = SyntaxFactory.Attribute(
                SyntaxFactory.IdentifierName("DecimalScale"))
            .WithArgumentList(
                SyntaxFactory.AttributeArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.AttributeArgument(
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.NumericLiteralExpression,
                                SyntaxFactory.Literal(scale))))));

        // Mirror MissingTagCodeFix's indent-transfer recipe so the new attribute aligns
        // with the existing parameter rather than landing at column 0.
        var originalLeading = parameter.Type is null
            ? parameter.GetLeadingTrivia()
            : parameter.Type.GetLeadingTrivia();

        var attributeList = SyntaxFactory.AttributeList(
                SyntaxFactory.AttributeTargetSpecifier(
                    SyntaxFactory.Token(SyntaxKind.PropertyKeyword)),
                SyntaxFactory.SingletonSeparatedList(attribute))
            .WithLeadingTrivia(originalLeading)
            .WithTrailingTrivia(SyntaxFactory.Space);

        var newParameter = parameter;
        if (parameter.Type is not null)
        {
            newParameter = newParameter.WithType(
                parameter.Type.WithLeadingTrivia(SyntaxFactory.TriviaList()));
        }
        newParameter = newParameter.WithAttributeLists(
            newParameter.AttributeLists.Insert(0, attributeList));

        var newRoot = root.ReplaceNode(parameter, newParameter);
        return Task.FromResult(document.WithSyntaxRoot(newRoot));
    }
}
