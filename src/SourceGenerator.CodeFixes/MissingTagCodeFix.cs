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
/// Roslyn <see cref="CodeFixProvider"/> for BSSG010 (missing <c>[Tag]</c> on a
/// property of a <c>[Key]</c> or <c>[StorageSerializable]</c> record). Inserts
/// <c>[property: Tag(N)]</c> on a positional constructor parameter, or a plain
/// <c>[Tag(N)]</c> on a body-declared property, where N is the lowest
/// positive tag that is neither in use on a sibling property nor reserved via
/// <c>[ReservedTag(N)]</c> on the type. Single-document fix only;
/// <see cref="FixAllProvider"/> is a deliberate follow-up and can be added if consumers request it.
/// </summary>
/// <remarks>
/// <para>
/// The diagnostic location points at the record header (the source generator
/// emits BSSG010 per-missing-property at the type-level location so callers can
/// see every missing tag at once). The property name is carried in the
/// diagnostic message under the pattern
/// <c>"Property 'NAME' on type 'TYPE' must declare a [Tag(N)]. Suggested: [property: Tag(N)]."</c>
/// This code fix recovers the property name by parsing the message and then
/// re-computes the suggested tag from the syntax tree so the offered fix matches
/// current sibling tags and <c>[ReservedTag]</c> attributes, even if the
/// diagnostic was produced against slightly older source than the buffer the
/// IDE is editing.
/// </para>
/// <para>
/// Lives in <c>Boutquin.Storage.SourceGenerator.CodeFixes.dll</c> and ships
/// alongside the generator DLL under <c>analyzers/dotnet/cs/</c> in the
/// generator's NuGet package — Roslyn discovers both DLLs together.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MissingTagCodeFix))]
[Shared]
public sealed class MissingTagCodeFix : CodeFixProvider
{
    // Hardcoded ID rather than referencing the generator project's RuleIdentifiers.MissingTag:
    // pulling that constant in would require a project reference to Boutquin.Storage.SourceGenerator,
    // which the Roslyn analyzer packaging convention avoids (analyzer and code-fix DLLs are
    // peers under analyzers/dotnet/cs, not a chain of references). The ID is a published part
    // of the generator's public diagnostic surface and rarely changes.
    private const string BSSG010 = "BSSG010";

