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
    private const string TagAttributeFqn = "Boutquin.Storage.Domain.Attributes.TagAttribute";
    private const string ReservedTagAttributeFqn = "Boutquin.Storage.Domain.Attributes.ReservedTagAttribute";
    private const string DecimalScaleAttributeFqn = "Boutquin.Storage.Domain.Attributes.DecimalScaleAttribute";
    private const string PackedAttributeFqn = "Boutquin.Storage.Domain.Attributes.PackedAttribute";
    private const string UnpackedAttributeFqn = "Boutquin.Storage.Domain.Attributes.UnpackedAttribute";

    // Tag-and-wire-type varint packs the wire type in the low 3 bits; a tag number fitting in
    // the upper 28 bits keeps the combined varint ≤ 5 bytes. Enforced by BSSG012.
    private const int MaxTagNumber = (1 << 28) - 1;

    private const string ISerializableFqn = "Boutquin.Storage.Domain.Interfaces.ISerializable`1";
    private const string IComparableFqn = "System.IComparable`1";
    private const string IEquatableFqn = "System.IEquatable`1";
    private const string IProjectableFqn = "Boutquin.Storage.Domain.Interfaces.IProjectable`1";

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
            var emitted = new List<TypeToGenerate>();
            var recursive = new List<bool>();

            foreach (var result in keyResults.Concat(serializableResults))
            {
                // Report diagnostics
                foreach (var diag in result.Diagnostics)
                {
                    spc.ReportDiagnostic(diag.ToDiagnostic());
                }

                // Skip generation for any Error-severity property/key-shape diagnostic.
                // Continuing past these would emit code that either (a) fails to compile
                // (e.g. OrderedCodeGenerator on a decimal key with no scale, BSSG017), or
                // (b) silently drops the affected property at every round-trip — the exact
                // failure mode each diagnostic exists to prevent. The complete block list:
                //   BSSG002 TypeMustBePartial          — generator cannot extend a sealed declaration
                //   BSSG005 MutuallyExclusiveAttributes — [Key] + [StorageSerializable] on one type
                //   BSSG017 MissingDecimalScale         — decimal key without valid [DecimalScale]
                //   BSSG010 MissingTag                  — silent default-at-read drop
                //   BSSG011 DuplicateTag                — reader cannot dispatch on shared tag
                //   BSSG012 TagOutOfRange               — silent property drop on negative/overflow tag
                //   BSSG013 ReservedTagCollision        — schema-evolution invariant breach
                //   BSSG014 KeyNestedRecord             — ordered wire is flat-only
                //   BSSG015 KeyCollection               — variable-length data not byte-lex sortable
                //   BSSG016 KeyNestedNullable           — only leaf-level null discriminator allowed
                //   BSSG019 KeyNullableString           — strings have no null discriminator; flush-time throw
                //   BSSG001 UnsupportedPropertyType     — emitter has no codec; property silently skipped
                //   BSSG008 ExcludedPropertyType        — DateTime / nint / nuint silently skipped
                //   BSSG007 IncompatibleColumnEncoding  — codec wired to wrong CLR type
                //   BSSG018 ConflictingPackedUnpacked   — emitter falls through to Default encoding
                //   BSSG023 GenericTargetNotSupported   — partial would declare a separate non-generic type
                //   BSSG024 InheritedPropertiesNotSerialized — inherited properties / public fields silently dropped
                //   BSSG025 TypeNotConstructible        — Deserialize/ProjectTo would not compile
                if (result.Diagnostics.Any(d =>
                    d.Severity == DiagnosticSeverity.Error &&
                    (d.Id == RuleIdentifiers.TypeMustBePartial ||
                     d.Id == RuleIdentifiers.TypeNotConstructible ||
                     d.Id == TargetShapeDiagnostics.GenericTargetId ||
                     d.Id == TargetShapeDiagnostics.InheritedPropertiesId ||
                     d.Id == RuleIdentifiers.MutuallyExclusiveAttributes ||
                     d.Id == RuleIdentifiers.MissingDecimalScale ||
                     d.Id == RuleIdentifiers.MissingTag ||
                     d.Id == RuleIdentifiers.DuplicateTag ||
                     d.Id == RuleIdentifiers.TagOutOfRange ||
                     d.Id == RuleIdentifiers.ReservedTagCollision ||
                     d.Id == RuleIdentifiers.KeyNestedRecord ||
                     d.Id == RuleIdentifiers.KeyCollection ||
                     d.Id == RuleIdentifiers.KeyNestedNullable ||
                     d.Id == RuleIdentifiers.KeyNullableString ||
                     d.Id == RuleIdentifiers.UnsupportedPropertyType ||
                     d.Id == RuleIdentifiers.ExcludedPropertyType ||
                     d.Id == RuleIdentifiers.IncompatibleColumnEncoding ||
                     d.Id == RuleIdentifiers.ConflictingPackedUnpacked)))
                {
                    continue;
                }

                if (result.TypeToGenerate is { } typeToGenerate)
                {
                    emitted.Add(typeToGenerate);
                    recursive.Add(result.IsRecursive);
                }
            }

            var hintNames = HintNames.Assign(
                emitted.Select(t => HintNames.Identity(t.Namespace, t.ParentTypes, t.Name)).ToList(),
                ".g.cs");
            for (var i = 0; i < emitted.Count; i++)
            {
                TaggedWireEmitter.s_boundNestingDepth = recursive[i];
                try
                {
                    spc.AddSource(hintNames[i], CodeGenerator.Generate(emitted[i], defaultsConfig));
                }
                finally
                {
                    TaggedWireEmitter.s_boundNestingDepth = false;
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
        var hasKey = typeSymbol.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == KeyAttributeFqn);
        var hasStorageSerializable = typeSymbol.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == StorageSerializableAttributeFqn);
        if (hasKey && hasStorageSerializable)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.MutuallyExclusiveAttributes,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name));
            return new TransformResult(null, new EquatableArray<DiagnosticInfo>([.. diagnostics]));
        }

        // BSSG002: must be partial
        if (ctx.TargetNode is TypeDeclarationSyntax tds &&
            !tds.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.TypeMustBePartial,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name));
            return new TransformResult(null, new EquatableArray<DiagnosticInfo>([.. diagnostics]));
        }

        // BSSG026: every containing type must be partial too — the emitted code re-opens each.
        var nonPartialParents = TargetShapeDiagnostics.FindNonPartialContainingTypes(typeSymbol);
        if (nonPartialParents.Count > 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                TargetShapeDiagnostics.ContainingTypeNotPartial,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name,
                string.Join("', '", nonPartialParents)));
            return new TransformResult(null, new EquatableArray<DiagnosticInfo>([.. diagnostics]));
        }

        // BSSG023: generic targets. The emitted partial repeats the bare name, which would
        // declare a distinct non-generic type whose generated members target nothing.
        if (typeSymbol.TypeParameters.Length > 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                TargetShapeDiagnostics.GenericTargetNotSupported,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name));
            return new TransformResult(null, new EquatableArray<DiagnosticInfo>([.. diagnostics]));
        }

        // BSSG003: advisory — should be record struct
        var isRecordStruct = typeSymbol.IsValueType && typeSymbol.IsRecord;
        if (!isRecordStruct)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.TypeShouldBeRecordStruct,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name));
        }

        // BSSG024: public state that property discovery below cannot see — properties inherited
        // from a base type (discovery reads the type's own members only) and public instance
        // fields, declared or inherited (discovery reads properties only) — would round-trip to
        // default, and on a [Key] the generated CompareTo and bytes would ignore it while the
        // synthesized record equality does not. Reject, don't drop.
        //
        // A [Key] is its planned components and nothing else: equality-keyed engine maps
        // (tombstones) look a key up by equality while every ordered structure finds it by
        // CompareTo, so non-public instance state — a private field, the backing field of an
        // internal or private property — that CompareTo and the ordered bytes ignore but the
        // record's synthesized equality compares would make two keys that order equal unequal,
        // and a round trip unequal to its own input. A [StorageSerializable] value keeps its
        // non-public scratch state (no ordering invariant rests on it); a key rejects it.
        var inheritedProperties = TargetShapeDiagnostics.FindInheritedPublicProperties(typeSymbol, storedOnly: false);
        var publicFields = TargetShapeDiagnostics.FindPublicInstanceFields(typeSymbol);
        var nonPublicState = isKey
            ? TargetShapeDiagnostics.FindNonPublicInstanceState(typeSymbol)
            : new List<string>();
        if (inheritedProperties.Count > 0 || publicFields.Count > 0 || nonPublicState.Count > 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                TargetShapeDiagnostics.InheritedPropertiesNotSerialized,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name,
                TargetShapeDiagnostics.DescribeUnplannedState(inheritedProperties, publicFields, nonPublicState)));
        }

        // BSSG006: check for already-EXPLICITLY-implemented interfaces.
        // Use the syntax base list rather than AllInterfaces — record structs synthesize
        // IEquatable<T> automatically, which would cause false-positive BSSG006 on every
        // record struct. We only warn/skip when the user explicitly lists the interface
        // in the type declaration's base list.
        var skipSerializable = ExplicitlyImplementsInterface(ctx.SemanticModel, ctx.TargetNode, ISerializableFqn);
        var skipComparable = ExplicitlyImplementsInterface(ctx.SemanticModel, ctx.TargetNode, IComparableFqn);
        var skipEquatable = ExplicitlyImplementsInterface(ctx.SemanticModel, ctx.TargetNode, IEquatableFqn);
        // A user hand-implementing IProjectable<TSelf> silently supersedes the generator-emitted
        // body (escape hatch — mirrors ISerializable<T>'s skipSerializable). No BSSG006 is
        // raised because projection is an additive, post-v1 surface; warning every migrator
        // would be noisy and add no safety.
        var skipProjectable = ExplicitlyImplementsInterface(ctx.SemanticModel, ctx.TargetNode, IProjectableFqn);
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
        var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : typeSymbol.ContainingNamespace.ToDisplayString();

        // Extract accessibility
        var accessibility = typeSymbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";

        // Parse [ReservedTag(N)] attributes on the type itself. Used in tag-validation below
        // (BSSG013) to flag a [Tag] that collides with a previously-retired slot.
        var reservedTags = new HashSet<int>();
        foreach (var attr in typeSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == ReservedTagAttributeFqn &&
                attr.ConstructorArguments.Length == 1 &&
                attr.ConstructorArguments[0].Value is int reserved)
            {
                reservedTags.Add(reserved);
            }
        }

        // Extract properties
        var properties = new List<PropertyInfo>();
        var propertySymbols = new List<IPropertySymbol>();
        foreach (var prop in typeSymbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            // An indexer is not row state: it has no column name (its symbol name is `this[]`) and
            // no tag to declare. Every sibling discovery path (columnar, cycle walk, inherited-
            // property scan) skips it; without this the type is unannotatable (BSSG010/BSSG025).
            if (prop.IsStatic || prop.IsIndexer)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();

            var propInfo = ClassifyProperty(prop, diagnostics, typeSymbol.Name, ctx.TargetNode.GetLocation(), isKey);
            properties.Add(propInfo);
            propertySymbols.Add(prop);
        }

        // BSSG025: the emitted code must be able to construct the type. Deserialize ends with
        // `new T(<every property, declaration order>)`, and [StorageSerializable] ProjectTo seeds
        // its result with `this with { ... }`, which is valid only on records and structs, and
        // assigns every serialized property in a `with` initializer, which needs a set or init
        // accessor (CS0200 on a get-only property).
        var getOnlyProperties = propertySymbols.Where(p => p.SetMethod is null).Select(p => p.Name).ToList();
        var positionalConstructor = FindPositionalConstructor(typeSymbol, propertySymbols);
        // An abstract type has constructors (a positional abstract record keeps its protected
        // primary constructor), so the positional check alone accepts it, yet `new T(...)` on it
        // is CS0144.
        var notConstructibleReason =
            !skipSerializable && typeSymbol.IsAbstract
                ? "it is abstract, and the generated Deserialize creates the value with 'new'. Annotate a concrete type."
            : !skipSerializable && positionalConstructor is null
                ? "no constructor takes its public properties (" +
                  string.Join(", ", propertySymbols.Select(p => p.Type.ToDisplayString() + " " + p.Name)) +
                  ") positionally, in declaration order with matching parameter names, as the generated Deserialize requires. Use a positional record, or declare a matching constructor."
                : !skipSerializable && UnsetRequiredMembers(typeSymbol, positionalConstructor) is { Count: > 0 } unsetRequired
                    ? "its required members (" + string.Join(", ", unsetRequired) +
                      ") are not set by the positional constructor the generated Deserialize calls, which is CS9035 without [SetsRequiredMembers]. Mark that constructor [SetsRequiredMembers]."
                : !isKey && !skipProjectable && !typeSymbol.IsRecord && !typeSymbol.IsValueType
                    ? "it is a class that is not a record, and the generated ProjectTo rebuilds the value with a 'with' expression, which C# allows only on records and structs. Declare it as a record."
                    : !isKey && !skipProjectable && getOnlyProperties.Count > 0
                        ? "its properties (" + string.Join(", ", getOnlyProperties) +
                          ") have no set or init accessor, and the generated ProjectTo assigns every serialized property in a 'with' expression. Add an init accessor, or implement IProjectable<T> by hand."
                        : null;
        if (notConstructibleReason is not null)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.TypeNotConstructible,
                ctx.TargetNode.GetLocation(),
                typeSymbol.Name,
                notConstructibleReason));
        }

        // Tag validation — BSSG010 missing, BSSG011 duplicate, BSSG012 range,
        // BSSG013 reserved-collision. Runs after all property tags have been collected so the
        // "next unused tag" suggestion in BSSG010's message is globally correct.
        ValidateTags(properties, reservedTags, diagnostics, typeSymbol.Name, ctx.TargetNode.GetLocation());

        // Phase 3 [Key] shape constraints. Ordered wire is flat-only; nested records,
        // collections, and nested-nullable shapes all break byte-lex comparability.
        if (isKey)
        {
            ValidateKeyShape(properties, diagnostics, typeSymbol.Name, ctx.TargetNode.GetLocation());
        }

        // Extract parent types (for nested types)
        var parentTypes = BuildParentTypes(typeSymbol);

        // A [Key] record's equality is compiler-synthesized unless the user declares Equals(T) or
        // GetHashCode() themselves; the generator emits a CompareTo-aligned pair for records with
        // DateTimeOffset components (CodeGenerator), so a user-declared one must suppress it (CS0111).
        var userOwnsRecordEquality = typeSymbol.IsRecord && (
            typeSymbol.GetMembers("Equals").OfType<IMethodSymbol>().Any(m =>
                !m.IsImplicitlyDeclared && !m.IsStatic && m.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, typeSymbol)) ||
            typeSymbol.GetMembers("GetHashCode").OfType<IMethodSymbol>().Any(m =>
                !m.IsImplicitlyDeclared && !m.IsStatic && m.Parameters.Length == 0));

        // The record-class Equals(T?) the generator emits must be virtual unless the record is
        // sealed (CS8872 / CS0549), so a sealed record-class key repeats `sealed` on its partial,
        // where CodeGenerator reads it back. Repeating a declared modifier on a partial is legal.
        var declarationModifiers = isKey && typeSymbol.IsRecord && !typeSymbol.IsValueType && typeSymbol.IsSealed
            ? accessibility + " sealed"
            : accessibility;

        var typeToGenerate = new TypeToGenerate(
            Name: typeSymbol.Name,
            Namespace: ns,
            Accessibility: declarationModifiers,
            TypeKeyword: GetTypeKind(typeSymbol),
            IsKey: isKey,
            IsRecordStruct: isRecordStruct,
            SkipComparable: skipComparable,
            SkipEquatable: skipEquatable || userOwnsRecordEquality,
            SkipSerializable: skipSerializable,
            SkipProjectable: skipProjectable,
            Properties: new EquatableArray<PropertyInfo>([.. properties]),
            ReservedTags: new EquatableArray<int>([.. reservedTags.OrderBy(t => t)]),
            ParentTypes: new EquatableArray<ParentTypeInfo>([.. parentTypes]));

        // Only a recursive schema lets the bytes, not the schema, decide how deeply the codec
        // recurses; a [Key] is flat (BSSG014) and a hand-written ISerializable<T> owns its codec.
        var isRecursive = !isKey && !skipSerializable && IsOnSerializableCycle(typeSymbol);

        return new TransformResult(typeToGenerate, new EquatableArray<DiagnosticInfo>([.. diagnostics]), isRecursive);
    }

    // True when the type reaches itself through the members the tagged-wire codec recurses into:
    // a nested [StorageSerializable] record (optionally Nullable<T>) or a collection of them.
    // Directly or through other types (A → B → A). Such a schema puts no bound on a value's
    // nesting depth, so forged bytes can drive the generated reader arbitrarily deep. Cycles
    // never cross assemblies, so walking the declared members of source types is complete.
    private static bool IsOnSerializableCycle(INamedTypeSymbol root)
    {
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<INamedTypeSymbol>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var member in pending.Pop().GetMembers())
            {
                if (member is not IPropertySymbol { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public } prop
                    || NestedSerializableType(prop.Type) is not { } child)
                {
                    continue;
                }

                if (SymbolEqualityComparer.Default.Equals(child, root))
                {
                    return true;
                }

                if (visited.Add(child))
                {
                    pending.Push(child);
                }
            }
        }

        return false;
    }

    // The [StorageSerializable] record a property's codec recurses into, or null.
    private static INamedTypeSymbol? NestedSerializableType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }
        else if (type.SpecialType != SpecialType.System_String
                 && TryGetEnumerableElementType(type, out var element) && element is not null)
        {
            type = element;
        }

        return type is INamedTypeSymbol named
               && named.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == StorageSerializableAttributeFqn)
            ? named
            : null;
    }

    // The instance constructor that accepts the serialized properties positionally, or null: its
    // leading parameters have exactly the property types in declaration order, each named like
    // its property (ignoring case), and any further parameters are optional. That is the call
    // shape every Deserialize emitter produces; the name check stops a same-typed constructor in
    // another order from compiling and silently swapping values on every read.
    private static IMethodSymbol? FindPositionalConstructor(INamedTypeSymbol typeSymbol, List<IPropertySymbol> properties)
    {
        foreach (var ctor in typeSymbol.InstanceConstructors)
        {
            var parameters = ctor.Parameters;
            if (parameters.Length < properties.Count)
            {
                continue;
            }

            var matches = true;
            for (var i = 0; i < parameters.Length && matches; i++)
            {
                matches = i < properties.Count
                    ? parameters[i].RefKind == RefKind.None &&
                      SymbolEqualityComparer.Default.Equals(parameters[i].Type, properties[i].Type) &&
                      string.Equals(parameters[i].Name, properties[i].Name, StringComparison.OrdinalIgnoreCase)
                    : parameters[i].IsOptional || parameters[i].IsParams;
            }

            if (matches)
            {
                return ctor;
            }
        }

        return null;
    }

    // Required members (declared on the type or a base) that `new T(...)` through the given
    // constructor leaves unset — all of them unless it carries [SetsRequiredMembers].
    private static List<string> UnsetRequiredMembers(INamedTypeSymbol typeSymbol, IMethodSymbol? constructor)
    {
        var unset = new List<string>();
        if (constructor is null || constructor.GetAttributes().Any(a =>
                a.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute"))
        {
            return unset;
        }

        for (var t = typeSymbol; t is not null; t = t.BaseType)
        {
            foreach (var member in t.GetMembers())
            {
                if (member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true }
                    && !unset.Contains(member.Name))
                {
                    unset.Add(member.Name);
                }
            }
        }

        return unset;
    }

    // Containing-type chain of a nested target, outermost first, so an emitter can re-open each
    // parent partial around the generated declaration. Shared with ColumnarSourceGenerator.
    internal static List<ParentTypeInfo> BuildParentTypes(INamedTypeSymbol typeSymbol)
    {
        var parentTypes = new List<ParentTypeInfo>();
        var containingType = typeSymbol.ContainingType;
        while (containingType is not null)
        {
            var ptds = containingType.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as TypeDeclarationSyntax;
            var kind = GetTypeKind(containingType);
            var modifiers = ptds is not null
                ? string.Join(" ", ptds.Modifiers.Select(m => m.Text))
                : $"{(containingType.DeclaredAccessibility == Accessibility.Public ? "public" : "internal")} partial";
            var genericParams = containingType.TypeParameters.Length > 0
                ? "<" + string.Join(", ", containingType.TypeParameters.Select(tp => tp.Name)) + ">"
                : null;
            var constraints = BuildConstraints(containingType.TypeParameters);

            parentTypes.Add(new ParentTypeInfo(containingType.Name, kind, modifiers, genericParams, constraints));
            containingType = containingType.ContainingType;
        }
        parentTypes.Reverse(); // outermost first
        return parentTypes;
    }

    private static PropertyInfo ClassifyProperty(
        IPropertySymbol prop,
        List<DiagnosticInfo> diagnostics,
        string typeName,
        Location location,
        bool isKey)
    {
        var type = prop.Type;
        // Emitted code re-binds a type name inside the generated file's namespace scope, so every
        // name that flows into code is global::-qualified (keeping a nullable `?`); the display
        // form is kept for diagnostic text and the schema descriptor's declared-type text.
        var typeName2 = type.ToDisplayString(s_qualifiedConstraintTypeFormat);
        var declaredName = type.ToDisplayString();
        var tag = ReadTagNumber(prop);
        var decimalScale = ReadDecimalScale(prop);
        var collectionEncoding = ReadCollectionEncoding(prop, diagnostics, typeName, location);

        // BSSG019 — string? on a [Key]. The classifier's nullable unwrap only covers
        // Nullable<T> value types, so an annotated nullable reference string classifies as a
        // plain String: the record would compile clean, order in a memtable (null sorts first
        // under CompareStringByteLex), and fail only at flush time when Serialize rejects the
        // null. Detect the annotation here where the symbol is still in scope.
        if (isKey &&
            type.SpecialType == SpecialType.System_String &&
            type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.KeyNullableString,
                location,
                prop.Name, typeName));
            return new PropertyInfo(
                prop.Name, typeName2, TypeKind.Unsupported,
                EncodableTypeDescriptor.Unsupported(typeName2), null, tag, decimalScale, collectionEncoding);
        }

        // BSSG016 — Nullable<AnnotatedRecord> on a [Key]. The classifier's nullable unwrap
        // treats the annotated-record inner as Unsupported (no primitive kind), which loses
        // the "was-nullable" shape information downstream. Detect it here where the symbol
        // is still in scope and the containing type's [Key]-ness is known.
        if (isKey &&
            type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableSym &&
            IsAnnotatedType(nullableSym.TypeArguments[0]))
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.KeyNestedNullable,
                location,
                prop.Name, typeName, declaredName));
            return new PropertyInfo(
                prop.Name, typeName2, TypeKind.Unsupported,
                EncodableTypeDescriptor.Unsupported(typeName2), null, tag, decimalScale, collectionEncoding);
        }

        // BSSG008 excluded types (DateTime / nint / nuint) — targeted remediation message
        // before falling back to BSSG001's generic "unsupported type" copy.
        if (TypeClassifier.IsExplicitlyExcluded(type, out var remediation))
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.ExcludedPropertyType,
                location,
                prop.Name, typeName, declaredName, remediation));
            return new PropertyInfo(
                prop.Name, typeName2, TypeKind.Unsupported,
                EncodableTypeDescriptor.Unsupported(typeName2), null, tag, decimalScale, collectionEncoding);
        }

        // Tier 1–4, 6 via the shared classifier. Anything that resolves to a non-Unsupported
        // kind becomes a Primitive for the driver — the Descriptor carries the actual kind
        // used by the emitters.
        var descriptor = TypeClassifier.Classify(type) with { FullyQualifiedTypeName = typeName2 };
        if (descriptor.Kind != EncodableTypeKind.Unsupported)
        {
            return new PropertyInfo(prop.Name, typeName2, TypeKind.Primitive, descriptor, null, tag, decimalScale, collectionEncoding);
        }

        // Tier 5a — annotated record types recurse into their generated Serialize/Deserialize.
        // The annotated name drops a nullable reference annotation (`Child?` → `Child`): it is
        // the receiver of the static `Deserialize` call. TypeName keeps the `?`, which the
        // tagged-wire writer reads as "absent tag ⇒ null".
        if (IsAnnotatedType(type))
        {
            var annotatedName = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(s_qualifiedConstraintTypeFormat);
            return new PropertyInfo(
                prop.Name, typeName2, TypeKind.AnnotatedType,
                new EncodableTypeDescriptor(
                    EncodableTypeKind.AnnotatedType, typeName2, annotatedName, null, null, null),
                annotatedName, tag, decimalScale, collectionEncoding, IsSelfFramingChild(type),
                DefaultValueExpression(type, $"this.{IdentifierEscaper.Escape(prop.Name)}"));
        }

        // Tier 5b — IEnumerable<T> of a supported element kind.
        if (TryGetEnumerableElementType(type, out var elementType) && elementType is not null)
        {
            var elementTypeName = elementType.ToDisplayString(s_qualifiedConstraintTypeFormat);

            // The tagged-wire Deserialize decodes a collection into a local typed IEnumerable<T>
            // and passes it to the constructor, so only a property declared as IEnumerable<T>
            // itself compiles; List<T>, IReadOnlyList<T>, HashSet<T>, … would emit CS1503 inside
            // the generated file. Nullable collections cannot round-trip either: absent tags
            // read as empty collections, and the wire has no collection-null discriminator.
            // [Key] keeps classifying them as collections so BSSG015 names
            // the real reason (no collection is allowed in a key).
            if (!isKey && (type.OriginalDefinition.SpecialType != SpecialType.System_Collections_Generic_IEnumerable_T
                || type.NullableAnnotation == NullableAnnotation.Annotated))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    DiagnosticDescriptors.UnsupportedPropertyType,
                    location,
                    prop.Name, typeName, declaredName));
                return new PropertyInfo(
                    prop.Name, typeName2, TypeKind.Unsupported,
                    EncodableTypeDescriptor.Unsupported(typeName2), null, tag, decimalScale, collectionEncoding);
            }

            // BSSG008 check on the element type too — "IEnumerable<DateTime>" goes through
            // the targeted-remediation path instead of the generic BSSG001.
            if (TypeClassifier.IsExplicitlyExcluded(elementType, out var elemRemediation))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    DiagnosticDescriptors.ExcludedPropertyType,
                    location,
                    prop.Name, typeName, elementType.ToDisplayString(), elemRemediation));
                return new PropertyInfo(
                    prop.Name, typeName2, TypeKind.Unsupported,
                    EncodableTypeDescriptor.Unsupported(typeName2), null, tag, decimalScale, collectionEncoding);
            }

            var elementDescriptor = TypeClassifier.Classify(elementType);

            // Nullable collection elements (IEnumerable<int?>, IEnumerable<string?>, …) are not supported:
            // the tagged-wire collection emitter has no per-element null discriminator. Reaching the
            // emitter with ElementKind == Nullable previously threw at code-gen time, which Roslyn
            // surfaces as a CS8785 generator-crash WARNING (silent under default settings, zero code
            // emitted for the whole record). Reject loudly at transform time via BSSG001 — which is in
            // the driver's error block-list — so the record is skipped with a real diagnostic instead
            // of silently miscompiling. (Same class as the fixed Nullable<enum> scalar bug.)
            if (elementDescriptor.Kind == EncodableTypeKind.Nullable
                || (elementType.IsReferenceType && elementType.NullableAnnotation == NullableAnnotation.Annotated))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    DiagnosticDescriptors.UnsupportedPropertyType,
                    location,
                    prop.Name, typeName, declaredName));
                return new PropertyInfo(
                    prop.Name, typeName2, TypeKind.Unsupported,
                    EncodableTypeDescriptor.Unsupported(typeName2), null, tag, decimalScale, collectionEncoding);
            }

            if (elementDescriptor.Kind != EncodableTypeKind.Unsupported)
            {
                var collectionDescriptor = new EncodableTypeDescriptor(
                    Kind: elementDescriptor.Kind == EncodableTypeKind.Enum
                        ? EncodableTypeKind.EnumCollection
                        : EncodableTypeKind.PrimitiveCollection,
                    FullyQualifiedTypeName: typeName2,
                    AnnotatedTypeName: null,
                    ElementKind: elementDescriptor.Kind,
                    EnumUnderlying: elementDescriptor.EnumUnderlying,
                    NullableInner: elementDescriptor.NullableInner);
                return new PropertyInfo(
                    prop.Name, typeName2, TypeKind.PrimitiveCollection,
                    collectionDescriptor, elementTypeName, tag, decimalScale, collectionEncoding);
            }
            if (IsAnnotatedType(elementType))
            {
                return new PropertyInfo(
                    prop.Name, typeName2, TypeKind.AnnotatedCollection,
                    new EncodableTypeDescriptor(
                        EncodableTypeKind.AnnotatedCollection, typeName2, elementTypeName,
                        null, null, null),
                    elementTypeName, tag, decimalScale, collectionEncoding, IsSelfFramingChild(elementType));
            }

            // Unsupported element type in IEnumerable<T>
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.UnsupportedPropertyType,
                location,
                prop.Name, typeName, declaredName));
            return new PropertyInfo(
                prop.Name, typeName2, TypeKind.Unsupported,
                EncodableTypeDescriptor.Unsupported(typeName2), null, tag, decimalScale, collectionEncoding);
        }

        // Fallback — BSSG001 generic unsupported type.
        diagnostics.Add(DiagnosticInfo.Create(
            DiagnosticDescriptors.UnsupportedPropertyType,
            location,
            prop.Name, typeName, declaredName));
        return new PropertyInfo(
            prop.Name, typeName2, TypeKind.Unsupported,
            EncodableTypeDescriptor.Unsupported(typeName2), null, tag, decimalScale, collectionEncoding);
    }

    // Positional-record primary constructor params emit `[property: Tag(N)]` which lands as
    // an attribute on the *property*. Hand-written get/set properties declared `[Tag(N)]`
    // directly on the member line are also property-level. Either way the attribute ends up
    // visible on IPropertySymbol.GetAttributes() — no parameter-walk is needed here.
    //
    // Returns null when no [Tag] was declared (BSSG010 path). Returns the declared number
    // otherwise, including 0 and negatives (BSSG012 range check is downstream).
    private static int? ReadTagNumber(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == TagAttributeFqn &&
                attr.ConstructorArguments.Length == 1 &&
                attr.ConstructorArguments[0].Value is int number)
            {
                return number;
            }
        }
        return null;
    }

    // Phase A2 — read [DecimalScale(N)] off a property. Returns null when absent. Out-of-range
    // values are returned as-is so the downstream validator (BSSG017) can emit a precise
    // diagnostic; the runtime helper (Helpers.OrderedByteHelpers.WriteDecimal) also enforces
    // the [0, 28] range as defence-in-depth.
    private static int? ReadDecimalScale(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == DecimalScaleAttributeFqn &&
                attr.ConstructorArguments.Length == 1 &&
                attr.ConstructorArguments[0].Value is int scale)
            {
                return scale;
            }
        }
        return null;
    }

    // Phase F2 — resolve the collection-encoding selector from [Packed]/[Unpacked] attributes.
    // Applying both emits BSSG018 and falls through to Default (i.e., let the element-kind
    // default stand so the generated code still compiles and exercises some defined behaviour
    // — the diagnostic is the real failure signal, not the chosen encoding).
    private static CollectionEncoding ReadCollectionEncoding(
        IPropertySymbol prop,
        List<DiagnosticInfo> diagnostics,
        string typeName,
        Location location)
    {
        var hasPacked = false;
        var hasUnpacked = false;
        foreach (var attr in prop.GetAttributes())
        {
            var fqn = attr.AttributeClass?.ToDisplayString();
            if (fqn == PackedAttributeFqn)
            {
                hasPacked = true;
            }
            else if (fqn == UnpackedAttributeFqn)
            {
                hasUnpacked = true;
            }
        }

        if (hasPacked && hasUnpacked)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.ConflictingPackedUnpacked,
                location,
                prop.Name, typeName));
            return CollectionEncoding.Default;
        }

        if (hasPacked)
        {
            return CollectionEncoding.Packed;
        }

        if (hasUnpacked)
        {
            return CollectionEncoding.Unpacked;
        }

        return CollectionEncoding.Default;
    }

    // [Key]-shape constraints. Runs only for [Key] types; [StorageSerializable]
    // freely accepts nested, collection, and nullable-nested shapes via tagged wire.
    //
    // Rules:
    //   BSSG014 — nested [StorageSerializable] inside a [Key].
    //   BSSG015 — IEnumerable<T> property on a [Key].
    //   BSSG016 — nullable-of-annotated-record on a [Key] (emitted at ClassifyProperty time
    //             when the symbol is still in scope; the driver has the info needed there).
    private static void ValidateKeyShape(
        List<PropertyInfo> properties,
        List<DiagnosticInfo> diagnostics,
        string typeName,
        Location location)
    {
        foreach (var prop in properties)
        {
            if (prop.Kind == TypeKind.Unsupported)
            {
                continue;
            }

            switch (prop.Kind)
            {
                case TypeKind.AnnotatedType:
                    diagnostics.Add(DiagnosticInfo.Create(
                        DiagnosticDescriptors.KeyNestedRecord,
                        location,
                        prop.Name, typeName, CodeGenerator.WithoutGlobalQualifier(prop.TypeName)));
                    break;
                case TypeKind.PrimitiveCollection:
                case TypeKind.AnnotatedCollection:
                    diagnostics.Add(DiagnosticInfo.Create(
                        DiagnosticDescriptors.KeyCollection,
                        location,
                        prop.Name, typeName, CodeGenerator.WithoutGlobalQualifier(prop.TypeName)));
                    break;
            }

            // BSSG017 — decimal keys MUST declare [DecimalScale(N)] with 0 <= N <= 28.
            // Applies to both decimal and Nullable<decimal> key properties; matches when
            // the descriptor kind resolves to Decimal either directly or via NullableInner.
            if (prop.Kind == TypeKind.Primitive && IsDecimalProperty(prop))
            {
                var scale = prop.DecimalScale;
                if (scale is null || scale < 0 || scale > 28)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        DiagnosticDescriptors.MissingDecimalScale,
                        location,
                        prop.Name, typeName));
                }
            }
        }
    }

    private static bool IsDecimalProperty(PropertyInfo prop) =>
        prop.Descriptor.Kind == EncodableTypeKind.Decimal ||
        (prop.Descriptor.Kind == EncodableTypeKind.Nullable &&
         prop.Descriptor.NullableInner == EncodableTypeKind.Decimal);

    // Consolidated tag validation. Runs once per type after every property has been classified
    // so BSSG010's "suggested next tag" sees the full used-tag set globally.
    //
    // Rules:
    //   BSSG010 — every property MUST have [Tag(N)] (Tag == null means "not declared").
    //   BSSG011 — two properties cannot share the same positive tag.
    //   BSSG012 — declared tag must be 1..2^28-1; tag 0 is reserved for the schema-format version byte.
    //   BSSG013 — tag cannot reuse a value listed in [ReservedTag(N)] on the same type.
    private static void ValidateTags(
        List<PropertyInfo> properties,
        HashSet<int> reservedTags,
        List<DiagnosticInfo> diagnostics,
        string typeName,
        Location location)
    {
        // First pass: BSSG012 (range) + collect usage for BSSG011 / BSSG013. Track
        // every declared tag — including out-of-range values — so the BSSG010 "next
        // unused tag" suggestion in the second pass never collides with a likely-typo'd
        // declared value.
        var tagToProperty = new Dictionary<int, string>();
        var usedTags = new HashSet<int>();
        var declaredTags = new HashSet<int>();
        foreach (var prop in properties)
        {
            if (prop.Kind == TypeKind.Unsupported || prop.Tag is null)
            {
                continue; // BSSG010 handled in second pass below.
            }

            var tag = prop.Tag.Value;
            declaredTags.Add(tag);
            if (tag < 1 || tag > MaxTagNumber)
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    DiagnosticDescriptors.TagOutOfRange,
                    location,
                    prop.Name, typeName, tag));
                continue;
            }

            if (tagToProperty.TryGetValue(tag, out var prior))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    DiagnosticDescriptors.DuplicateTag,
                    location,
                    prior, prop.Name, typeName, tag));
            }
            else
            {
                tagToProperty[tag] = prop.Name;
            }

            if (reservedTags.Contains(tag))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    DiagnosticDescriptors.ReservedTagCollision,
                    location,
                    prop.Name, typeName, tag));
            }

            usedTags.Add(tag);
        }

        // Second pass: BSSG010 — every property without a [Tag] attribute gets the lowest
        // unused positive tag suggested as remediation. "Unused" respects both sibling tags
        // AND reserved tags so the suggestion never steps on a retired slot.
        var nextSuggestion = 1;
        foreach (var prop in properties)
        {
            if (prop.Kind == TypeKind.Unsupported || prop.Tag is not null)
            {
                continue;
            }

            while (usedTags.Contains(nextSuggestion)
                || reservedTags.Contains(nextSuggestion)
                || declaredTags.Contains(nextSuggestion))
            {
                nextSuggestion++;
            }

            diagnostics.Add(DiagnosticInfo.Create(
                DiagnosticDescriptors.MissingTag,
                location,
                prop.Name, typeName, nextSuggestion));
            usedTags.Add(nextSuggestion); // Reserve the suggested slot for subsequent suggestions.
            nextSuggestion++;
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

    // Value equality can collapse representations that the value wire preserves (negative zero,
    // decimal scale, offsets), and a user-written Equals can ignore arbitrary stored state.
    // Inspect the same public properties the serializer writes instead of invoking that equality.
    private static string DefaultValueExpression(ITypeSymbol type, string value)
    {
        if (type.IsReferenceType)
        {
            return $"{value} is null";
        }

        var descriptor = TypeClassifier.Classify(type);
        switch (descriptor.Kind)
        {
            case EncodableTypeKind.Nullable:
                return $"!{value}.HasValue";
            case EncodableTypeKind.Double:
                return $"global::System.BitConverter.DoubleToInt64Bits({value}) == 0";
            case EncodableTypeKind.Single:
                return $"global::System.BitConverter.SingleToInt32Bits({value}) == 0";
            case EncodableTypeKind.Half:
                return $"global::System.BitConverter.HalfToUInt16Bits({value}) == 0";
            case EncodableTypeKind.Decimal:
                return $"({value} == 0m && global::System.Decimal.GetBits({value})[3] == 0)";
            case EncodableTypeKind.DateTimeOffset:
                return $"({value}.Ticks == 0 && {value}.Offset == default)";
        }

        if (IsAnnotatedType(type))
        {
            var tests = type.GetMembers().OfType<IPropertySymbol>()
                .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && !p.IsIndexer)
                .Select(p => DefaultValueExpression(p.Type, $"{value}.{IdentifierEscaper.Escape(p.Name)}"));
            var predicate = string.Join(" && ", tests.Select(test => $"({test})"));
            return predicate.Length == 0 ? "true" : predicate;
        }

        return $"{value} == default({type.ToDisplayString(s_qualifiedConstraintTypeFormat)})";
    }

    // A nested child's Serialize writes its own length prefix only when this generator emits it
    // for a [StorageSerializable] type. A [Key] child writes the flat ordered wire, and a
    // hand-implemented ISerializable<T> (anywhere in the interface set) makes no framing promise,
    // so the tagged-wire parent must length-frame either one itself. Framing a child that did
    // self-frame would still decode; leaving an unframed child unframed is what breaks skipping.
    private static bool IsSelfFramingChild(ITypeSymbol type) =>
        type is INamedTypeSymbol named
        && named.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == StorageSerializableAttributeFqn)
        && !named.AllInterfaces.Any(i =>
            i.OriginalDefinition.ContainingNamespace.ToDisplayString() + "." + i.OriginalDefinition.MetadataName == ISerializableFqn);

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

        // Named types that implement IEnumerable<T> (List<T>, HashSet<T>, …). Arrays are not
        // INamedTypeSymbol and fall through to BSSG001.
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
            if (typeInfo.Type is not INamedTypeSymbol symbol)
            {
                continue;
            }

            var originalFqn = symbol.OriginalDefinition.ToDisplayString();
            if (originalFqn == interfaceFqn)
            {
                return true;
            }

            // Also match the generic form like "System.IEquatable<T>" vs the metadata name
            // "System.IEquatable`1". Match either the bare name exactly or the open-generic prefix
            // "<", NOT a bare StartsWith — a plain StartsWith would falsely match any user interface
            // whose FQN merely extends the target's as a prefix (e.g. "...ISerializableExtended"
            // matching "...ISerializable"), causing the generator to silently skip emitting the
            // real interface implementation.
            var shortFqn = interfaceFqn.Replace("`1", "");
            if (originalFqn == shortFqn ||
                originalFqn.StartsWith(shortFqn + "<", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static StorageDefaultsConfig TransformDefaults(GeneratorAttributeSyntaxContext ctx)
    {
        var generateOperators = true;
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
        // A containing interface is re-opened as an interface: partial declarations of one type
        // must all use the same kind (CS0261).
        if (type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface)
        {
            return "interface";
        }

        if (type.IsRecord)
        {
            return type.IsValueType ? "record struct" : "record class";
        }
        return type.IsValueType ? "struct" : "class";
    }

    private static readonly SymbolDisplayFormat s_qualifiedConstraintTypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

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
            // Partial declarations must agree on every constraint, and `class?` is a different
            // constraint from `class` under an enabled nullable context (CS0265).
            if (tp.HasReferenceTypeConstraint)
            {
                constraints.Add(tp.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated
                    ? "class?"
                    : "class");
            }

            // Roslyn sets HasValueTypeConstraint = true for BOTH `struct` and `unmanaged`.
            // Emit "struct" only when unmanaged is NOT the operative constraint — otherwise
            // the generated output reads `where T : struct, unmanaged` and fails CS0449
            // (cannot combine 'class', 'struct', 'unmanaged', 'notnull', 'default').
            if (tp.HasValueTypeConstraint && !tp.HasUnmanagedTypeConstraint)
            {
                constraints.Add("struct");
            }

            if (tp.HasUnmanagedTypeConstraint)
            {
                constraints.Add("unmanaged");
            }

            // Same shape: HasNotNullConstraint is also raised by `class` (reference-type
            // constraint implies not-null). Emit "notnull" only when there is no
            // reference-type constraint, otherwise the output reads `where T : class, notnull`
            // which fails CS0449.
            if (tp.HasNotNullConstraint && !tp.HasReferenceTypeConstraint)
            {
                constraints.Add("notnull");
            }

            // global::-qualified: the generated file carries no using directives, so a
            // display-form name binds by namespace lookup from the target's namespace and can
            // resolve to a different (shadowing) type. The nullable modifier must survive too.
            foreach (var ct in tp.ConstraintTypes)
            {
                constraints.Add(ct.ToDisplayString(s_qualifiedConstraintTypeFormat));
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

}

// Hint names for both generators. Roslyn requires a hint name to be unique within a generator,
// compares hint names case-insensitively, and throws on a duplicate — the exception aborts the
// generator's whole output step, dropping the generated code of every annotated type in the
// compilation. So the name must be unique per type identity, not per display path.
internal static class HintNames
{
    // Namespace, each containing type with its generic arity in metadata form (`Outer`1`), and
    // the type name: unique per C# type identity under ordinal comparison. Arity matters because
    // `Outer` and `Outer<T>` are distinct types that may each nest a same-named target.
    public static string Identity(string? ns, IEnumerable<ParentTypeInfo> parents, string name)
    {
        var parts = new List<string>();
        if (ns is not null)
        {
            parts.Add(ns);
        }

        foreach (var parent in parents)
        {
            parts.Add(parent.GenericParameters is null
                ? parent.Name
                : parent.Name + "`" + (parent.GenericParameters.Count(c => c == ',') + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        parts.Add(name);
        return string.Join(".", parts);
    }

    // Final hint names, index-aligned with identities. Identities that collide only by case
    // (`P.Row` / `P.row`) each get a "-N" suffix by ordinal rank within their group; '-' cannot
    // occur in an identity, so a suffixed name never equals another name. Identities without a
    // case-insensitive twin keep their plain name.
    public static string[] Assign(IReadOnlyList<string> identities, string suffix)
    {
        var names = new string[identities.Count];
        var groups = Enumerable.Range(0, identities.Count)
            .GroupBy(i => identities[i], StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var distinct = group.Select(i => identities[i]).Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            foreach (var i in group)
            {
                names[i] = distinct.Count == 1
                    ? identities[i] + suffix
                    : identities[i] + "-" + (distinct.IndexOf(identities[i]) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix;
            }
        }

        return names;
    }
}

// Target-shape diagnostics shared by StorageSourceGenerator and ColumnarSourceGenerator. Each
// rejects a declaration shape that the emitters would otherwise turn into uncompilable code
// or a silently lossy wire plan; each is Error-severity and blocks emission for the type.
internal static class TargetShapeDiagnostics
{
    public const string GenericTargetId = "BSSG023";
    public const string InheritedPropertiesId = "BSSG024";
    public const string ContainingTypeNotPartialId = "BSSG026";

    private const string Category = "Boutquin.Storage.SourceGenerator";

    public static readonly DiagnosticDescriptor GenericTargetNotSupported = new(
        id: GenericTargetId,
        title: "Generic annotated types are not supported",
        messageFormat: "Type '{0}' annotated with [Key], [StorageSerializable] or [ColumnarStorable] is generic. Generic annotated types are not supported: the generated partial would declare a separate non-generic type. Declare a non-generic type (a generic containing type is supported).",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generated partial declaration repeats the type name without its type parameters, so it would introduce a distinct non-generic type whose generated members reference properties and constructors that do not exist. The type is rejected and no code is emitted for it.");

    public static readonly DiagnosticDescriptor InheritedPropertiesNotSerialized = new(
        id: InheritedPropertiesId,
        title: "Instance state outside the generated plan is not serialized",
        messageFormat: "Type '{0}' has instance state the generator does not plan: {1}. The generator plans only the public instance properties the annotated type declares itself, so this state would be silently dropped. Declare every persisted value as a public property on '{0}' directly, and keep a [Key] free of other instance state.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Member discovery reads the annotated type's own public instance properties. A public instance property inherited from a base type, or a public instance field (declared on the type or inherited), is absent from the wire plan (or columnar plan) and would read back as default(T) after every round trip; on a [Key] the generated comparison and ordered bytes would also disagree with the record's synthesized equality. A [Key] also rejects non-public instance state (a private field, the backing field of a non-public property) for the same reason. The type is rejected and no code is emitted for it.");

    public static readonly DiagnosticDescriptor ContainingTypeNotPartial = new(
        id: ContainingTypeNotPartialId,
        title: "Containing type of an annotated type must be partial",
        messageFormat: "Type '{0}' annotated with [Key], [StorageSerializable] or [ColumnarStorable] is nested in '{1}', which is not declared 'partial'. The generated code re-opens every containing type to add the nested members; declare each containing type 'partial'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The generator emits a nested annotated type's members inside a re-declaration of each of its containing types. A containing type that is not partial would be declared a second time (CS0101 inside the generated file). The type is rejected and no code is emitted for it.");

    // Containing types of a nested target, innermost first, with any declaration lacking the
    // 'partial' modifier — each would be re-declared, not re-opened, by the generated code.
    public static List<string> FindNonPartialContainingTypes(INamedTypeSymbol type)
    {
        var names = new List<string>();
        for (var containing = type.ContainingType; containing is not null; containing = containing.ContainingType)
        {
            if (containing.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax())
                .OfType<TypeDeclarationSyntax>()
                .Any(d => !d.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))))
            {
                names.Add(containing.Name);
            }
        }

        return names;
    }

    // Public instance fields of a type and its base types — persisted state that property
    // discovery never sees. Constants and static fields are type state, not instance state, and
    // compiler-generated backing fields are never public.
    public static List<string> FindPublicInstanceFields(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<string>();
        for (var t = type; t is not null; t = t.BaseType)
        {
            foreach (var field in t.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.DeclaredAccessibility == Accessibility.Public
                    && !field.IsStatic
                    && !field.IsConst
                    && !field.IsImplicitlyDeclared
                    && seen.Add(field.Name))
                {
                    fields.Add(field.Name);
                }
            }
        }

        return fields;
    }

    // Non-public instance state of a type and its base types: every instance field that is not
    // the compiler backing field of a public property, named by its property when it backs a
    // non-public one. Constants and static fields are type state. A base type compiled into
    // another assembly does not expose its private fields, so only source-visible state is seen.
    public static List<string> FindNonPublicInstanceState(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var state = new List<string>();
        for (var t = type; t is not null; t = t.BaseType)
        {
            foreach (var field in t.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic || field.IsConst || field.DeclaredAccessibility == Accessibility.Public)
                {
                    continue;
                }

                var name = field.AssociatedSymbol switch
                {
                    IPropertySymbol { DeclaredAccessibility: Accessibility.Public } => null,
                    IPropertySymbol backed => backed.Name,
                    _ when field.IsImplicitlyDeclared => null,
                    _ => field.Name,
                };
                if (name is not null && seen.Add(name))
                {
                    state.Add(name);
                }
            }
        }

        return state;
    }

    // The {1} argument of BSSG024: which unplanned members a type carries, by kind.
    public static string DescribeUnplannedState(List<string> inheritedProperties, List<string> publicFields)
        => DescribeUnplannedState(inheritedProperties, publicFields, new List<string>());

    public static string DescribeUnplannedState(
        List<string> inheritedProperties,
        List<string> publicFields,
        List<string> nonPublicState)
    {
        var parts = new List<string>();
        if (inheritedProperties.Count > 0)
        {
            parts.Add("properties inherited from a base type (" + string.Join(", ", inheritedProperties) + ")");
        }

        if (publicFields.Count > 0)
        {
            parts.Add("public fields (" + string.Join(", ", publicFields) + ")");
        }

        if (nonPublicState.Count > 0)
        {
            parts.Add("non-public instance state on a [Key], which the generated CompareTo and ordered bytes ignore but the type's equality compares (" + string.Join(", ", nonPublicState) + ")");
        }

        return string.Join("; ", parts);
    }

    // Public instance properties a type inherits but does not itself declare (an override or a
    // hiding redeclaration on the type counts as declared). storedOnly restricts the result to
    // stored state — properties with a setter, and get-only properties that own a backing field
    // (`{ get; }`, `get => field`), which a constructor assigns exactly as on a declared property.
    // Only a computed getter is derived state the columnar plan may exclude; a base type compiled
    // into another assembly does not expose its private backing fields, so its get-only
    // properties count as stored (fail closed).
    public static List<string> FindInheritedPublicProperties(INamedTypeSymbol type, bool storedOnly)
    {
        var seen = new HashSet<string>(
            type.GetMembers().OfType<IPropertySymbol>().Select(p => p.Name),
            StringComparer.Ordinal);
        var inherited = new List<string>();
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            foreach (var prop in baseType.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.DeclaredAccessibility != Accessibility.Public || prop.IsStatic || prop.IsIndexer)
                {
                    continue;
                }

                if (storedOnly && prop.SetMethod is null && IsComputedGetter(baseType, prop))
                {
                    continue;
                }

                if (seen.Add(prop.Name))
                {
                    inherited.Add(prop.Name);
                }
            }
        }

        return inherited;
    }

    // An auto-property (`{ get; }`) or a `field`-keyword property owns a compiler-synthesized
    // backing field whose AssociatedSymbol is the property; a computed getter owns none. Only a
    // source declaration can show that absence — metadata omits private members.
    public static bool HasBackingField(INamedTypeSymbol type, IPropertySymbol property)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is IFieldSymbol field
                && SymbolEqualityComparer.Default.Equals(field.AssociatedSymbol, property))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsComputedGetter(INamedTypeSymbol declaringType, IPropertySymbol property)
        => !property.DeclaringSyntaxReferences.IsEmpty && !HasBackingField(declaringType, property);
}

internal readonly record struct TransformResult(
    TypeToGenerate? TypeToGenerate,
    EquatableArray<DiagnosticInfo> Diagnostics,
    // [StorageSerializable] only: the type's schema reaches itself (see IsOnSerializableCycle),
    // so its tagged-wire reader and writer are emitted with a nesting-depth bound.
    bool IsRecursive = false)
{
    public static readonly TransformResult Empty = new(null, EquatableArray<DiagnosticInfo>.Empty);
}
