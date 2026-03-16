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

[Generator]
public sealed class StorageSourceGenerator : IIncrementalGenerator
{
    private const string KeyAttributeFqn = "Boutquin.Storage.Domain.Attributes.KeyAttribute";
    private const string StorageSerializableAttributeFqn = "Boutquin.Storage.Domain.Attributes.StorageSerializableAttribute";
    private const string StorageDefaultsAttributeFqn = "Boutquin.Storage.Domain.Attributes.StorageDefaultsAttribute";

    private const string ISerializableFqn = "Boutquin.Storage.Domain.Interfaces.ISerializable`1";
    private const string IComparableFqn = "System.IComparable`1";
    private const string IEquatableFqn = "System.IEquatable`1";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Pipeline a: [Key]-annotated types
        var keyTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                KeyAttributeFqn,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, ct) => TransformType(ctx, isKey: true, ct))
            .WithTrackingName("KeyTypes");

        // Pipeline b: [StorageSerializable]-annotated types
        var serializableTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                StorageSerializableAttributeFqn,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, ct) => TransformType(ctx, isKey: false, ct))
            .WithTrackingName("SerializableTypes");

        // Pipeline c: [assembly: StorageDefaults(...)]
        var defaults = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                StorageDefaultsAttributeFqn,
                predicate: static (node, _) => node is CompilationUnitSyntax,
                transform: static (ctx, _) => TransformDefaults(ctx))
            .Collect()
            .WithTrackingName("StorageDefaults")
            .Select(static (configs, _) =>
            {
                if (configs.Length > 1)
                {
                    // BSSG004 is emitted by the collect step below; return default
                    return StorageDefaultsConfig.Default;
                }
                return configs.Length == 1 ? configs[0] : StorageDefaultsConfig.Default;
            });

        // Emit BSSG004 for duplicate [StorageDefaults]
        var defaultsDiagnostics = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                StorageDefaultsAttributeFqn,
                predicate: static (node, _) => node is CompilationUnitSyntax,
                transform: static (ctx, _) => ctx.TargetNode.GetLocation())
            .Collect()
            .WithTrackingName("StorageDefaultsDiagnostics");

        context.RegisterSourceOutput(defaultsDiagnostics, static (spc, locations) =>
        {
            if (locations.Length > 1)
            {
                foreach (var loc in locations)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.DuplicateAssemblyDefaults,
                        loc));
                }
            }
        });

        // Combine all types with defaults and emit source + diagnostics
        var allTypes = keyTypes.Collect()
            .Combine(serializableTypes.Collect())
            .Combine(defaults);

        context.RegisterSourceOutput(allTypes, static (spc, combined) =>
        {
            var ((keyResults, serializableResults), defaultsConfig) = combined;

            foreach (var result in keyResults.Concat(serializableResults))
            {
                // Report diagnostics
                foreach (var diag in result.Diagnostics)
                {
                    spc.ReportDiagnostic(diag.ToDiagnostic());
                }

                // Skip generation if there's a blocking error (partial missing, dual-attribute)
                if (result.Diagnostics.Any(d =>
                    d.Severity == DiagnosticSeverity.Error &&
                    (d.Id == RuleIdentifiers.TypeMustBePartial || d.Id == RuleIdentifiers.MutuallyExclusiveAttributes)))
                {
                    continue;
                }

                if (result.TypeToGenerate is { } typeToGenerate)
                {
                    var source = CodeGenerator.Generate(typeToGenerate, defaultsConfig);
                    var fileName = CreateSourceName(typeToGenerate);
                    spc.AddSource(fileName, source);
                }
            }
        });
    }

    private static TransformResult TransformType(
        GeneratorAttributeSyntaxContext ctx,
        bool isKey,
        System.Threading.CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol)
        {
            return TransformResult.Empty;
        }

        var diagnostics = new List<DiagnosticInfo>();

        // BSSG005: mutual exclusion check
        bool hasKey = typeSymbol.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == KeyAttributeFqn);
        bool hasStorageSerializable = typeSymbol.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == StorageSerializableAttributeFqn);
        if (hasKey && hasStorageSerializable)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.MutuallyExclusiveAttributes,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name));
            return new TransformResult(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
        }

        // BSSG002: must be partial
        if (ctx.TargetNode is TypeDeclarationSyntax tds &&
            !tds.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.TypeMustBePartial,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name));
            return new TransformResult(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
        }

        // BSSG003: advisory — should be record struct
        bool isRecordStruct = typeSymbol.IsValueType && typeSymbol.IsRecord;
        if (!isRecordStruct)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.TypeShouldBeRecordStruct,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name));
        }

        // BSSG006: check for already-EXPLICITLY-implemented interfaces.
        // Use the syntax base list rather than AllInterfaces — record structs synthesize
        // IEquatable<T> automatically, which would cause false-positive BSSG006 on every
        // record struct. We only warn/skip when the user explicitly lists the interface
        // in the type declaration's base list.
        bool skipSerializable = ExplicitlyImplementsInterface(ctx.SemanticModel, ctx.TargetNode, ISerializableFqn);
        bool skipComparable = ExplicitlyImplementsInterface(ctx.SemanticModel, ctx.TargetNode, IComparableFqn);
        bool skipEquatable = ExplicitlyImplementsInterface(ctx.SemanticModel, ctx.TargetNode, IEquatableFqn);
        if (skipSerializable)
        {
            diagnostics.Add(DiagnosticInfo.Create(DiagnosticDescriptors.InterfaceAlreadyImplemented,
                ctx.TargetNode.GetLocation(), typeSymbol.Name, "ISerializable<T>"));
        }

        if (isKey && skipComparable)
        {
            diagnostics.Add(DiagnosticInfo.Create(DiagnosticDescriptors.InterfaceAlreadyImplemented,
                ctx.TargetNode.GetLocation(), typeSymbol.Name, "IComparable<T>"));
        }

        if (isKey && skipEquatable)
        {
            diagnostics.Add(DiagnosticInfo.Create(DiagnosticDescriptors.InterfaceAlreadyImplemented,
                ctx.TargetNode.GetLocation(), typeSymbol.Name, "IEquatable<T>"));
        }

        // Extract namespace
        string? ns = typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : typeSymbol.ContainingNamespace.ToDisplayString();

        // Extract accessibility
        string accessibility = typeSymbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";

        // Extract properties
        var properties = new List<PropertyInfo>();
        foreach (var prop in typeSymbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            if (prop.IsStatic)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();

            var propInfo = ClassifyProperty(prop, diagnostics, typeSymbol.Name, ctx.TargetNode.GetLocation());
            properties.Add(propInfo);
        }

        // Extract parent types (for nested types)
        var parentTypes = new List<ParentTypeInfo>();
        var containingType = typeSymbol.ContainingType;
        while (containingType is not null)
        {
            var ptds = containingType.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as TypeDeclarationSyntax;
            string kind = GetTypeKind(containingType);
            string modifiers = ptds is not null
                ? string.Join(" ", ptds.Modifiers.Select(m => m.Text))
                : $"{(containingType.DeclaredAccessibility == Accessibility.Public ? "public" : "internal")} partial";
            string? genericParams = containingType.TypeParameters.Length > 0
                ? "<" + string.Join(", ", containingType.TypeParameters.Select(tp => tp.Name)) + ">"
                : null;
            string? constraints = BuildConstraints(containingType.TypeParameters);

            parentTypes.Add(new ParentTypeInfo(containingType.Name, kind, modifiers, genericParams, constraints));
            containingType = containingType.ContainingType;
        }
        parentTypes.Reverse(); // outermost first

        var typeToGenerate = new TypeToGenerate(
            Name: typeSymbol.Name,
            Namespace: ns,
            Accessibility: accessibility,
            IsKey: isKey,
            IsRecordStruct: isRecordStruct,
            SkipComparable: skipComparable,
            SkipEquatable: skipEquatable,
            SkipSerializable: skipSerializable,
            Properties: new EquatableArray<PropertyInfo>(properties.ToArray()),
            ParentTypes: new EquatableArray<ParentTypeInfo>(parentTypes.ToArray()));

        return new TransformResult(typeToGenerate, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
    }

    private static PropertyInfo ClassifyProperty(
        IPropertySymbol prop,
        List<DiagnosticInfo> diagnostics,
        string typeName,
        Location location)
    {
        var type = prop.Type;
        var typeName2 = type.ToDisplayString();

        // Check primitive
        if (TryGetPrimitiveKind(type, out var pk))
        {
            return new PropertyInfo(prop.Name, typeName2, TypeKind.Primitive, pk, null);
        }

        // Check annotated type (has [Key] or [StorageSerializable])
        if (IsAnnotatedType(type))
        {
            return new PropertyInfo(prop.Name, typeName2, TypeKind.AnnotatedType, null, typeName2);
        }

        // Check IEnumerable<T>
        if (TryGetEnumerableElementType(type, out var elementType) && elementType is not null)
        {
            if (TryGetPrimitiveKind(elementType, out var epk))
            {
                return new PropertyInfo(prop.Name, typeName2, TypeKind.PrimitiveCollection, epk,
                    elementType.ToDisplayString());
            }
            if (IsAnnotatedType(elementType))
            {
                return new PropertyInfo(prop.Name, typeName2, TypeKind.AnnotatedCollection, null,
                    elementType.ToDisplayString());
            }
            // Unsupported element type in IEnumerable<T>
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.UnsupportedPropertyType,
                location,
                prop.Name, typeName, typeName2));
            return new PropertyInfo(prop.Name, typeName2, TypeKind.Unsupported, null, null);
        }

        // Unsupported
        diagnostics.Add(DiagnosticInfo.Create(
            DiagnosticDescriptors.UnsupportedPropertyType,
            location,
            prop.Name, typeName, typeName2));
        return new PropertyInfo(prop.Name, typeName2, TypeKind.Unsupported, null, null);
    }

    private static bool TryGetPrimitiveKind(ITypeSymbol type, out PrimitiveKind kind)
    {
        kind = default;
        var specialType = type.SpecialType;
        switch (specialType)
        {
            case SpecialType.System_Int32: kind = PrimitiveKind.Int; return true;
            case SpecialType.System_Int64: kind = PrimitiveKind.Long; return true;
            case SpecialType.System_String: kind = PrimitiveKind.String; return true;
            case SpecialType.System_Single: kind = PrimitiveKind.Float; return true;
            case SpecialType.System_Double: kind = PrimitiveKind.Double; return true;
            case SpecialType.System_Boolean: kind = PrimitiveKind.Bool; return true;
            case SpecialType.System_Byte: kind = PrimitiveKind.Byte; return true;
            case SpecialType.System_Char: kind = PrimitiveKind.Char; return true;
            default: return false;
        }
    }

    private static bool IsAnnotatedType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol namedType)
        {
            return false;
        }

        return namedType.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == KeyAttributeFqn ||
            a.AttributeClass?.ToDisplayString() == StorageSerializableAttributeFqn);
    }

    private static bool TryGetEnumerableElementType(ITypeSymbol type, out ITypeSymbol? elementType)
    {
        elementType = null;
        if (type is not INamedTypeSymbol namedType)
        {
            return false;
        }

        // Direct IEnumerable<T>
        if (namedType.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
        {
            elementType = namedType.TypeArguments[0];
            return true;
        }

        // Types that implement IEnumerable<T> (List<T>, T[], etc.)
        foreach (var iface in namedType.AllInterfaces)
        {
            if (iface.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            {
                elementType = iface.TypeArguments[0];
                return true;
            }
        }

        return false;
    }

    // Check whether the user has EXPLICITLY listed an interface in the type's base list.
    // We use the syntax base list rather than AllInterfaces to avoid false positives from
    // compiler-synthesized implementations (e.g., record struct always synthesizes IEquatable<T>).
    private static bool ExplicitlyImplementsInterface(SemanticModel semanticModel, SyntaxNode targetNode, string interfaceFqn)
    {
        if (targetNode is not TypeDeclarationSyntax tds)
        {
            return false;
        }

        if (tds.BaseList is null)
        {
            return false;
        }

        foreach (var baseType in tds.BaseList.Types)
        {
            var typeInfo = semanticModel.GetTypeInfo(baseType.Type);
            var symbol = typeInfo.Type as INamedTypeSymbol;
            if (symbol is null)
            {
                continue;
            }

            var originalFqn = symbol.OriginalDefinition.ToDisplayString();
            if (originalFqn == interfaceFqn)
            {
                return true;
            }

            // Also match generic form like "System.IEquatable<T>" vs "System.IEquatable`1"
            var shortFqn = interfaceFqn.Replace("`1", "");
            if (originalFqn.StartsWith(shortFqn, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static StorageDefaultsConfig TransformDefaults(GeneratorAttributeSyntaxContext ctx)
    {
        bool generateOperators = true;
        if (ctx.Attributes.Length > 0)
        {
            var attr = ctx.Attributes[0];
            foreach (var arg in attr.NamedArguments)
            {
                if (arg.Key == "GenerateComparisonOperators" && arg.Value.Value is bool b)
                {
                    generateOperators = b;
                }
            }
        }
        return new StorageDefaultsConfig(generateOperators);
    }

    private static string GetTypeKind(INamedTypeSymbol type)
    {
        if (type.IsRecord)
        {
            return type.IsValueType ? "record struct" : "record class";
        }
        return type.IsValueType ? "struct" : "class";
    }

    private static string? BuildConstraints(ImmutableArray<ITypeParameterSymbol> typeParams)
    {
        if (typeParams.Length == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var tp in typeParams)
        {
            var constraints = new List<string>();
            if (tp.HasReferenceTypeConstraint)
            {
                constraints.Add("class");
            }

            if (tp.HasValueTypeConstraint)
            {
                constraints.Add("struct");
            }

            if (tp.HasUnmanagedTypeConstraint)
            {
                constraints.Add("unmanaged");
            }

            if (tp.HasNotNullConstraint)
            {
                constraints.Add("notnull");
            }

            foreach (var ct in tp.ConstraintTypes)
            {
                constraints.Add(ct.ToDisplayString());
            }

            if (tp.HasConstructorConstraint)
            {
                constraints.Add("new()");
            }

            if (constraints.Count > 0)
            {
                sb.Append($"where {tp.Name} : {string.Join(", ", constraints)} ");
            }
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
    }

    private static string CreateSourceName(TypeToGenerate type)
    {
        // Pattern borrowed from StronglyTypedId: concatenate full type path to avoid conflicts
        var parts = new List<string>();
        if (type.Namespace is not null)
        {
            parts.Add(type.Namespace);
        }

        foreach (var parent in type.ParentTypes)
        {
            parts.Add(parent.Name);
        }

        parts.Add(type.Name);
        return string.Join(".", parts) + ".g.cs";
    }
}

internal readonly record struct TransformResult(
    TypeToGenerate? TypeToGenerate,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    public static readonly TransformResult Empty = new TransformResult(null, EquatableArray<DiagnosticInfo>.Empty);
}