    // Matches the BSSG010 message shape. Capture group "name" is the property name whose
    // tag is missing; group "tag" is the source generator's originally suggested number
    // (recomputed below so the fix stays correct under syntax drift).
    private static readonly System.Text.RegularExpressions.Regex s_messagePattern =
        new(
            @"^Property '(?<name>[^']+)' on type '[^']+' must declare a \[Tag\(N\)\]\. Suggested: \[property: Tag\((?<tag>\d+)\)\]\.$",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <inheritdoc/>
    public sealed override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(BSSG010);

    // Single-document fix only. FixAllProvider is a deliberate follow-up.
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

            // Find the record declaration at (or enclosing) the diagnostic location.
            var diagnosticSpan = diagnostic.Location.SourceSpan;
            var node = root.FindNode(diagnosticSpan);
            var recordDecl = node.FirstAncestorOrSelf<TypeDeclarationSyntax>(
                t => t is RecordDeclarationSyntax or StructDeclarationSyntax or ClassDeclarationSyntax);
            if (recordDecl is not RecordDeclarationSyntax record)
            {
                continue;
            }

            // Positional records carry the property as a primary-constructor parameter; body-
            // property records declare it as a PropertyDeclarationSyntax inside the body. Both
            // raise BSSG010, so the fix must offer an action for either shape — a body-property
            // record previously fell through to a silent zero-action no-op.
            var parameter = record.ParameterList?.Parameters
                .FirstOrDefault(p => string.Equals(p.Identifier.Text, propertyName, System.StringComparison.Ordinal));
            if (parameter is not null)
            {
                var suggestedTag = ComputeSuggestedTag(record, parameter);

                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: $"Add [property: Tag({suggestedTag})] to '{propertyName}'",
                        createChangedDocument: ct => AddTagAttributeAsync(context.Document, root, parameter, suggestedTag, ct),
                        equivalenceKey: $"AddTag:{propertyName}:{suggestedTag}"),
                    diagnostic);
                continue;
            }

            var property = record.Members
                .OfType<PropertyDeclarationSyntax>()
                .FirstOrDefault(p => string.Equals(p.Identifier.Text, propertyName, System.StringComparison.Ordinal));
            if (property is null)
            {
                continue;
            }

            var bodyTag = ComputeSuggestedTagForProperty(record, property);

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: $"Add [Tag({bodyTag})] to '{propertyName}'",
                    createChangedDocument: ct => AddTagAttributeToPropertyAsync(context.Document, root, property, bodyTag, ct),
                    equivalenceKey: $"AddTag:{propertyName}:{bodyTag}"),
                diagnostic);
        }
    }

    // Recomputes the lowest unused positive tag number on the record, accounting for both
    // declared sibling tags and [ReservedTag(N)] attributes on the type. Matches the
    // generator's second-pass suggestion loop in StorageSourceGenerator.ValidateTags.
    private static int ComputeSuggestedTag(RecordDeclarationSyntax record, ParameterSyntax target)
    {
        var used = new HashSet<int>();

        foreach (var parameter in record.ParameterList!.Parameters)
        {
            if (parameter == target)
            {
                continue;
            }

            var tag = ExtractDeclaredTag(parameter);
            if (tag is not null)
            {
                used.Add(tag.Value);
            }
        }

        foreach (var list in record.AttributeLists)
        {
            foreach (var attribute in list.Attributes)
            {
                if (!IsAttributeName(attribute, "ReservedTag"))
                {
                    continue;
                }

                var tag = ExtractIntArgument(attribute);
                if (tag is not null)
                {
                    used.Add(tag.Value);
                }
            }
        }

        var next = 1;
        while (used.Contains(next))
        {
            next++;
        }
        return next;
    }

    // Body-property variant of ComputeSuggestedTag. Gathers used tags from positional
    // parameters, sibling body properties, and [ReservedTag(N)] attributes, then returns the
    // lowest unused positive tag — the same "lowest unused positive" policy the generator uses.
    private static int ComputeSuggestedTagForProperty(RecordDeclarationSyntax record, PropertyDeclarationSyntax target)
    {
        var used = new HashSet<int>();

        if (record.ParameterList is not null)
        {
            foreach (var parameter in record.ParameterList.Parameters)
            {
                var tag = ExtractDeclaredTag(parameter);
                if (tag is not null)
                {
                    used.Add(tag.Value);
                }
            }
        }

        foreach (var property in record.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (property == target)
            {
                continue;
            }

            var tag = ExtractDeclaredTagFromProperty(property);
            if (tag is not null)
            {
                used.Add(tag.Value);
            }
        }

        foreach (var list in record.AttributeLists)
        {
            foreach (var attribute in list.Attributes)
            {
                if (!IsAttributeName(attribute, "ReservedTag"))
                {
                    continue;
                }

                var tag = ExtractIntArgument(attribute);
                if (tag is not null)
                {
                    used.Add(tag.Value);
                }
            }
        }

        var next = 1;
        while (used.Contains(next))
        {
            next++;
        }
        return next;
    }

    private static int? ExtractDeclaredTagFromProperty(PropertyDeclarationSyntax property)
    {
        foreach (var list in property.AttributeLists)
        {
            foreach (var attribute in list.Attributes)
            {
                if (!IsAttributeName(attribute, "Tag"))
                {
                    continue;
                }

                var value = ExtractIntArgument(attribute);
                if (value is not null)
                {
                    return value;
                }
            }
        }
        return null;
    }

    private static int? ExtractDeclaredTag(ParameterSyntax parameter)
    {
        foreach (var list in parameter.AttributeLists)
        {
            foreach (var attribute in list.Attributes)
            {
                if (!IsAttributeName(attribute, "Tag"))
                {
                    continue;
                }

                var value = ExtractIntArgument(attribute);
                if (value is not null)
                {
                    return value;
                }
            }
        }
        return null;
    }

    private static bool IsAttributeName(AttributeSyntax attribute, string name)
    {
        var text = attribute.Name.ToString();
        return text == name
            || text.EndsWith("." + name, System.StringComparison.Ordinal)
            || text == name + "Attribute"
            || text.EndsWith("." + name + "Attribute", System.StringComparison.Ordinal);
    }

    private static int? ExtractIntArgument(AttributeSyntax attribute)
    {
        var args = attribute.ArgumentList?.Arguments;
        if (args is null || args.Value.Count == 0)
        {
            return null;
        }

        if (args.Value[0].Expression is LiteralExpressionSyntax literal
            && literal.Token.Value is int i)
        {
            return i;
        }
        return null;
    }

    private static Task<Document> AddTagAttributeAsync(
        Document document,
        SyntaxNode root,
        ParameterSyntax parameter,
        int suggestedTag,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var attribute = SyntaxFactory.Attribute(
                SyntaxFactory.IdentifierName("Tag"))
            .WithArgumentList(
                SyntaxFactory.AttributeArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.AttributeArgument(
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.NumericLiteralExpression,
                                SyntaxFactory.Literal(suggestedTag))))));

        // When a parameter has no existing attribute list, its leading trivia (indent / newline)
        // lives on the type token. When we prepend an attribute list, we must transfer that
        // trivia onto the new attribute list's opening bracket and clear it from the type token,
        // otherwise the attribute appears at column 0 and the type retains its indent — which
        // reads as "<newline indent>[property: Tag(N)] <newline indent>string Name".
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

    // Body-property variant of AddTagAttributeAsync. Prepends a plain [Tag(N)] attribute list
    // (no `property:` target specifier — that specifier is only needed on positional
    // constructor parameters to redirect the attribute to the synthesized property). The
    // property's leading trivia (indent / newline) is transferred to the new attribute list so
    // the attribute lands on its own indented line above the property declaration.
    private static Task<Document> AddTagAttributeToPropertyAsync(
        Document document,
        SyntaxNode root,
        PropertyDeclarationSyntax property,
        int suggestedTag,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var attribute = SyntaxFactory.Attribute(
                SyntaxFactory.IdentifierName("Tag"))
            .WithArgumentList(
                SyntaxFactory.AttributeArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.AttributeArgument(
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.NumericLiteralExpression,
                                SyntaxFactory.Literal(suggestedTag))))));

        // Transfer the property's leading trivia (indent / newline) to the new attribute list so
        // the attribute starts on the property's own line, then clip the property's own leading
        // trivia to a single space — mirrors the same-line layout the positional-parameter path
        // produces. A space trailing trivia keeps the attribute and the property on one line.
        var originalLeading = property.GetLeadingTrivia();

        var attributeList = SyntaxFactory.AttributeList(
                SyntaxFactory.SingletonSeparatedList(attribute))
            .WithLeadingTrivia(originalLeading)
            .WithTrailingTrivia(SyntaxFactory.Space);

        var newProperty = property
            .WithLeadingTrivia(SyntaxFactory.TriviaList())
            .WithAttributeLists(property.AttributeLists.Insert(0, attributeList));

        var newRoot = root.ReplaceNode(property, newProperty);
        return Task.FromResult(document.WithSyntaxRoot(newRoot));
    }
}
