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

/// <summary>
/// Incremental source generator that discovers types annotated with
/// <c>[ColumnarStorable]</c> and emits a partial-type extension carrying a static
/// <c>ColumnarFieldPlan</c> property. Validates per-property <c>[ColumnEncoding]</c>
/// overrides against CLR-type compatibility and emits <c>BSSG007</c> on mismatch.
/// </summary>
[Generator]
public sealed class ColumnarSourceGenerator : IIncrementalGenerator
{
    private const string ColumnarStorableAttributeFqn = "Boutquin.Storage.Domain.Attributes.ColumnarStorableAttribute";
    private const string StorageSerializableAttributeFqn = "Boutquin.Storage.Domain.Attributes.StorageSerializableAttribute";
    private const string ColumnEncodingAttributeFqn = "Boutquin.Storage.Domain.Attributes.ColumnEncodingAttribute";
    private const string IProjectableFqn = "Boutquin.Storage.Domain.Interfaces.IProjectable`1";
    private const string SetsRequiredMembersAttributeFqn = "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var columnarTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ColumnarStorableAttributeFqn,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, ct) => Transform(ctx, ct))
            .WithTrackingName("ColumnarTypes");

        // Collected, because a hint name is unique only relative to every other emitted type:
        // identities that differ only by case must be told apart (HintNames.Assign).
        context.RegisterSourceOutput(columnarTypes.Collect(), static (spc, results) =>
        {
            var emitted = new List<ColumnarTypePlan>();
            foreach (var result in results)
            {
                foreach (var diag in result.Diagnostics)
                {
                    spc.ReportDiagnostic(diag.ToDiagnostic());
                }

                if (result.Plan is { } plan
                    && !result.Diagnostics.Any(d =>
                        d.Severity == DiagnosticSeverity.Error
                        && (d.Id == RuleIdentifiers.TypeMustBePartial
                            || d.Id == RuleIdentifiers.IncompatibleColumnEncoding
                            // BSSG020: a settable property with no columnar encoding. Emitting anyway
                            // drops the column silently from the plan, so Rehydrate leaves the property
                            // at default(T) on every read — runtime data loss from a compile-time shape
                            // mistake. Block emission so the build fails loudly instead.
                            || d.Id == RuleIdentifiers.ColumnarFieldNotEncodable)))
                {
                    emitted.Add(plan);
                }
            }

            var hintNames = HintNames.Assign(emitted.Select(p => p.HintIdentity).ToList(), ".Columnar.g.cs");
            for (var i = 0; i < emitted.Count; i++)
            {
                spc.AddSource(hintNames[i], ColumnarCodeGenerator.Generate(emitted[i]));
            }
        });
    }

    // ───── Pipeline types ─────────────────────────────────────────────

    internal readonly record struct ColumnarTransformResult(
        ColumnarTypePlan? Plan,
        EquatableArray<DiagnosticInfo> Diagnostics)
    {
        public static ColumnarTransformResult Empty => new(null, EquatableArray<DiagnosticInfo>.Empty);
    }

    internal readonly record struct ColumnarTypePlan(
        string Name,
        string? Namespace,
        string Accessibility,
        string TypeKeyword, // "class" / "record" / "record struct" / "struct"
        bool SkipProjectable, // true when the user's partial explicitly declares IProjectable<TSelf> — the Columnar emitter defers to their implementation
        bool SkipRehydratable, // true when the user's partial already declares static TSelf Rehydrate(IReadOnlyDictionary<string, Array>, int)
        bool SkipSortComparer, // true when the user's partial already declares static IComparer<TSelf> CreateColumnSortComparer(ColumnSortSpec)
        EquatableArray<ColumnarFieldPlan> Fields,
        EquatableArray<ColumnarSortablePropertyPlan> SortableProperties,
        string HintIdentity, // HintNames.Identity of the type; the output step derives the unique hint name
        EquatableArray<ParentTypeInfo> ParentTypes, // containing-type chain, outermost first; empty for a top-level target
        bool HasParameterlessConstructor, // record class only: Rehydrate may seed with `new TSelf()`
        EquatableArray<string> SeedConstructorArguments, // record class without one: per seed-constructor parameter, the column it reads, or "" for `default!`
        EquatableArray<string> RequiredMembersToSeed); // record class: required members a `new TSelf(...)` seed sets to default in an object initializer (CS9035 otherwise)

    internal readonly record struct ColumnarFieldPlan(
        string PropertyName,
        string EncodingEnumMemberName,
        string ClrArrayTypeName); // e.g. "global::System.DateOnly[]" — used by AppendRehydrate for direct typed casts

    // Carries the per-property typing required by the generator-emitted IComparer<TSelf>.
    // Distinct from ColumnarFieldPlan because sortable properties are a superset: any public
    // instance scalar (Tier 1/2/3, Enum) qualifies regardless of whether it carries a column
    // encoding. ClrPropertyTypeName is the global::-qualified property type used to drive
    // Comparer<T>.Default.Compare in the emitted switch.
    internal readonly record struct ColumnarSortablePropertyPlan(
        string PropertyName,
        string ClrPropertyTypeName); // e.g. "global::System.DateOnly", "string", "double", "global::Boutquin.Storage.MySegmentKind"

    // ───── Transform (semantic discovery) ─────────────────────────────

    private static ColumnarTransformResult Transform(
        GeneratorAttributeSyntaxContext ctx,
        System.Threading.CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol)
        {
            return ColumnarTransformResult.Empty;
        }

        var diagnostics = new List<DiagnosticInfo>();

        // Require the target type to be a record (record class or record struct). The columnar
        // code generator emits C# `with` expressions in Rehydrate and ProjectTo — a record-only
        // feature. Applying [ColumnarStorable] to a plain class or struct compiles in the generator
        // but produces CS8858/CS9035 in the consumer build. Emit BSSG022 and suppress emission.
        if (!typeSymbol.IsRecord)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.ColumnarStorableMustBeRecord,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name));
            return new ColumnarTransformResult(null, new EquatableArray<DiagnosticInfo>([.. diagnostics]));
        }

        // Require partial so the generator can emit a companion declaration carrying the
        // ColumnarFieldPlan property. Mirrors StorageSourceGenerator's BSSG002.
        // Return early (null plan) to match the non-record path above — both shape-failure
        // diagnostics now consistently suppress emission via a null plan rather than relying
        // on the emission guard's diagnostic filter. This eliminates the asymmetry where
        // !IsRecord returned null but !isPartial fell through and built a full plan.
        var syntaxNode = ctx.TargetNode as TypeDeclarationSyntax;
        var isPartial = syntaxNode?.Modifiers.Any(SyntaxKind.PartialKeyword) ?? false;
        if (!isPartial)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.TypeMustBePartial,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name));
            return new ColumnarTransformResult(null, new EquatableArray<DiagnosticInfo>([.. diagnostics]));
        }

        // BSSG026 — every containing type must be partial too: the emitted code re-opens each
        // (shared BuildParentTypes), so a non-partial one would be declared twice (CS0101).
        var nonPartialParents = TargetShapeDiagnostics.FindNonPartialContainingTypes(typeSymbol);
        if (nonPartialParents.Count > 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                TargetShapeDiagnostics.ContainingTypeNotPartial,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name,
                string.Join("', '", nonPartialParents)));
            return new ColumnarTransformResult(null, new EquatableArray<DiagnosticInfo>([.. diagnostics]));
        }

        // BSSG023 — the emitted partial repeats the bare name, which would declare a distinct
        // non-generic type whose generated members target nothing. Mirrors StorageSourceGenerator.
        if (typeSymbol.TypeParameters.Length > 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                TargetShapeDiagnostics.GenericTargetNotSupported,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name));
            return new ColumnarTransformResult(null, new EquatableArray<DiagnosticInfo>([.. diagnostics]));
        }

        // BSSG024 — an inherited stored property has no column (discovery below reads the type's
        // own members), so it would rehydrate as default(T): the BSSG020 data-loss shape reached
        // through a base type. Stored means settable, or get-only with a backing field — the same
        // test the declared-member loop below applies (BSSG025); only a computed inherited getter
        // is derived state. A public instance field, declared or inherited, takes the same path
        // (discovery below reads properties only); it is stored state even when readonly, so every
        // one is rejected.
        var inheritedProperties = TargetShapeDiagnostics.FindInheritedPublicProperties(typeSymbol, storedOnly: true);
        var publicFields = TargetShapeDiagnostics.FindPublicInstanceFields(typeSymbol);
        if (inheritedProperties.Count > 0 || publicFields.Count > 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                TargetShapeDiagnostics.InheritedPropertiesNotSerialized,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name,
                TargetShapeDiagnostics.DescribeUnplannedState(inheritedProperties, publicFields)));
            return new ColumnarTransformResult(null, new EquatableArray<DiagnosticInfo>([.. diagnostics]));
        }

        var fields = new List<ColumnarFieldPlan>();
        var sortableProperties = new List<ColumnarSortablePropertyPlan>();
        // Settable public properties: each becomes a column or carries its own BSSG020/BSSG007.
        var columnCandidates = new HashSet<string>(StringComparer.Ordinal);
        var storedGetOnly = new List<IPropertySymbol>();
        foreach (var member in typeSymbol.GetMembers())
        {
            ct.ThrowIfCancellationRequested();
            // An indexer is not row state: it has no column name (its symbol name is `this[]`)
            // and no single value per row, as TargetShapeDiagnostics also treats it.
            if (member is not IPropertySymbol property
                || property.DeclaredAccessibility != Accessibility.Public
                || property.IsStatic
                || property.IsIndexer
                || property.GetMethod is null)
            {
                continue;
            }

            // Delegate primitive-type recognition to the shared TypeClassifier so row and
            // columnar paths stay in lock step. The classifier's FQN is the "global::"-stripped
            // form we then use for the column-specific encoding dispatch and for the BSSG007
            // compatibility diagnostic message.
            var descriptor = TypeClassifier.Classify(property.Type);
            var propertyClrType = descriptor.FullyQualifiedTypeName;
            var overrideEncoding = ReadColumnEncodingOverride(property);
            var resolvedEncoding = overrideEncoding ?? ChooseDefault(descriptor);

            // Collect sortable properties for the IComparer<TSelf> emission. The set is
            // the union of (Tier 1 numerics, Tier 2 non-numerics excluding char in practice, Tier 3
            // BCL primitives, enums) — i.e., every kind whose CLR type implements IComparable
            // unconditionally. Nullable<T> is excluded so the generated Compare never sees a null
            // CompareTo target — mirrors the existing reflection comparator's nullability guard.
            // Annotated types and collections are excluded because their orderability depends on
            // user code; the existing reflection fallback covers those.
            if (IsSortable(descriptor))
            {
                var clrPropertyTypeName = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                sortableProperties.Add(new ColumnarSortablePropertyPlan(property.Name, clrPropertyTypeName));
            }

            // A computed get-only property is derived state: Rehydrate assigns via `with { }`,
            // which requires a set/init accessor, and the getter recomputes from stored columns —
            // excluding it loses nothing. (It remains eligible as a sort key above; sorting only
            // reads.) A get-only property with a backing field (`{ get; }`, `get => field`) is
            // stored state a constructor assigns: with no column it would never be written and
            // would rehydrate as default, so it is rejected below (BSSG025) instead.
            if (property.SetMethod is null)
            {
                if (TargetShapeDiagnostics.HasBackingField(typeSymbol, property))
                {
                    storedGetOnly.Add(property);
                }

                continue;
            }

            columnCandidates.Add(property.Name);
            if (resolvedEncoding is null)
            {
                // Fail closed: a settable property without a column would never be written and
                // would silently rehydrate as default(T) — runtime data loss from a compile-time
                // shape mistake. (The segment format sources its columns exclusively from the
                // emitted plan, so nothing downstream surfaces the gap.)
                var unsupportedLocation = property.Locations.FirstOrDefault()
                                          ?? ctx.TargetNode.GetLocation();
                diagnostics.Add(DiagnosticInfo.Create(
                    DiagnosticDescriptors.ColumnarFieldNotEncodable,
                    unsupportedLocation,
                    property.Name,
                    typeSymbol.Name,
                    propertyClrType));
                continue;
            }

            if (overrideEncoding is not null
                && !IsCompatible(overrideEncoding.Value, propertyClrType, descriptor))
            {
                var propertyLocation = property.Locations.FirstOrDefault()
                                       ?? ctx.TargetNode.GetLocation();
                diagnostics.Add(DiagnosticInfo.Create(
                    DiagnosticDescriptors.IncompatibleColumnEncoding,
                    propertyLocation,
                    property.Name,
                    typeSymbol.Name,
                    overrideEncoding.Value.ToString(),
                    propertyClrType,
                    CompatibleTypesFor(overrideEncoding.Value)));
                continue;
            }

            // Build the globally-qualified array type name for AppendRehydrate casts.
            // SymbolDisplayFormat.FullyQualifiedFormat adds "global::" prefix which ensures
            // the generated cast is unambiguous regardless of the target namespace.
            var clrArrayTypeName = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "[]";

            fields.Add(new ColumnarFieldPlan(property.Name, resolvedEncoding.Value.ToString(), clrArrayTypeName));
        }

        // BSSG025 — a get-only property with a backing field is stored state the generated
        // Rehydrate cannot rebuild (`with { }` needs a set/init accessor, and the plan has no
        // column for it), so every segment round trip would return it as default. Reject the
        // type, located on each such property, as the row generator rejects the same shape.
        if (storedGetOnly.Count > 0)
        {
            foreach (var property in storedGetOnly)
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    DiagnosticDescriptors.TypeNotConstructible,
                    property.Locations.FirstOrDefault() ?? ctx.TargetNode.GetLocation(),
                    typeSymbol.Name,
                    "its get-only property '" + property.Name + "' stores a value (it has a backing field) but has no set or init accessor, so the generated Rehydrate cannot restore it and every read would return default. Add an init accessor, or compute the property from other properties."));
            }

            return new ColumnarTransformResult(null, new EquatableArray<DiagnosticInfo>([.. diagnostics]));
        }

        var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : typeSymbol.ContainingNamespace.ToDisplayString();
        var accessibility = typeSymbol.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            _ => "internal",
        };
        var typeKeyword = BuildTypeKeyword(typeSymbol);
        var parentTypes = StorageSourceGenerator.BuildParentTypes(typeSymbol);

        // Hint names must be unique per generator: the same type identity StorageSourceGenerator
        // uses (namespace, containing types with generic arity, name), so same-named records in
        // different scopes coexist.
        var hintIdentity = HintNames.Identity(ns, parentTypes, typeSymbol.Name);

        // Record class seeding for Rehydrate (static, so no `this` to clone): prefer a
        // parameterless constructor; otherwise call the constructor whose parameters best match
        // column properties by name and type (a positional record's primary constructor), passing
        // the decoded column value for each matched parameter.
        // BSSG025 — a required member the plan has no column for (a non-public property or field)
        // is never written to a segment, so a rehydrated row could only carry default for it.
        // Before the seed below set required members, `new T()` failed loudly with CS9035 on this
        // shape; reject it here, as the row path rejects required members its constructor leaves
        // unset, instead of letting the seed silence it.
        var requiredNotColumns = new List<string>();
        for (var t = typeSymbol; t is not null; t = t.BaseType)
        {
            foreach (var requiredMember in t.GetMembers())
            {
                if (requiredMember is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true }
                    && !columnCandidates.Contains(requiredMember.Name)
                    && !requiredNotColumns.Contains(requiredMember.Name))
                {
                    requiredNotColumns.Add(requiredMember.Name);
                }
            }
        }

        if (requiredNotColumns.Count > 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.TypeNotConstructible,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name,
                "its required members (" + string.Join(", ", requiredNotColumns) +
                ") are not columns (only public properties with a set or init accessor are stored), so the generated Rehydrate could only set them to default and their values would be lost. Make each one a public property, or remove 'required'."));
            return new ColumnarTransformResult(null, new EquatableArray<DiagnosticInfo>([.. diagnostics]));
        }

        var hasParameterlessConstructor = false;
        var seedConstructorArguments = new List<string>();
        IMethodSymbol? seedConstructor = null;
        var requiredMembersToSeed = new List<string>();
        if (!typeSymbol.IsValueType)
        {
            var fieldTypes = typeSymbol.GetMembers().OfType<IPropertySymbol>()
                .Where(p => fields.Any(f => f.PropertyName == p.Name))
                .ToDictionary(p => p.Name, p => p.Type, StringComparer.Ordinal);
            var candidates = typeSymbol.InstanceConstructors
                .Where(c => c.Parameters.All(p => p.RefKind is RefKind.None or RefKind.In)
                            && !(c.Parameters.Length == 1
                                 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, typeSymbol)))
                .ToList();
            hasParameterlessConstructor = candidates.Any(c => c.Parameters.Length == 0);
            if (!hasParameterlessConstructor)
            {
                string MatchColumn(IParameterSymbol parameter) =>
                    fieldTypes.FirstOrDefault(kv =>
                        string.Equals(kv.Key, parameter.Name, StringComparison.OrdinalIgnoreCase)
                        && SymbolEqualityComparer.Default.Equals(kv.Value, parameter.Type)).Key ?? "";

                var seed = candidates
                    .OrderByDescending(c => c.Parameters.Count(p => MatchColumn(p).Length > 0))
                    .ThenBy(c => c.Parameters.Length)
                    .FirstOrDefault();
                if (seed is not null)
                {
                    seedConstructorArguments.AddRange(seed.Parameters.Select(MatchColumn));
                    seedConstructor = seed;
                }
            }
            else
            {
                seedConstructor = candidates.First(c => c.Parameters.Length == 0);
            }

            // `new T(...)` on a type with required members is CS9035 unless the constructor
            // carries [SetsRequiredMembers]; the `with` assignments that follow do not count.
            // The seed therefore sets each required member to default in an object initializer,
            // and the column assignments overwrite them. Only columns are seeded: a required
            // member with no column was rejected above (BSSG025), never defaulted here.
            if (seedConstructor is not null
                && !seedConstructor.GetAttributes().Any(a =>
                    a.AttributeClass?.ToDisplayString() == SetsRequiredMembersAttributeFqn))
            {
                for (var t = typeSymbol; t is not null; t = t.BaseType)
                {
                    foreach (var requiredMember in t.GetMembers())
                    {
                        if (requiredMember is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true }
                            && fields.Any(f => f.PropertyName == requiredMember.Name)
                            && !requiredMembersToSeed.Contains(requiredMember.Name))
                        {
                            requiredMembersToSeed.Add(requiredMember.Name);
                        }
                    }
                }
            }
        }

        // When [StorageSerializable] is present, StorageSourceGenerator already emits
        // IProjectable<TSelf>.ProjectTo on the same partial — emitting it again causes CS0111.
        var hasStorageSerializable = typeSymbol.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == StorageSerializableAttributeFqn);

        // Detect user-owned IProjectable<TSelf>. Mirrors StorageSourceGenerator's skip check so
        // hand-implementers don't collide with generator-emitted ProjectTo (CS0111).
        var skipProjectable = hasStorageSerializable
            || (syntaxNode is not null
                && ExplicitlyImplementsInterface(ctx.SemanticModel, syntaxNode, IProjectableFqn));

        // Detect user-declared static Rehydrate(IReadOnlyDictionary<string, Array>, int).
        // When present the generator skips emission to avoid CS0111.
        var skipRehydratable = typeSymbol.GetMembers("Rehydrate")
            .OfType<IMethodSymbol>()
            .Any(m => m.IsStatic
                      && m.Parameters.Length == 2
                      && m.Parameters[0].Type.OriginalDefinition.ToDisplayString() ==
                         "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
                      && m.Parameters[1].Type.SpecialType == SpecialType.System_Int32);

        // Detect user-declared static CreateColumnSortComparer(ColumnSortSpec). When present the
        // generator skips emission so hand-rolled comparers (e.g., a custom IComparer<T> that
        // honours descending or culture-sensitive string ordering) win over the default emit.
        var skipSortComparer = typeSymbol.GetMembers("CreateColumnSortComparer")
            .OfType<IMethodSymbol>()
            .Any(m => m.IsStatic
                      && m.Parameters.Length == 1
                      && m.Parameters[0].Type.ToDisplayString() ==
                         "Boutquin.Storage.Domain.ValueObjects.TimeSeries.ColumnSortSpec");

        // BSSG025 — Rehydrate always seeds with `new TSelf(...)` (a record class) and ProjectTo
        // does when a parameterless constructor exists; on an abstract type either is CS0144.
        if (typeSymbol.IsAbstract && (!skipRehydratable || (!skipProjectable && hasParameterlessConstructor)))
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.TypeNotConstructible,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name,
                "it is abstract, and the generated Rehydrate creates the value with 'new'. Annotate a concrete type."));
            return new ColumnarTransformResult(null, new EquatableArray<DiagnosticInfo>([.. diagnostics]));
        }

        var plan = new ColumnarTypePlan(
            typeSymbol.Name,
            ns,
            accessibility,
            typeKeyword,
            skipProjectable,
            skipRehydratable,
            skipSortComparer,
            new EquatableArray<ColumnarFieldPlan>([.. fields]),
            new EquatableArray<ColumnarSortablePropertyPlan>([.. sortableProperties]),
            hintIdentity,
            new EquatableArray<ParentTypeInfo>([.. parentTypes]),
            hasParameterlessConstructor,
            new EquatableArray<string>([.. seedConstructorArguments]),
            new EquatableArray<string>([.. requiredMembersToSeed]));

        return new ColumnarTransformResult(plan, new EquatableArray<DiagnosticInfo>([.. diagnostics]));
    }

    // Syntax-level base-list check — mirrors StorageSourceGenerator.ExplicitlyImplementsInterface.
    // We look at the user-written base list so compiler-synthesised bases (e.g., on records) don't
    // false-positive. Matches both the fully-qualified generic form and the bare generic name.
    private static bool ExplicitlyImplementsInterface(
        SemanticModel semanticModel,
        SyntaxNode targetNode,
        string interfaceFqn)
    {
        if (targetNode is not TypeDeclarationSyntax tds || tds.BaseList is null)
        {
            return false;
        }

        foreach (var baseType in tds.BaseList.Types)
        {
            if (semanticModel.GetTypeInfo(baseType.Type).Type is not INamedTypeSymbol symbol)
            {
                continue;
            }

            var originalFqn = symbol.OriginalDefinition.ToDisplayString();
            if (originalFqn == interfaceFqn)
            {
                return true;
            }

            // Match the bare name exactly or the open-generic prefix "<", NOT a bare StartsWith.
            // A plain StartsWith would falsely match any user interface whose FQN merely extends
            // the target's as a prefix (e.g. "...IProjectableExtended" matching "...IProjectable"),
            // making the generator silently skip emitting the real interface implementation.
            var shortFqn = interfaceFqn.Replace("`1", string.Empty);
            if (originalFqn == shortFqn ||
                originalFqn.StartsWith(shortFqn + "<", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildTypeKeyword(INamedTypeSymbol type)
    {
        if (type.IsRecord && type.IsValueType)
        {
            return "record struct";
        }

        if (type.IsRecord)
        {
            return "record";
        }

        return type.IsValueType ? "struct" : "class";
    }

    private static ColumnEncoding? ReadColumnEncodingOverride(IPropertySymbol property)
    {
        foreach (var attr in property.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == ColumnEncodingAttributeFqn
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is int enumValue)
            {
                return (ColumnEncoding)enumValue;
            }
        }

        return null;
    }

    // ───── Defaults + compatibility ───────────────────────────────────

    // Sort-spec eligibility predicate. Restricted to scalar kinds whose CLR types
    // unconditionally implement IComparable in the BCL (Tier 1 numerics, Tier 2 non-numerics,
    // Tier 3 BCL primitives, enums). Nullable<T> is excluded so the emitted Compare lambda
    // never dereferences a null target — matches the existing reflection comparator's
    // nullability rejection. AnnotatedType / collections / Unsupported are excluded because
    // their orderability depends on user code; the segment-format reflection fallback handles
    // the long tail.
    private static bool IsSortable(EncodableTypeDescriptor descriptor) => descriptor.Kind switch
    {
        EncodableTypeKind.SByte
            or EncodableTypeKind.Byte
            or EncodableTypeKind.Int16
            or EncodableTypeKind.UInt16
            or EncodableTypeKind.Int32
            or EncodableTypeKind.UInt32
            or EncodableTypeKind.Int64
            or EncodableTypeKind.UInt64
            or EncodableTypeKind.Int128
            or EncodableTypeKind.UInt128
            or EncodableTypeKind.Half
            or EncodableTypeKind.Single
            or EncodableTypeKind.Double
            or EncodableTypeKind.Bool
            or EncodableTypeKind.Char
            or EncodableTypeKind.String
            or EncodableTypeKind.DateOnly
            or EncodableTypeKind.TimeOnly
            or EncodableTypeKind.TimeSpan
            or EncodableTypeKind.DateTimeOffset
            or EncodableTypeKind.Guid
            or EncodableTypeKind.Decimal
            or EncodableTypeKind.Enum => true,
        _ => false,
    };

    /// <summary>Default encoding per encodable-type kind; mirrors the type-default encoding table.</summary>
    private static ColumnEncoding? ChooseDefault(EncodableTypeDescriptor descriptor) => descriptor.Kind switch
    {
        EncodableTypeKind.DateOnly => ColumnEncoding.DeltaOfDeltaVlq,
        EncodableTypeKind.Int32 => ColumnEncoding.DeltaVlq,
        EncodableTypeKind.Int64 => ColumnEncoding.DeltaVlq,
        EncodableTypeKind.Decimal => ColumnEncoding.Dictionary,
        EncodableTypeKind.Double => ColumnEncoding.GorillaXor,
        // float columns ride the GorillaXor stream as zero-extended raw float bits — the
        // segment format widens/narrows by bit transport (no FP conversion), so the
        // round-trip is bit-exact including NaN payloads.
        EncodableTypeKind.Single => ColumnEncoding.GorillaXor,
        EncodableTypeKind.String => ColumnEncoding.Dictionary,
        EncodableTypeKind.Bool => ColumnEncoding.BitmapRle,
        // Enum columns store the underlying value widened to int64 on the DeltaVlq stream —
        // runs of equal enum values (the common low-cardinality shape) collapse into RLE runs.
        EncodableTypeKind.Enum => ColumnEncoding.DeltaVlq,
        _ => null,
    };

    /// <summary>
    /// Build-time compatibility matrix for <c>[ColumnEncoding]</c> overrides. Raises
    /// <c>BSSG007</c> on mismatch.
    /// </summary>
    private static bool IsCompatible(ColumnEncoding encoding, string clrType, EncodableTypeDescriptor descriptor) => encoding switch
    {
        ColumnEncoding.Dictionary => clrType is "string" or "System.String"
                                              or "decimal" or "System.Decimal",
        ColumnEncoding.BitmapRle => clrType is "bool" or "System.Boolean",
        ColumnEncoding.DeltaVlq => descriptor.Kind == EncodableTypeKind.Enum
                                   || clrType is "int" or "System.Int32"
                                              or "long" or "System.Int64",
        ColumnEncoding.DeltaOfDeltaVlq => clrType is "System.DateOnly",
        ColumnEncoding.GorillaXor => clrType is "double" or "System.Double"
                                             or "float" or "System.Single",
        ColumnEncoding.RawDouble => clrType is "double" or "System.Double",
        ColumnEncoding.ScaledIntegerBitPacked => clrType is "decimal" or "System.Decimal",
        _ => false,
    };

    private static string CompatibleTypesFor(ColumnEncoding encoding) => encoding switch
    {
        ColumnEncoding.Dictionary => "string, decimal",
        ColumnEncoding.BitmapRle => "bool",
        ColumnEncoding.DeltaVlq => "int, long, enums",
        ColumnEncoding.DeltaOfDeltaVlq => "DateOnly",
        ColumnEncoding.GorillaXor => "double, float",
        ColumnEncoding.RawDouble => "double",
        ColumnEncoding.ScaledIntegerBitPacked => "decimal",
        _ => "(unknown)",
    };

    // ───── Encoding enum mirror (kept internal; mirrors Domain's public enum) ─

    internal enum ColumnEncoding
    {
        Dictionary = 0,
        BitmapRle = 1,
        DeltaVlq = 2,
        DeltaOfDeltaVlq = 3,
        GorillaXor = 4,
        RawDouble = 5,
        ScaledIntegerBitPacked = 6,
    }
}
