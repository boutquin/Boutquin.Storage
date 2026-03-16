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
namespace Boutquin.Storage.SourceGenerator;

// Suppresses false-positive diagnostics on [Key] types.
// The generator already produces the required comparison operators, so CA1036
// ("Override methods on comparable types") and S1210 (SonarAnalyzer equivalent)
// would be spurious if emitted on generated types.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StorageDiagnosticSuppressor : DiagnosticSuppressor
{
    private const string KeyAttributeFqn = "Boutquin.Storage.Domain.Attributes.KeyAttribute";

    private static readonly SuppressionDescriptor s_suppressCA1036 = new SuppressionDescriptor(
        id: "BSSGS001",
        suppressedDiagnosticId: "CA1036",
        justification: "The Boutquin.Storage source generator produces the required comparison operators for [Key] types.");

    private static readonly SuppressionDescriptor s_suppressS1210 = new SuppressionDescriptor(
        id: "BSSGS002",
        suppressedDiagnosticId: "S1210",
        justification: "The Boutquin.Storage source generator produces the required comparison operators for [Key] types.");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions =>
        ImmutableArray.Create(s_suppressCA1036, s_suppressS1210);

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        var keyAttributeSymbol = context.Compilation.GetTypeByMetadataName(KeyAttributeFqn);
        if (keyAttributeSymbol is null)
        {
            return;
        }

        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            if (diagnostic.Id != "CA1036" && diagnostic.Id != "S1210")
            {
                continue;
            }

            var location = diagnostic.Location;
            if (location == null)
            {
                continue;
            }

            var root = location.SourceTree?.GetRoot();
            if (root is null)
            {
                continue;
            }

            var node = root.FindNode(location.SourceSpan);
            var semanticModel = context.GetSemanticModel(location.SourceTree!);
            var symbol = semanticModel.GetDeclaredSymbol(node);

            if (symbol is not INamedTypeSymbol typeSymbol)
            {
                continue;
            }

            bool hasKeyAttribute = typeSymbol.GetAttributes().Any(a =>
                a.AttributeClass != null &&
                a.AttributeClass.Equals(keyAttributeSymbol, SymbolEqualityComparer.Default));

            if (hasKeyAttribute)
            {
                var suppressionId = diagnostic.Id == "CA1036" ? "BSSGS001" : "BSSGS002";
                var descriptor = suppressionId == "BSSGS001" ? s_suppressCA1036 : s_suppressS1210;
                context.ReportSuppression(Suppression.Create(descriptor, diagnostic));
            }
        }
    }
}
