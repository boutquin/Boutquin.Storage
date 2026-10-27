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
/// BSSG021 — flags an allocation whose size derives from a decoded (untrusted) length that was not
/// bounded against the bytes actually remaining on the same decode path before the allocation runs.
/// </summary>
/// <remarks>
/// <para>
/// The defect class this guards recurred eleven times across the v2.0.0 storage-hardening review:
/// a codec / segment format / footer reader / SSTable block decoder / serializer reads a length or
/// count out of a byte payload (footer field, file header, length-prefix, varint frame) and uses it
/// directly as <c>new T[n]</c> / <c>stackalloc T[n]</c> / <c>ArrayPool&lt;T&gt;.Shared.Rent(n)</c> /
/// <c>Array.CreateInstance(t, n)</c> without first bounding <c>n</c> against the remaining payload.
/// Because the on-disk block/segment CRC is a checksum, not a MAC, a forged-and-re-CRC'd segment
/// reaches the decode path, so a colossal declared length drives an unbounded allocation
/// (out-of-memory denial of service) or an uncaught <see cref="OverflowException"/> ahead of the
/// read-bound.
/// </para>
/// <para><b>Taint is conservative; guard recognition fails closed.</b> The analyzer reports only
/// when the size is <i>clearly</i> decode-tainted (partial recall: an untracked taint source is
/// not reported). Once it is tainted, a guard silences the report only if it provably runs on every
/// path to the allocation: the dominance check and the bound-condition check are allow-lists of
/// constructs whose evaluation the analyzer understands, and any other shape — a branch, an arm, a
/// short-circuit operand, a null-conditional access, a swallowing <c>catch</c>, a label a
/// <c>goto</c> can reach, a gated condition — leaves the allocation reported. A genuine design
/// limit with no payload-derived ceiling (e.g. a constant zero-bit-width column) should carry an
/// explicit absolute-cap check or a documented in-source <c>[SuppressMessage]</c>.
/// </para>
/// <para><b>Ceiling provenance is intra-procedural.</b> Known decode reads and decoded-metadata
/// fields cannot serve as ceilings, including through local aliases or reassignments. A maximal
/// integer constant is not an effective allocation cap. Opaque caller-supplied ceilings, fields
/// outside the recognized metadata types, and the contracts of guard/decoder calls still require
/// caller review; silence is not a whole-program proof of a payload-derived bound.</para>
/// <para>Recognized taint sources (all intra-procedural):</para>
/// <list type="bullet">
///   <item>A local assigned from a decode read — <c>BinaryPrimitives.ReadInt32LittleEndian(...)</c>,
///   <c>VarintHelpers.ReadVarUInt64(...)</c>, any <c>Read{Int,UInt,Var,Double,Single,Half,Decimal}*</c>
///   or <c>Decode*</c> invocation — possibly through casts and <c>+ - *</c> arithmetic.</item>
///   <item>A decoded-metadata member access — <c>desc.ByteLength</c>, <c>footer.RowCount</c> — where
///   the receiver type is a descriptor / footer / header / metadata / manifest / row-group / entry
///   AND the access occurs inside a decode-shaped method (encode/write paths size from in-memory
///   state, never from an untrusted source).</item>
///   <item>A count-shaped integer parameter of a <c>Decode*</c> / <c>Read*</c> / <c>Deserialize*</c>
///   method that also takes a byte-payload parameter (span / memory / array / stream).</item>
/// </list>
/// <para>
/// A length whose static type is at most 16 bits wide (<c>byte</c>, <c>sbyte</c>, <c>short</c>,
/// <c>ushort</c>) is never flagged: its value cannot exceed 65 535, so it is not an out-of-memory
/// vector even when decoded from a hostile segment.
/// </para>
/// <para>Recognized guards (any one that runs on every path to the allocation suppresses the report):</para>
/// <list type="bullet">
///   <item>A preceding call to a method whose name reads as a guard
///   (<c>Validate* / Ensure* / Check* / Guard* / Bound* / Require* / Verify* / Throw*</c>) that
///   receives one of the tainted symbols — <c>ValidateColumnExtent(desc, …)</c>,
///   <c>EnsureWithinRemaining(stream, suffixLen, …)</c>.</item>
///   <item>A preceding <c>if (&lt;upper bound&gt;) { throw / return / break / continue }</c> — an
///   inline bound check or absolute-cap check. The bound is proved by construction: the exit fires
///   exactly when the length is ABOVE a ceiling that does not itself derive from the length
///   (<c>length &gt; payload.Length</c>, <c>offset + length &gt; stream.Length - stream.Position</c>,
///   <c>count &gt; MaxRows</c>, <c>remaining - length &lt; 0</c>, or the negated form exiting from the
///   else branch). The length side may be an intermediate such as <c>requiredBytes</c>, followed by
///   forward taint closure, built only through <c>+ - *</c> by a non-negative factor and
///   <c>/ &gt;&gt;</c> by a positive constant, every sum and product evaluated where it cannot wrap
///   (a checked context, or 64-bit arithmetic over operands at most 32 bits wide). A sign-only check (<c>length &lt; 0</c>), an inverted
///   comparison (<c>length &lt; payload.Length</c>), a relational early-out (<c>length &lt;= 0</c>) or
///   a comparison with a value derived from the length bounds nothing and leaves the allocation
///   reported.</item>
///   <item>A preceding call that passes the tainted length together with the byte payload to a
///   bounded decoder — <c>DecodeString(input, firstRowIndex + count)</c> — which validates the
///   length against that payload before this thinner slice allocates.</item>
/// </list>
/// <para>
/// Every guard must also see the value that is allocated: a write to a symbol the size is tainted
/// through (an assignment, <c>++</c>/<c>--</c>, an <c>out</c>/<c>ref</c> argument) after the guard
/// and before the allocation, or inside a loop that re-runs the allocation but not the guard,
/// voids it. A multiplication by a zero width (a constant column) makes a payload comparison
/// vacuous; such a path carries its own absolute ceiling (see the suppression note above).
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnboundedDecodedLengthAllocationAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.UnboundedDecodedLengthAllocation);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();

        // Generated code (including this generator's own emitted serializers) is not a decode
        // surface we author, so exclude it — a generated reader is the generator's responsibility,
        // not a hand-written persistence path.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterSyntaxNodeAction(AnalyzeArrayCreation, SyntaxKind.ArrayCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeStackAlloc, SyntaxKind.StackAllocArrayCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocationAllocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeArrayCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (ArrayCreationExpressionSyntax)context.Node;
        var size = FirstExplicitSize(creation.Type);
        if (size is not null)
        {
            AnalyzeAllocation(context, size, $"{creation.Type.ElementType}[]");
        }
    }

    private static void AnalyzeStackAlloc(SyntaxNodeAnalysisContext context)
    {
        var creation = (StackAllocArrayCreationExpressionSyntax)context.Node;
        if (creation.Type is ArrayTypeSyntax arrayType && FirstExplicitSize(arrayType) is { } size)
        {
            AnalyzeAllocation(context, size, $"stackalloc {arrayType.ElementType}[]");
        }
    }

    private static void AnalyzeInvocationAllocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
            is not IMethodSymbol method)
        {
            return;
        }

        var containing = method.ContainingType?.Name ?? string.Empty;
        var args = invocation.ArgumentList.Arguments;

        // ArrayPool<T>.Shared.Rent(minimumLength) — the rented buffer is at least `minimumLength`,
        // so a forged length drives the pool to allocate an arbitrarily large array.
        if (containing == "ArrayPool" && method.Name == "Rent" && args.Count >= 1)
        {
            AnalyzeAllocation(context, args[0].Expression, "ArrayPool buffer");
            return;
        }

        // Array.CreateInstance(elementType, length [, length2 …]) — every length argument is a size.
        if (containing == "Array" && method.Name == "CreateInstance" && args.Count >= 2)
        {
            for (var i = 1; i < args.Count; i++)
            {
                AnalyzeAllocation(context, args[i].Expression, "array");
            }

            return;
        }

        // GC.AllocateArray<T>(length) / GC.AllocateUninitializedArray<T>(length).
        if (containing == "GC"
            && (method.Name == "AllocateArray" || method.Name == "AllocateUninitializedArray")
            && args.Count >= 1)
        {
            AnalyzeAllocation(context, args[0].Expression, "array");
        }
    }

    private static void AnalyzeAllocation(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax sizeExpression,
        string allocatedDescription)
    {
        var model = context.SemanticModel;
        var token = context.CancellationToken;

        // A compile-time constant size cannot be attacker-controlled.
        if (model.GetConstantValue(sizeExpression, token).HasValue)
        {
            return;
        }

        // A length at most 16 bits wide (≤ 65 535) is not an out-of-memory vector even when forged.
        if (model.GetTypeInfo(sizeExpression, token).Type is { } sizeType && IsSmallIntegerType(sizeType))
        {
            return;
        }

        var body = EnclosingBody(sizeExpression);
        if (body is null)
        {
            return;
        }

        var inDecodeContext = IsDecodeMethodName(EnclosingMethodName(sizeExpression));

        // Collect the symbols / member names through which the size is decode-tainted, including the
        // upstream roots (so a guard that checks an intermediate derived from the same root is seen).
        var taint = new TaintKeys();
        CollectTaint(sizeExpression, body, model, token, inDecodeContext, taint);
        if (taint.IsEmpty)
        {
            return;
        }

        if (HasPrecedingGuard(body, sizeExpression, taint, model, token))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.UnboundedDecodedLengthAllocation,
            sizeExpression.GetLocation(),
            allocatedDescription,
            sizeExpression.ToString()));
    }

    // ----- taint collection -------------------------------------------------------------------

    private static void CollectTaint(
        ExpressionSyntax sizeExpression,
        SyntaxNode body,
        SemanticModel model,
        CancellationToken token,
        bool inDecodeContext,
        TaintKeys taint)
    {
        if (inDecodeContext)
        {
            foreach (var member in sizeExpression.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
            {
                if (IsMetadataMember(member, model, token, out var receiver))
                {
                    taint.MemberNames.Add(member.Name.Identifier.ValueText);
                    if (receiver is not null)
                    {
                        taint.Symbols.Add(receiver);
                    }
                }
            }
        }

        foreach (var id in sizeExpression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            // Skip the `.Member` half of a member access — it binds to the member, not a length local.
            if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id)
            {
                continue;
            }

            if (model.GetSymbolInfo(id, token).Symbol is { } symbol and (ILocalSymbol or IParameterSymbol))
            {
                IsSymbolTainted(symbol, body, model, token, inDecodeContext,
                    new HashSet<ISymbol>(SymbolEqualityComparer.Default), taint.Symbols);
            }
        }
    }

    /// <summary>
    /// Determines whether <paramref name="symbol"/> carries a decoded length, recording it and every
    /// upstream tainted symbol it derives from into <paramref name="roots"/>.
    /// </summary>
    private static bool IsSymbolTainted(
        ISymbol symbol,
        SyntaxNode body,
        SemanticModel model,
        CancellationToken token,
        bool inDecodeContext,
        HashSet<ISymbol> visited,
        HashSet<ISymbol> roots)
    {
        if (!visited.Add(symbol))
        {
            return false;
        }

        var tainted = symbol switch
        {
            IParameterSymbol p => IsTaintedParameter(p),
            ILocalSymbol l => IsLocalTainted(l, body, model, token, inDecodeContext, visited, roots),
            _ => false,
        };

        if (tainted)
        {
            roots.Add(symbol);
        }

        return tainted;
    }

    /// <summary>
    /// A count-shaped integer parameter of a decode/read/deserialize method that also takes a
    /// byte-payload parameter is treated as untrusted: it is the caller-supplied element count a
    /// projection or slice decoder receives, and a corrupt segment reaches the decoder through it.
    /// </summary>
    private static bool IsTaintedParameter(IParameterSymbol parameter)
    {
        if (parameter.ContainingSymbol is not IMethodSymbol method
            || !IsDecodeMethodName(method.Name)
            || !IsIntegerType(parameter.Type)
            || !IsLengthName(parameter.Name))
        {
            return false;
        }

        // Require a sibling byte-payload parameter — that buffer is what the count is supposed to be
        // bounded against, and its presence is what makes the count a decode quantity.
        foreach (var sibling in method.Parameters)
        {
            if (!SymbolEqualityComparer.Default.Equals(sibling, parameter) && IsBytePayloadType(sibling.Type))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLocalTainted(
        ILocalSymbol local,
        SyntaxNode body,
        SemanticModel model,
        CancellationToken token,
        bool inDecodeContext,
        HashSet<ISymbol> visited,
        HashSet<ISymbol> roots)
    {
        foreach (var declarator in body.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Initializer?.Value is { } init
                && SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(declarator, token), local)
                && IsTaintedExpression(init, body, model, token, inDecodeContext, visited, roots))
            {
                return true;
            }
        }

        foreach (var assignment in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left, token).Symbol, local)
                && IsTaintedExpression(assignment.Right, body, model, token, inDecodeContext, visited, roots))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTaintedExpression(
        ExpressionSyntax expression,
        SyntaxNode body,
        SemanticModel model,
        CancellationToken token,
        bool inDecodeContext,
        HashSet<ISymbol> visited,
        HashSet<ISymbol> roots)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax paren:
                return IsTaintedExpression(paren.Expression, body, model, token, inDecodeContext, visited, roots);

            case CastExpressionSyntax cast:
                return IsTaintedExpression(cast.Expression, body, model, token, inDecodeContext, visited, roots);

            case CheckedExpressionSyntax chk:
                return IsTaintedExpression(chk.Expression, body, model, token, inDecodeContext, visited, roots);

            case BinaryExpressionSyntax binary
                when binary.IsKind(SyntaxKind.AddExpression)
                    || binary.IsKind(SyntaxKind.SubtractExpression)
                    || binary.IsKind(SyntaxKind.MultiplyExpression):
                return IsTaintedExpression(binary.Left, body, model, token, inDecodeContext, visited, roots)
                    || IsTaintedExpression(binary.Right, body, model, token, inDecodeContext, visited, roots);

            case InvocationExpressionSyntax invocation:
                return model.GetSymbolInfo(invocation, token).Symbol is IMethodSymbol m
                    && IsDecodeReadName(m.Name);

            case MemberAccessExpressionSyntax member:
                return inDecodeContext && IsMetadataMember(member, model, token, out _);

            case IdentifierNameSyntax id:
                return model.GetSymbolInfo(id, token).Symbol is { } s and (ILocalSymbol or IParameterSymbol)
                    && IsSymbolTainted(s, body, model, token, inDecodeContext, visited, roots);

            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="member"/> reads a length/count off a decoded-metadata receiver
    /// (descriptor / footer / header / metadata / manifest / row-group / entry). The receiver symbol,
    /// if any, is returned so a guard that validates the whole descriptor (e.g.
    /// <c>ValidateColumnExtent(desc, …)</c>) is recognized.
    /// </summary>
    private static bool IsMetadataMember(
        MemberAccessExpressionSyntax member,
        SemanticModel model,
        CancellationToken token,
        out ISymbol? receiver)
    {
        receiver = null;

        if (!IsLengthName(member.Name.Identifier.ValueText))
        {
            return false;
        }

        if (model.GetTypeInfo(member.Expression, token).Type is not { } receiverType
            || !IsMetadataTypeName(receiverType.Name))
        {
            return false;
        }

        receiver = model.GetSymbolInfo(member.Expression, token).Symbol;
        return true;
    }

    // ----- guard recognition ------------------------------------------------------------------

    private static bool HasPrecedingGuard(
        SyntaxNode body,
        ExpressionSyntax sizeExpression,
        TaintKeys taint,
        SemanticModel model,
        CancellationToken token)
    {
        // Expand the tainted roots to every local derived from them, so a guard that checks an
        // intermediate (requiredBytes, framingTail, frameEnd, footerPayloadStart) is recognized.
        var derived = BuildForwardClosure(body, taint.Symbols, model, token);
        var allocStart = sizeExpression.SpanStart;

        // Guard-named calls, and bounded-consume calls that pass the length with the byte payload.
        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.SpanStart >= allocStart
                || !ReferencesTaint(invocation.ArgumentList, derived, taint.MemberNames, model, token)
                || !Dominates(invocation, body, sizeExpression)
                || IsReassignedAfterGuard(invocation, body, sizeExpression, taint.Symbols, model, token))
            {
                continue;
            }

            if (IsGuardName(CalleeName(invocation)) || ArgumentsIncludeBytePayload(invocation, model, token))
            {
                return true;
            }
        }

        // `if (<relational comparison referencing the tainted length>) { exit }`. The relational
        // operator distinguishes a real bound / absolute-cap check from an equality early-out such as
        // `if (count == 0) return []`, which must NOT be mistaken for a length bound.
        foreach (var ifStatement in body.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (ifStatement.Span.End > allocStart
                || !Dominates(ifStatement, body, sizeExpression)
                || IsReassignedAfterGuard(ifStatement.Condition, body, sizeExpression, taint.Symbols, model, token))
            {
                continue;
            }

            if (IsUnconditionalBoundCheck(ifStatement, derived, taint, model, token))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a symbol the allocation size is tainted through may hold a different value at the
    /// allocation than it held when <paramref name="guard"/> evaluated it: a write to it (any
    /// assignment, <c>++</c>/<c>--</c>, or <c>out</c>/<c>ref</c> argument) lies after the guard and
    /// before the allocation, or lies in a loop that re-runs the allocation without re-running the
    /// guard. Dominance proves the guard ran; this proves it ran on the value that is allocated.
    /// Fails closed: a write anywhere in such a region invalidates the guard, whatever path it is on.
    /// </summary>
    private static bool IsReassignedAfterGuard(
        SyntaxNode guard,
        SyntaxNode body,
        ExpressionSyntax allocation,
        HashSet<ISymbol> watched,
        SemanticModel model,
        CancellationToken token)
    {
        foreach (var node in body.DescendantNodes())
        {
            if (WrittenSymbol(node, model, token) is not { } written || !watched.Contains(written))
            {
                continue;
            }

            if (node.SpanStart >= guard.Span.End && node.SpanStart < allocation.SpanStart)
            {
                return true;
            }

            for (var loop = node.Parent; loop is not null && loop != body; loop = loop.Parent)
            {
                if (IsLoop(loop)
                    && loop.Span.Contains(allocation.Span)
                    && !loop.Span.Contains(guard.Span))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ISymbol? WrittenSymbol(SyntaxNode node, SemanticModel model, CancellationToken token)
    {
        var target = node switch
        {
            AssignmentExpressionSyntax a => a.Left,
            PrefixUnaryExpressionSyntax p when p.IsKind(SyntaxKind.PreIncrementExpression)
                || p.IsKind(SyntaxKind.PreDecrementExpression) => p.Operand,
            PostfixUnaryExpressionSyntax p when p.IsKind(SyntaxKind.PostIncrementExpression)
                || p.IsKind(SyntaxKind.PostDecrementExpression) => p.Operand,
            ArgumentSyntax arg when !arg.RefKindKeyword.IsKind(SyntaxKind.None)
                && !arg.RefKindKeyword.IsKind(SyntaxKind.InKeyword) => arg.Expression,
            _ => null,
        };

        return target switch
        {
            null => null,
            IdentifierNameSyntax id => model.GetSymbolInfo(id, token).Symbol,
            DeclarationExpressionSyntax => null,
            // A deconstruction / tuple target writes every element; report the first tainted one by
            // returning the symbol of any identifier element (the caller tests membership).
            TupleExpressionSyntax tuple => tuple.Arguments
                .Select(a => a.Expression is IdentifierNameSyntax eid ? model.GetSymbolInfo(eid, token).Symbol : null)
                .FirstOrDefault(sym => sym is not null),
            _ => model.GetSymbolInfo(target, token).Symbol,
        };
    }

    private static bool IsLoop(SyntaxNode node)
        => node is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or ForEachStatementSyntax
            or ForEachVariableStatementSyntax;

    /// <summary>
    /// True when <paramref name="guard"/> runs on every path that reaches the allocation. This is
    /// an allow-list, not a deny-list: walking from the guard up to the lowest node that also
    /// contains the allocation, every step must be a construct the analyzer knows evaluates the
    /// child whenever the parent runs — a statement in a block, an <c>if</c>/<c>while</c>/<c>for</c>
    /// condition, a <c>try</c> block with no <c>catch</c>, the left operand of a short-circuit
    /// operator, an argument, an initializer. Any other construct — an <c>if</c>/<c>else</c>
    /// branch, a loop body or incrementor, a <c>switch</c> section or switch-expression arm, a
    /// conditional-expression arm, the right operand of <c>&amp;&amp;</c>/<c>||</c>/<c>??</c>/<c>??=</c>,
    /// the access of <c>?.</c>, a <c>try</c> whose <c>catch</c> can swallow the guard's throw, a
    /// lambda, or anything not listed — fails closed, so the allocation stays reported. A body
    /// that declares a label fails closed as well: a <c>goto</c> can jump over the guard.
    /// </summary>
    private static bool Dominates(SyntaxNode guard, SyntaxNode body, ExpressionSyntax allocation)
    {
        if (body.DescendantNodes().OfType<LabeledStatementSyntax>().Any())
        {
            return false;
        }

        var child = guard;
        for (var current = guard.Parent; current is not null; current = current.Parent)
        {
            if (!EvaluatesChildUnconditionally(current, child))
            {
                return false;
            }

            // Lowest common ancestor reached: the guard's child runs unconditionally here and,
            // since it does not contain the allocation and starts before it, it runs first.
            if (current == body || current.Span.Contains(allocation.Span))
            {
                return true;
            }

            child = current;
        }

        return false;
    }

    /// <summary>
    /// The allow-list behind <see cref="Dominates"/>: true only when <paramref name="parent"/>
    /// always evaluates <paramref name="child"/> whenever <paramref name="parent"/> itself runs
    /// (or, for a block, runs it in sequence). Everything not listed returns false.
    /// </summary>
    private static bool EvaluatesChildUnconditionally(SyntaxNode parent, SyntaxNode child)
    {
        return parent switch
        {
            BlockSyntax => true,
            SwitchSectionSyntax => true,
            ArrowExpressionClauseSyntax => true,
            ExpressionStatementSyntax => true,
            LocalDeclarationStatementSyntax => true,
            VariableDeclarationSyntax => true,
            VariableDeclaratorSyntax => true,
            EqualsValueClauseSyntax => true,
            ArgumentSyntax => true,
            ArgumentListSyntax => true,
            InvocationExpressionSyntax => true,
            ObjectCreationExpressionSyntax => true,
            MemberAccessExpressionSyntax m => child == m.Expression,
            ParenthesizedExpressionSyntax => true,
            CastExpressionSyntax => true,
            CheckedExpressionSyntax => true,
            AwaitExpressionSyntax => true,
            PrefixUnaryExpressionSyntax => true,
            PostfixUnaryExpressionSyntax => true,
            AssignmentExpressionSyntax a => !a.IsKind(SyntaxKind.CoalesceAssignmentExpression) || child == a.Left,
            BinaryExpressionSyntax b => child == b.Left
                || !(b.IsKind(SyntaxKind.LogicalAndExpression)
                     || b.IsKind(SyntaxKind.LogicalOrExpression)
                     || b.IsKind(SyntaxKind.CoalesceExpression)),
            ConditionalExpressionSyntax c => child == c.Condition,
            ConditionalAccessExpressionSyntax ca => child == ca.Expression,
            SwitchExpressionSyntax se => child == se.GoverningExpression,
            IfStatementSyntax i => child == i.Condition,
            WhileStatementSyntax w => child == w.Condition,
            ForStatementSyntax f => child == f.Declaration || child == f.Condition || f.Initializers.Contains(child),
            ForEachStatementSyntax fe => child == fe.Expression,
            SwitchStatementSyntax ss => child == ss.Expression,
            UsingStatementSyntax => true,
            LockStatementSyntax => true,
            FixedStatementSyntax => true,
            CheckedStatementSyntax => true,
            UnsafeStatementSyntax => true,
            TryStatementSyntax t => t.Catches.Count == 0,
            FinallyClauseSyntax => true,
            _ => false,
        };
    }

    private static HashSet<ISymbol> BuildForwardClosure(
        SyntaxNode body,
        HashSet<ISymbol> roots,
        SemanticModel model,
        CancellationToken token)
    {
        var closure = new HashSet<ISymbol>(roots, SymbolEqualityComparer.Default);
        var noMembers = new HashSet<string>(StringComparer.Ordinal);

        bool changed;
        do
        {
            changed = false;

            foreach (var declarator in body.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (declarator.Initializer?.Value is { } init
                    && model.GetDeclaredSymbol(declarator, token) is { } symbol
                    && !closure.Contains(symbol)
                    && ReferencesTaint(init, closure, noMembers, model, token))
                {
                    closure.Add(symbol);
                    changed = true;
                }
            }
        }
        while (changed);

        return closure;
    }

    private static bool ReferencesTaint(
        SyntaxNode scope,
        HashSet<ISymbol> symbols,
        HashSet<string> memberNames,
        SemanticModel model,
        CancellationToken token)
    {
        if (memberNames.Count > 0)
        {
            foreach (var member in scope.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
            {
                if (memberNames.Contains(member.Name.Identifier.ValueText))
                {
                    return true;
                }
            }
        }

        foreach (var id in scope.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id)
            {
                continue;
            }

            if (model.GetSymbolInfo(id, token).Symbol is { } symbol && symbols.Contains(symbol))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ArgumentsIncludeBytePayload(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken token)
    {
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (model.GetTypeInfo(argument.Expression, token).Type is { } type && IsBytePayloadType(type))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="ifStatement"/> is an unconditional guard-and-exit for the tainted
    /// length: one branch exits unconditionally (a throw / return / break / continue / yield break
    /// as the branch itself or as a top-level statement of its block — never one nested in a
    /// further <c>if</c> or a <c>try</c>), and the condition forces that branch whenever the length
    /// bound is violated. See <see cref="ForcesExitOnViolation"/>.
    /// </summary>
    private static bool IsUnconditionalBoundCheck(
        IfStatementSyntax ifStatement,
        HashSet<ISymbol> derived,
        TaintKeys taint,
        SemanticModel model,
        CancellationToken token)
    {
        var bound = new BoundContext(derived, taint, model, token);
        if (ExitsUnconditionally(ifStatement.Statement)
            && ForcesExitOnViolation(ifStatement.Condition, exitWhenTrue: true, bound))
        {
            return true;
        }

        return ifStatement.Else is { } elseClause
            && ExitsUnconditionally(elseClause.Statement)
            && ForcesExitOnViolation(ifStatement.Condition, exitWhenTrue: false, bound);
    }

    /// <summary>
    /// True when the condition takes the value <paramref name="exitWhenTrue"/> whenever the length
    /// bound it carries is violated, whatever any other operand evaluates to. An allow-list over
    /// boolean structure: parentheses pass through; <c>!</c> flips the exit sense; an <c>||</c> /
    /// <c>|</c> forces true when either side does and false only when both do; <c>&amp;&amp;</c> /
    /// <c>&amp;</c> the reverse. So <c>stream.CanSeek &amp;&amp; length &gt; remaining</c> does not
    /// bound a non-seekable stream, while <c>length &lt; 0 || length &gt; max</c> does. The leaf is
    /// a relational comparison that is an upper bound (see <see cref="IsUpperBound"/>); every
    /// other shape — a conditional expression, a switch expression, an invocation, a pattern —
    /// fails closed.
    /// </summary>
    private static bool ForcesExitOnViolation(ExpressionSyntax condition, bool exitWhenTrue, BoundContext bound)
    {
        switch (condition)
        {
            case ParenthesizedExpressionSyntax paren:
                return ForcesExitOnViolation(paren.Expression, exitWhenTrue, bound);

            case PrefixUnaryExpressionSyntax negation when negation.IsKind(SyntaxKind.LogicalNotExpression):
                return ForcesExitOnViolation(negation.Operand, !exitWhenTrue, bound);

            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression) || b.IsKind(SyntaxKind.BitwiseOrExpression):
                return exitWhenTrue
                    ? ForcesExitOnViolation(b.Left, true, bound) || ForcesExitOnViolation(b.Right, true, bound)
                    : ForcesExitOnViolation(b.Left, false, bound) && ForcesExitOnViolation(b.Right, false, bound);

            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.BitwiseAndExpression):
                return exitWhenTrue
                    ? ForcesExitOnViolation(b.Left, true, bound) && ForcesExitOnViolation(b.Right, true, bound)
                    : ForcesExitOnViolation(b.Left, false, bound) || ForcesExitOnViolation(b.Right, false, bound);

            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LessThanExpression)
                || b.IsKind(SyntaxKind.LessThanOrEqualExpression)
                || b.IsKind(SyntaxKind.GreaterThanExpression)
                || b.IsKind(SyntaxKind.GreaterThanOrEqualExpression):
                return IsUpperBound(b, exitWhenTrue, bound);

            default:
                return false;
        }
    }

    /// <summary>
    /// True when the relational comparison <paramref name="comparison"/> evaluates to
    /// <paramref name="exitWhenTrue"/> exactly when the tainted length is LARGE — i.e. it orders the
    /// length above a ceiling that does not itself derive from the length. Proof by construction:
    /// exactly one operand references the length, and only through <c>+</c>, <c>-</c>, <c>*</c>,
    /// casts and parentheses, so its monotone direction in the length is known (<see cref="Polarity"/>);
    /// the other operand references nothing derived from the length. The comparison then exits on a
    /// large length only for the operator pointing that way: <c>length &gt; ceiling</c>,
    /// <c>ceiling &lt; length</c>, <c>ceiling - length &lt; 0</c>, and their negations taken from the
    /// else branch. A sign-only check (<c>length &lt; 0</c>), an inverted bound
    /// (<c>length &lt; payload.Length</c>), a relational early-out (<c>length &lt;= 0</c>), a
    /// comparison of the length with itself or with a value derived from it, and any operand whose
    /// direction in the length is not provable all fail closed.
    /// </summary>
    private static bool IsUpperBound(BinaryExpressionSyntax comparison, bool exitWhenTrue, BoundContext bound)
    {
        var left = Polarity(comparison.Left, bound, new HashSet<ISymbol>(SymbolEqualityComparer.Default));
        var right = Polarity(comparison.Right, bound, new HashSet<ISymbol>(SymbolEqualityComparer.Default));

        // The length side must be monotone in the length; the ceiling side must not involve it.
        int lengthDirection; // +1: the length side grows with the length; -1: it shrinks.
        bool lengthOnLeft;
        if (left is TaintPolarity.Increasing or TaintPolarity.Decreasing && right == TaintPolarity.None)
        {
            lengthDirection = left == TaintPolarity.Increasing ? 1 : -1;
            lengthOnLeft = true;
        }
        else if (right is TaintPolarity.Increasing or TaintPolarity.Decreasing && left == TaintPolarity.None)
        {
            lengthDirection = right == TaintPolarity.Increasing ? 1 : -1;
            lengthOnLeft = false;
        }
        else
        {
            return false;
        }

        // Being independent of this length does not make a ceiling trustworthy: a second
        // decoded field is equally forgeable, and int.MaxValue bounds no possible int count.
        var ceiling = lengthOnLeft ? comparison.Right : comparison.Left;
        var length = lengthOnLeft ? comparison.Left : comparison.Right;
        if (!IsIndependentCeiling(ceiling, bound)
            || !HasIndependentCeilingTerms(length, bound, lengthDirection))
        {
            return false;
        }

        // Does the comparison become TRUE as the length grows without bound?
        var greater = comparison.IsKind(SyntaxKind.GreaterThanExpression)
            || comparison.IsKind(SyntaxKind.GreaterThanOrEqualExpression);
        var leftGrowsTrue = greater; // `L > R` / `L >= R` turns true as L grows.
        var lengthSideGrowsTrue = lengthOnLeft ? leftGrowsTrue : !leftGrowsTrue;
        var trueWhenLarge = lengthDirection > 0 ? lengthSideGrowsTrue : !lengthSideGrowsTrue;

        return trueWhenLarge == exitWhenTrue;
    }

    // The ceiling may be moved to the length side (`ceiling - count < 0`). Check its
    // provenance there too; merely checking the opposite operand would accept that bypass.
    private static bool HasIndependentCeilingTerms(ExpressionSyntax expression, BoundContext bound, int direction, int sign = 1)
    {
        if (Polarity(expression, bound, new HashSet<ISymbol>(SymbolEqualityComparer.Default)) == TaintPolarity.None)
        {
            // Positive additive offsets tighten an increasing length bound; only terms that
            // loosen it act as a moved ceiling. Prior-frame positions may themselves be decoded.
            return sign == direction || IsIndependentCeiling(expression, bound);
        }

        return expression switch
        {
            ParenthesizedExpressionSyntax paren => HasIndependentCeilingTerms(paren.Expression, bound, direction, sign),
            CastExpressionSyntax cast => HasIndependentCeilingTerms(cast.Expression, bound, direction, sign),
            CheckedExpressionSyntax check => HasIndependentCeilingTerms(check.Expression, bound, direction, sign),
            PrefixUnaryExpressionSyntax unary => HasIndependentCeilingTerms(unary.Operand, bound, direction,
                unary.IsKind(SyntaxKind.UnaryMinusExpression) ? -sign : sign),
            BinaryExpressionSyntax binary => HasIndependentCeilingTerms(binary.Left, bound, direction, sign)
                && HasIndependentCeilingTerms(binary.Right, bound, direction,
                    binary.IsKind(SyntaxKind.SubtractExpression) ? -sign : sign),
            IdentifierNameSyntax id when bound.Model.GetSymbolInfo(id, bound.Token).Symbol is { } symbol
                && !bound.Taint.Symbols.Contains(symbol) && DeclarationInitializer(symbol, bound) is { } initializer
                => HasIndependentCeilingTerms(initializer, bound, direction, sign),
            _ => true,
        };
    }

    // This is a source rejection, not a whole-program proof: opaque caller-provided limits and
    // guard/decoder calls retain their existing contracts. Known decoded ceilings must not be
    // mistaken for limits merely because they are independent of the allocated length.
    private static bool IsIndependentCeiling(ExpressionSyntax expression, BoundContext bound)
    {
        var constant = bound.Model.GetConstantValue(expression, bound.Token);
        if (constant.HasValue)
        {
            if (constant.Value is null)
            {
                return false;
            }

            try
            {
                var value = Convert.ToDecimal(constant.Value, System.Globalization.CultureInfo.InvariantCulture);
                return value >= 0 && value < int.MaxValue;
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                return false;
            }
        }

        return !HasDecodedCeilingSource(expression, bound, new HashSet<ISymbol>(SymbolEqualityComparer.Default));
    }

    private static bool HasDecodedCeilingSource(ExpressionSyntax expression, BoundContext bound, HashSet<ISymbol> visiting)
    {
        // Reuse the effective-cap check for constants reached through local aliases too.
        if (bound.Model.GetConstantValue(expression, bound.Token).HasValue)
        {
            return !IsIndependentCeiling(expression, bound);
        }

        foreach (var node in expression.DescendantNodesAndSelf())
        {
            if (node is InvocationExpressionSyntax call
                && bound.Model.GetSymbolInfo(call, bound.Token).Symbol is IMethodSymbol method
                && IsDecodeReadName(method.Name))
            {
                return true;
            }

            if (node is MemberAccessExpressionSyntax member && IsMetadataMember(member, bound.Model, bound.Token, out _))
            {
                return true;
            }

            if (node is not IdentifierNameSyntax id
                || bound.Model.GetSymbolInfo(id, bound.Token).Symbol is not ILocalSymbol local
                || !visiting.Add(local))
            {
                continue;
            }

            if (DeclarationInitializer(local, bound) is { } initializer && HasDecodedCeilingSource(initializer, bound, visiting))
            {
                return true;
            }

            if (EnclosingBody(expression) is { } body && body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(assignment => SymbolEqualityComparer.Default.Equals(bound.Model.GetSymbolInfo(assignment.Left, bound.Token).Symbol, local)
                    && HasDecodedCeilingSource(assignment.Right, bound, visiting)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The monotone direction of <paramref name="expression"/> in the tainted length: <c>None</c> when
    /// it references nothing derived from the length, <c>Increasing</c> / <c>Decreasing</c> when it is
    /// built from the length only through <c>+</c>, <c>-</c>, <c>*</c> by a non-negative non-length
    /// factor, <c>/</c> or <c>&gt;&gt;</c> by a positive constant, unary
    /// <c>-</c>/<c>+</c>, casts, <c>checked</c> and parentheses (lengths and byte counts are
    /// non-negative), and <c>Unknown</c> for every other shape that references the length.
    /// A derived local takes the direction of its declaration initializer.
    /// </summary>
    private static TaintPolarity Polarity(ExpressionSyntax expression, BoundContext bound, HashSet<ISymbol> visiting)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax paren:
                return Polarity(paren.Expression, bound, visiting);
            case CastExpressionSyntax cast:
                return Polarity(cast.Expression, bound, visiting);
            case CheckedExpressionSyntax chk:
                return Polarity(chk.Expression, bound, visiting);
            case PrefixUnaryExpressionSyntax plus when plus.IsKind(SyntaxKind.UnaryPlusExpression):
                return Polarity(plus.Operand, bound, visiting);
            case PrefixUnaryExpressionSyntax minus when minus.IsKind(SyntaxKind.UnaryMinusExpression):
                return Flip(Polarity(minus.Operand, bound, visiting));
            case BinaryExpressionSyntax add when add.IsKind(SyntaxKind.AddExpression):
                return RequireNoWrap(
                    add,
                    Combine(Polarity(add.Left, bound, visiting), Polarity(add.Right, bound, visiting)),
                    bound);
            case BinaryExpressionSyntax sub when sub.IsKind(SyntaxKind.SubtractExpression):
                return Combine(Polarity(sub.Left, bound, visiting), Flip(Polarity(sub.Right, bound, visiting)));
            case BinaryExpressionSyntax mul when mul.IsKind(SyntaxKind.MultiplyExpression):
                {
                    var l = Polarity(mul.Left, bound, visiting);
                    var r = Polarity(mul.Right, bound, visiting);
                    // A factor not derived from the length keeps the direction only when it cannot be
                    // negative: a positive constant, or an unsigned width (`bitWidth`, a byte). A zero
                    // width makes the product constant, so the comparison stops bounding the length —
                    // that is the documented constant-column limit, which must carry its own absolute
                    // ceiling (see the type remarks). A length-squared or a signed factor is not provable.
                    if (r == TaintPolarity.None && IsNonNegativeFactor(mul.Right, bound))
                    {
                        return RequireNoWrap(mul, l, bound);
                    }

                    if (l == TaintPolarity.None && IsNonNegativeFactor(mul.Left, bound))
                    {
                        return RequireNoWrap(mul, r, bound);
                    }

                    return l == TaintPolarity.None && r == TaintPolarity.None ? TaintPolarity.None : TaintPolarity.Unknown;
                }

            case MemberAccessExpressionSyntax member:
                {
                    // `desc.ByteLength` — the decoded-metadata member the length is tainted through.
                    if (bound.Taint.MemberNames.Contains(member.Name.Identifier.ValueText)
                        && IsMetadataMember(member, bound.Model, bound.Token, out _))
                    {
                        return TaintPolarity.Increasing;
                    }

                    // Another decoded field of a tainted descriptor (`desc.Offset`) is a non-negative
                    // decoded quantity on the same side as the length; anything else off the receiver
                    // is not provable.
                    var receiver = Polarity(member.Expression, bound, visiting);
                    return receiver switch
                    {
                        TaintPolarity.None => TaintPolarity.None,
                        TaintPolarity.Increasing when IsLengthName(member.Name.Identifier.ValueText)
                            || IsOffsetName(member.Name.Identifier.ValueText) => TaintPolarity.Increasing,
                        _ => TaintPolarity.Unknown,
                    };
                }

            case BinaryExpressionSyntax div when div.IsKind(SyntaxKind.DivideExpression)
                || div.IsKind(SyntaxKind.RightShiftExpression):
                {
                    // `(bits + 7) / 8`, `bytes >> 3`: dividing by a positive constant keeps the direction
                    // (non-strictly, which is all an upper bound needs); any other divisor is not provable.
                    var l = Polarity(div.Left, bound, visiting);
                    var r = Polarity(div.Right, bound, visiting);
                    if (r == TaintPolarity.None && IsPositiveConstant(div.Right, bound))
                    {
                        return l;
                    }

                    return l == TaintPolarity.None && r == TaintPolarity.None ? TaintPolarity.None : TaintPolarity.Unknown;
                }

            case IdentifierNameSyntax id:
                {
                    if (bound.Model.GetSymbolInfo(id, bound.Token).Symbol is not { } symbol
                        || !bound.Derived.Contains(symbol))
                    {
                        return TaintPolarity.None;
                    }

                    if (bound.Taint.Symbols.Contains(symbol))
                    {
                        // A root the allocated size is tainted through: the length itself (or a value it is
                        // read from). Its writes are checked by IsReassignedAfterGuard.
                        return TaintPolarity.Increasing;
                    }

                    // A derived intermediate: follow its declaration initializer.
                    if (!visiting.Add(symbol))
                    {
                        return TaintPolarity.Unknown;
                    }

                    var initializer = DeclarationInitializer(symbol, bound);
                    var result = initializer is null ? TaintPolarity.Unknown : Polarity(initializer, bound, visiting);
                    visiting.Remove(symbol);
                    return result;
                }

            default:
                return ReferencesTaintPrecisely(expression, bound) ? TaintPolarity.Unknown : TaintPolarity.None;
        }
    }

    /// <summary>
    /// A sum or product that carries the length is monotone only while it cannot wrap: in unchecked
    /// 32-bit arithmetic a forged <c>int.MaxValue</c> turns <c>length + 4</c> or <c>length * 4</c>
    /// negative, the comparison passes and the full length is allocated. The node keeps its direction
    /// when it is evaluated in a checked context (a wrap throws), or in 64-bit (or floating /
    /// decimal) arithmetic over operands at most 32 bits wide, which cannot reach the 64-bit range
    /// with the small factors a size computation uses. Anything else is not provable.
    /// </summary>
    private static TaintPolarity RequireNoWrap(BinaryExpressionSyntax node, TaintPolarity polarity, BoundContext bound)
    {
        if (polarity is not (TaintPolarity.Increasing or TaintPolarity.Decreasing))
        {
            return polarity;
        }

        if (IsCheckedContext(node, bound))
        {
            return polarity;
        }

        if (bound.Model.GetTypeInfo(node, bound.Token).Type is not { } type
            || Unwrap(type).SpecialType is not (SpecialType.System_Int64 or SpecialType.System_UInt64
                or SpecialType.System_Double or SpecialType.System_Decimal))
        {
            return TaintPolarity.Unknown;
        }

        return NarrowOperand(node.Left, bound) && NarrowOperand(node.Right, bound) ? polarity : TaintPolarity.Unknown;
    }

    // An operand of a widened sum/product: a widened composite (checked by its own RequireNoWrap),
    // a constant, or a value whose own type is at most 32 bits wide before the conversion to the
    // 64-bit node type.
    private static bool NarrowOperand(ExpressionSyntax operand, BoundContext bound)
    {
        while (operand is ParenthesizedExpressionSyntax paren)
        {
            operand = paren.Expression;
        }

        if (bound.Model.GetConstantValue(operand, bound.Token).HasValue || operand is BinaryExpressionSyntax)
        {
            return true;
        }

        // A derived intermediate (`requiredBits`) took its direction from its own initializer, whose
        // sums and products passed this same check; only a root the length is read into carries its
        // own width.
        if (operand is IdentifierNameSyntax id
            && bound.Model.GetSymbolInfo(id, bound.Token).Symbol is { } symbol
            && bound.Derived.Contains(symbol)
            && !bound.Taint.Symbols.Contains(symbol))
        {
            return true;
        }

        var inner = operand is CastExpressionSyntax cast ? cast.Expression : operand;
        return bound.Model.GetTypeInfo(inner, bound.Token).Type is { } t
            && Unwrap(t).SpecialType is SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32;
    }

    private static bool IsCheckedContext(SyntaxNode node, BoundContext bound)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case CheckedExpressionSyntax chk:
                    return chk.IsKind(SyntaxKind.CheckedExpression);
                case CheckedStatementSyntax stmt:
                    return stmt.IsKind(SyntaxKind.CheckedStatement);
                case MemberDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                case AnonymousFunctionExpressionSyntax:
                    return bound.Model.Compilation.Options.CheckOverflow;
            }
        }

        return bound.Model.Compilation.Options.CheckOverflow;
    }

    private static ExpressionSyntax? DeclarationInitializer(ISymbol symbol, BoundContext bound)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(bound.Token) is VariableDeclaratorSyntax { Initializer.Value: { } value })
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsNonNegativeFactor(ExpressionSyntax expression, BoundContext bound)
    {
        if (IsPositiveConstant(expression, bound))
        {
            return true;
        }

        return bound.Model.GetTypeInfo(expression, bound.Token).Type is { } type
            && Unwrap(type).SpecialType is SpecialType.System_Byte or SpecialType.System_UInt16
                or SpecialType.System_UInt32 or SpecialType.System_UInt64;
    }

    private static bool IsPositiveConstant(ExpressionSyntax expression, BoundContext bound)
    {
        var constant = bound.Model.GetConstantValue(expression, bound.Token);
        if (!constant.HasValue || constant.Value is null)
        {
            return false;
        }

        try
        {
            return Convert.ToDecimal(constant.Value, System.Globalization.CultureInfo.InvariantCulture) > 0m;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="scope"/> references a symbol derived from the tainted length, or a
    /// decoded-metadata member the length is tainted through (matched by name AND metadata receiver,
    /// so an unrelated <c>payload.Length</c> ceiling is not mistaken for a tainted <c>header.Length</c>).
    /// </summary>
    private static bool ReferencesTaintPrecisely(SyntaxNode scope, BoundContext bound)
    {
        if (bound.Taint.MemberNames.Count > 0)
        {
            foreach (var member in scope.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
            {
                if (bound.Taint.MemberNames.Contains(member.Name.Identifier.ValueText)
                    && IsMetadataMember(member, bound.Model, bound.Token, out _))
                {
                    return true;
                }
            }
        }

        foreach (var id in scope.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id)
            {
                continue;
            }

            if (bound.Model.GetSymbolInfo(id, bound.Token).Symbol is { } symbol && bound.Derived.Contains(symbol))
            {
                return true;
            }
        }

        return false;
    }

    private static TaintPolarity Flip(TaintPolarity polarity) => polarity switch
    {
        TaintPolarity.Increasing => TaintPolarity.Decreasing,
        TaintPolarity.Decreasing => TaintPolarity.Increasing,
        _ => polarity,
    };

    private static TaintPolarity Combine(TaintPolarity a, TaintPolarity b)
    {
        if (a == TaintPolarity.None)
        {
            return b;
        }

        if (b == TaintPolarity.None || a == b)
        {
            return a;
        }

        return TaintPolarity.Unknown;
    }

    private static bool ExitsUnconditionally(StatementSyntax statement)
    {
        return statement switch
        {
            BlockSyntax block => block.Statements.Any(IsExitStatement),
            _ => IsExitStatement(statement),
        };
    }

    private static bool IsExitStatement(StatementSyntax statement)
    {
        return statement switch
        {
            ThrowStatementSyntax or ReturnStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax => true,
            YieldStatementSyntax y => y.IsKind(SyntaxKind.YieldBreakStatement),
            _ => false,
        };
    }

    // ----- small helpers ----------------------------------------------------------------------

    private static ExpressionSyntax? FirstExplicitSize(ArrayTypeSyntax type)
    {
        if (type.RankSpecifiers.Count == 0 || type.RankSpecifiers[0].Sizes.Count == 0)
        {
            return null;
        }

        var size = type.RankSpecifiers[0].Sizes[0];
        return size is OmittedArraySizeExpressionSyntax ? null : size;
    }

    /// <summary>
    /// The body (block or arrow expression) of the nearest enclosing method-like declaration or
    /// local function — the scope within which taint flows and guards are searched.
    /// </summary>
    private static SyntaxNode? EnclosingBody(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case MethodDeclarationSyntax m:
                    return (SyntaxNode?)m.Body ?? m.ExpressionBody;
                case LocalFunctionStatementSyntax lf:
                    return (SyntaxNode?)lf.Body ?? lf.ExpressionBody;
                case ConstructorDeclarationSyntax c:
                    return (SyntaxNode?)c.Body ?? c.ExpressionBody;
                case AccessorDeclarationSyntax a:
                    return (SyntaxNode?)a.Body ?? a.ExpressionBody;
                case OperatorDeclarationSyntax o:
                    return (SyntaxNode?)o.Body ?? o.ExpressionBody;
            }
        }

        return null;
    }

    private static string EnclosingMethodName(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case MethodDeclarationSyntax m:
                    return m.Identifier.ValueText;
                case LocalFunctionStatementSyntax lf:
                    return lf.Identifier.ValueText;
                case ConstructorDeclarationSyntax c:
                    return c.Identifier.ValueText;
                case PropertyDeclarationSyntax p:
                    return p.Identifier.ValueText;
                case OperatorDeclarationSyntax:
                    return "operator";
            }
        }

        return string.Empty;
    }

    private static string CalleeName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            GenericNameSyntax g => g.Identifier.ValueText,
            _ => string.Empty,
        };
    }

    private static bool IsGuardName(string name)
        // Use whole-word (starts-with) matching rather than substring Contains to avoid false positives
        // such as "EnsureCapacity" (a buffer resize, not a guard) matching the "Ensure" fragment.
        => StartsWithAny(name, "Validate", "Ensure", "Check", "Guard", "Bound", "Require", "Verify", "Throw", "Assert");

    private static bool IsDecodeMethodName(string name)
        => ContainsAny(name, "Decode", "Deserialize", "Read", "Parse", "Rehydrate", "Project", "Materialize", "Scan", "Load");

    private static bool IsDecodeReadName(string name)
        => ContainsAny(
            name,
            "ReadVar", "ReadInt", "ReadUInt", "ReadDouble", "ReadSingle", "ReadHalf", "ReadDecimal",
            "ReadCompact", "Read7Bit", "ReadLength", "Decode", "Varint", "VarUInt", "VarInt");

    private static bool IsLengthName(string name)
        => ContainsAny(name, "Length", "Count", "Rows", "RowCount", "Size", "FirstRow", "Index", "Total");

    private static bool IsOffsetName(string name)
        => ContainsAny(name, "Offset", "Start", "Position", "Begin");

    private static bool IsMetadataTypeName(string name)
        => ContainsAny(name, "Descriptor", "Footer", "Header", "Metadata", "Manifest", "RowGroup", "Entry");

    private static bool IsBytePayloadType(ITypeSymbol type)
    {
        if (type.Name is "Span" or "ReadOnlySpan" or "Memory" or "ReadOnlyMemory"
            && type is INamedTypeSymbol { TypeArguments: { Length: 1 } typeArgs }
            && typeArgs[0].SpecialType == SpecialType.System_Byte)
        {
            return true;
        }

        if (type is IArrayTypeSymbol array && array.ElementType.SpecialType == SpecialType.System_Byte)
        {
            return true;
        }

        // Stream and its subclasses (MemoryStream, FileStream, the test-double streams).
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.Name == "Stream")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIntegerType(ITypeSymbol type)
    {
        var t = Unwrap(type);
        return t.SpecialType is SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_IntPtr or SpecialType.System_UIntPtr
            || t.Name is "Int128" or "UInt128";
    }

    private static bool IsSmallIntegerType(ITypeSymbol type)
    {
        return Unwrap(type).SpecialType is SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int16 or SpecialType.System_UInt16;
    }

    private static ITypeSymbol Unwrap(ITypeSymbol type)
    {
        return type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            && nullable.TypeArguments.Length == 1
            ? nullable.TypeArguments[0]
            : type;
    }

    private static bool StartsWithAny(string value, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAny(string value, params string[] fragments)
    {
        foreach (var fragment in fragments)
        {
            if (value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private enum TaintPolarity
    {
        None,
        Increasing,
        Decreasing,
        Unknown,
    }

    /// <summary>The inputs an upper-bound proof reads, bundled so the recursion stays readable.</summary>
    private sealed class BoundContext
    {
        public BoundContext(HashSet<ISymbol> derived, TaintKeys taint, SemanticModel model, CancellationToken token)
        {
            Derived = derived;
            Taint = taint;
            Model = model;
            Token = token;
        }

        public HashSet<ISymbol> Derived { get; }

        public TaintKeys Taint { get; }

        public SemanticModel Model { get; }

        public CancellationToken Token { get; }
    }

    /// <summary>The symbols and decoded-metadata member names a size expression is tainted through.</summary>
    private sealed class TaintKeys
    {
        public HashSet<ISymbol> Symbols { get; } = new(SymbolEqualityComparer.Default);

        public HashSet<string> MemberNames { get; } = new(StringComparer.Ordinal);

        public bool IsEmpty => Symbols.Count == 0 && MemberNames.Count == 0;
    }
}
