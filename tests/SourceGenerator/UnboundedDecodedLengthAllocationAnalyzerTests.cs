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
using System.Collections.Immutable;

using Microsoft.CodeAnalysis.Diagnostics;

namespace Boutquin.Storage.SourceGenerator.Tests;

/// <summary>
/// Exercises the BSSG021 <see cref="UnboundedDecodedLengthAllocationAnalyzer"/> against the decode
/// shapes the v2.0.0 storage-hardening review closed (positives that must flag) and the bound-guard
/// shapes those fixes introduced (negatives that must stay silent). The Infrastructure build's
/// zero-BSSG021 status is the real-codebase precision gate; this suite is the behaviour contract.
/// </summary>
public sealed class UnboundedDecodedLengthAllocationAnalyzerTests
{
    static UnboundedDecodedLengthAllocationAnalyzerTests()
    {
        // Force-load the assemblies hosting the types the test snippets use, so TestHelpers'
        // loaded-assembly reference scan resolves them regardless of which assembly hosts them.
        _ = typeof(System.Buffers.Binary.BinaryPrimitives).Assembly.FullName;
        _ = typeof(System.Buffers.ArrayPool<byte>).Assembly.FullName;
        _ = typeof(System.IO.Stream).Assembly.FullName;
    }

    private const string Preamble = """
        using System;
        using System.Buffers;
        using System.Buffers.Binary;
        using System.IO;
        """;

    private static async Task<ImmutableArray<Diagnostic>> RunAsync(string body)
    {
        var compilation = TestHelpers.CreateCompilationFromSources(new[] { Preamble + body });
        compilation.GetDiagnostics().Should().NotContain(d => d.Severity == DiagnosticSeverity.Error,
            "guard tests must analyze valid code");
        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new UnboundedDecodedLengthAllocationAnalyzer()));
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync().ConfigureAwait(true);
        return diagnostics.Where(d => d.Id == "BSSG021").ToImmutableArray();
    }

    // ─────────────────────────────── positives (must flag) ───────────────────────────────

    [Theory]
    [InlineData("var ceiling = BinaryPrimitives.ReadInt32LittleEndian(input.Slice(4));", "ceiling")]
    [InlineData("", "int.MaxValue")]
    [InlineData("var ceiling = int.MaxValue;", "ceiling")]
    [InlineData("", "footer.TotalBytes")]
    [InlineData("var remaining = BinaryPrimitives.ReadInt32LittleEndian(input.Slice(4));", "remaining")]
    [InlineData("var ceiling = footer.TotalBytes;", "ceiling")]
    public async Task UntrustedOrVacuousCeiling_DoesNotProveBound(string declaration, string ceiling)
    {
        var diagnostics = await RunAsync($$"""
            sealed class Footer { public int TotalBytes { get; set; } }
            static class C {
                static long[] DecodeRows(ReadOnlySpan<byte> input, Footer footer) {
                    var count = BinaryPrimitives.ReadInt32LittleEndian(input);
                    {{declaration}}
                    if (count > {{ceiling}}) throw new InvalidDataException();
                    return new long[count];
                }
            }
            """);

        diagnostics.Should().ContainSingle();
    }

    [Theory]
    [InlineData("var ceiling = input.Length; ceiling = BinaryPrimitives.ReadInt32LittleEndian(input.Slice(4));", "count > ceiling")]
    [InlineData("var ceiling = BinaryPrimitives.ReadInt32LittleEndian(input.Slice(4));", "ceiling - count < 0")]
    [InlineData("var ceiling = BinaryPrimitives.ReadInt32LittleEndian(input.Slice(4));", "count - ceiling > 0")]
    public async Task UntrustedCeilingAliasesAndSubtraction_DoNotProveBound(string declaration, string condition)
    {
        var diagnostics = await RunAsync($$"""
            static class C {
                static long[] DecodeRows(ReadOnlySpan<byte> input) {
                    var count = BinaryPrimitives.ReadInt32LittleEndian(input);
                    {{declaration}}
                    if ({{condition}}) throw new InvalidDataException();
                    return new long[count];
                }
            }
            """);

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task PayloadExtentAlias_ProvesBound()
    {
        var diagnostics = await RunAsync("""
            static class C {
                static long[] DecodeRows(ReadOnlySpan<byte> input) {
                    var count = BinaryPrimitives.ReadInt32LittleEndian(input);
                    var remaining = input.Length - 4;
                    if (count > remaining) throw new InvalidDataException();
                    return new long[count];
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task CallerProvidedCeiling_ProvenanceRequiresCallerReview()
    {
        // The analyzer does not follow caller arguments. This may be an actual payload snapshot
        // or a validated policy limit; proving that contract remains a caller-review obligation.
        var diagnostics = await RunAsync("""
            static class C {
                static long[] DecodeRows(ReadOnlySpan<byte> input, int ceiling) {
                    var count = BinaryPrimitives.ReadInt32LittleEndian(input);
                    if (count > ceiling) throw new InvalidDataException();
                    return new long[count];
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task PriorFrameOffset_AddsToRequiredExtent_StillProvesBound()
    {
        var diagnostics = await RunAsync("""
            static class C {
                static void DecodeFrames(ReadOnlySpan<byte> input) {
                    long position = 0;
                    while (position < input.Length) {
                        var count = BinaryPrimitives.ReadInt32LittleEndian(input.Slice((int)position));
                        var frameEnd = position + 4 + count;
                        if (frameEnd > input.Length) break;
                        var bytes = new byte[count];
                        position = frameEnd;
                    }
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task LocalReadLength_NewArray_NoGuard_IsFlagged()
    {
        var diagnostics = await RunAsync("""
            static class C {
                static byte[] DecodeFrame(ReadOnlySpan<byte> input) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(input);
                    return new byte[length];
                }
            }
            """);

        diagnostics.Should().ContainSingle()
            .Which.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Should().Contain("length");
    }

    [Fact]
    public async Task ArithmeticOnReadLength_IsFlagged()
    {
        var diagnostics = await RunAsync("""
            static class C {
                static byte[] DecodeFrame(ReadOnlySpan<byte> input) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(input);
                    return new byte[length + 4];
                }
            }
            """);

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task ArrayPoolRent_FromReadLength_IsFlagged()
    {
        var diagnostics = await RunAsync("""
            static class C {
                static void DecodeFrame(ReadOnlySpan<byte> input) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(input);
                    var buffer = ArrayPool<byte>.Shared.Rent(length);
                }
            }
            """);

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task StackAlloc_FromReadLength_IsFlagged()
    {
        var diagnostics = await RunAsync("""
            static class C {
                static void DecodeFrame(ReadOnlySpan<byte> input) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(input);
                    Span<byte> scratch = stackalloc byte[length];
                }
            }
            """);

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task ArrayCreateInstance_FromReadLength_IsFlagged()
    {
        var diagnostics = await RunAsync("""
            static class C {
                static Array DecodeFrame(ReadOnlySpan<byte> input) {
                    var count = BinaryPrimitives.ReadInt32LittleEndian(input);
                    return Array.CreateInstance(typeof(int), count);
                }
            }
            """);

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task CountParameterOfDecodeMethod_WithPayloadSibling_IsFlagged()
    {
        var diagnostics = await RunAsync("""
            static class C {
                static decimal[] DecodeSlice(ReadOnlySpan<byte> input, int count) {
                    return new decimal[count];
                }
            }
            """);

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task MetadataMember_InDecodeContext_IsFlagged()
    {
        var diagnostics = await RunAsync("""
            readonly struct ColumnDescriptor { public long ByteLength { get; init; } }
            static class C {
                static byte[] DecodeColumn(ReadOnlySpan<byte> input, ColumnDescriptor desc) {
                    return new byte[desc.ByteLength];
                }
            }
            """);

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task EqualityEarlyOut_IsNotMistakenForBound_StillFlagged()
    {
        // `if (count == 0) return ...` is an early-out, not a length bound — the alloc is still
        // unbounded and must be flagged.
        var diagnostics = await RunAsync("""
            static class C {
                static byte[] DecodeFrame(ReadOnlySpan<byte> input) {
                    var count = BinaryPrimitives.ReadInt32LittleEndian(input);
                    if (count == 0) return Array.Empty<byte>();
                    return new byte[count];
                }
            }
            """);

        diagnostics.Should().ContainSingle();
    }

    [Fact]
    public async Task CanSeekGatedBound_LeavesNonSeekablePathUnbounded_IsFlagged()
    {
        // The bound `length > remaining` only fires when `stream.CanSeek` is true. On a non-seekable
        // stream the `&&` short-circuits and the throw never runs, so `new byte[length]` allocates the
        // full forged length unbounded. A guard conjoined with an orthogonal predicate (here CanSeek)
        // is NOT an unconditional bound — the allocation must still be flagged. (This is the
        // JsonVersionedSerializer forged-length-DoS class the BSSG021 loophole let through.)
        var diagnostics = await RunAsync("""
            static class C {
                static byte[] DecodeFrame(Stream stream, ReadOnlySpan<byte> header) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (stream.CanSeek && length > stream.Length - stream.Position)
                        throw new InvalidDataException("corrupt");
                    return new byte[length];
                }
            }
            """);

        diagnostics.Should().ContainSingle(
            "a bound gated behind stream.CanSeek leaves the non-seekable path unbounded, so it is "
            + "not a sufficient unconditional bound for BSSG021");
    }

    [Fact]
    public async Task BoolPredicateGatedBound_IsFlagged()
    {
        // Same loophole, generalised: any `&&` whose other operand is an orthogonal runtime predicate
        // (not derived from the tainted length) gates the comparison, leaving a bypass path unbounded.
        var diagnostics = await RunAsync("""
            static class C {
                static byte[] DecodeFrame(ReadOnlySpan<byte> header, bool strict) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (strict && length > header.Length)
                        throw new InvalidDataException("corrupt");
                    return new byte[length];
                }
            }
            """);

        diagnostics.Should().ContainSingle(
            "a bound conjoined with an orthogonal predicate is bypassed when that predicate is false");
    }

    // ─────────────────────────────── negatives (must stay silent) ───────────────────────────────

    [Fact]
    public async Task UnconditionalStreamBound_IsNotFlagged()
    {
        // The genuinely-unconditional bound the Wave-1 JsonVersionedSerializer fix adopts: the
        // comparison is NOT gated behind CanSeek, so every path (seekable or not) hits the throw
        // before the allocation. Must stay silent — over-tightening that flags this is a false
        // positive that would break the build.
        var diagnostics = await RunAsync("""
            static class C {
                static byte[] DecodeFrame(Stream stream, ReadOnlySpan<byte> header) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length > header.Length)
                        throw new InvalidDataException("corrupt");
                    return new byte[length];
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ConjoinedLengthDerivedComparisons_IsNotFlagged()
    {
        // Both operands of the `&&` are bounds derived from the tainted length — neither is an
        // orthogonal bypass predicate. Every path that reaches the allocation has passed a length
        // bound, so this stays silent (must not over-tighten on length-derived conjunctions).
        var diagnostics = await RunAsync("""
            static class C {
                static byte[] DecodeFrame(ReadOnlySpan<byte> header) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length < 0 || length > header.Length)
                        throw new InvalidDataException("corrupt");
                    return new byte[length];
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task InlineRelationalThrowGuard_IsNotFlagged()
    {
        var diagnostics = await RunAsync("""
            static class C {
                static byte[] DecodeFrame(ReadOnlySpan<byte> input) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(input);
                    if (length > input.Length) throw new InvalidDataException("corrupt");
                    return new byte[length];
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task NonThrowExitGuard_ReturnOnCorruptFrame_IsNotFlagged()
    {
        var diagnostics = await RunAsync("""
            static class C {
                static byte[]? DecodeFrame(ReadOnlySpan<byte> input) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(input);
                    if (length > input.Length) return null;
                    return new byte[length];
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task DerivedGuardVariable_IsNotFlagged()
    {
        // The guard compares an intermediate (requiredBytes) derived from the read length, never the
        // length symbol directly — forward taint closure must connect them. The product is widened
        // to 64 bits: an unchecked 32-bit `count * 4` wraps for a forged count >= 2^29 and bounds
        // nothing (pinned flagged in BoundShapeCases).
        var diagnostics = await RunAsync("""
            static class C {
                static int[] DecodeFrame(ReadOnlySpan<byte> input) {
                    var count = BinaryPrimitives.ReadInt32LittleEndian(input);
                    var requiredBytes = (long)count * 4;
                    if (requiredBytes > input.Length) throw new InvalidDataException("corrupt");
                    return new int[count];
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task GuardNamedCallReceivingLength_IsNotFlagged()
    {
        var diagnostics = await RunAsync("""
            static class C {
                static void ValidateExtent(ReadOnlySpan<byte> input, int count) {
                    if (count > input.Length) throw new InvalidDataException("corrupt");
                }
                static byte[] DecodeFrame(ReadOnlySpan<byte> input) {
                    var count = BinaryPrimitives.ReadInt32LittleEndian(input);
                    ValidateExtent(input, count);
                    return new byte[count];
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task AbsoluteCapCheck_IsNotFlagged()
    {
        // A constant-column / zero-payload path with no payload-derived ceiling carries an explicit
        // absolute-cap check; the analyzer treats it as a satisfying bound.
        var diagnostics = await RunAsync("""
            static class C {
                const int MaxRows = 1_000_000;
                static decimal[] DecodeConstant(ReadOnlySpan<byte> input, int count) {
                    if (count > MaxRows) throw new InvalidDataException("corrupt");
                    return new decimal[count];
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task BoundedConsumeCall_PassesLengthWithPayload_IsNotFlagged()
    {
        var diagnostics = await RunAsync("""
            static class C {
                static decimal[] DecodeFull(ReadOnlySpan<byte> input, int total) {
                    if (total > input.Length) throw new InvalidDataException("corrupt");
                    return new decimal[total];
                }
                static decimal[] DecodeSlice(ReadOnlySpan<byte> input, int firstRowIndex, int count) {
                    var full = DecodeFull(input, firstRowIndex + count);
                    return new decimal[count];
                }
            }
            """);

        // DecodeSlice's `new decimal[count]` rides on DecodeFull having validated firstRowIndex+count
        // against the same payload first.
        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task SixteenBitLength_IsNotFlagged()
    {
        // A ushort length tops out at 65 535 — not an out-of-memory vector.
        var diagnostics = await RunAsync("""
            static class C {
                static byte[] DecodeFrame(ReadOnlySpan<byte> input) {
                    var count = BinaryPrimitives.ReadUInt16LittleEndian(input);
                    return new byte[count];
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ConstantSize_IsNotFlagged()
    {
        var diagnostics = await RunAsync("""
            static class C {
                const int HeaderSize = 16;
                static byte[] DecodeFrame(ReadOnlySpan<byte> input) {
                    return new byte[HeaderSize];
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task BufferLengthSize_IsNotFlagged()
    {
        var diagnostics = await RunAsync("""
            static class C {
                static byte[] DecodeFrame(ReadOnlySpan<byte> input) {
                    return new byte[input.Length];
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task MetadataMember_InEncodeContext_IsNotFlagged()
    {
        // Same descriptor.member shape, but on the write path the descriptor is in-memory state the
        // writer itself built — never an untrusted source.
        var diagnostics = await RunAsync("""
            readonly struct ColumnDescriptor { public long ByteLength { get; init; } }
            static class C {
                static byte[] BuildColumn(ColumnDescriptor desc) {
                    return new byte[desc.ByteLength];
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task PlainCapacityArgument_NotADecodeMethod_IsNotFlagged()
    {
        // `count` here is an ordinary capacity argument: the method is not decode-shaped and there is
        // no byte-payload sibling parameter, so it is not treated as a decoded length.
        var diagnostics = await RunAsync("""
            static class C {
                static byte[] Allocate(int count) {
                    return new byte[count];
                }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// AC-13.3 — BSSG021 bitWidth==0 suppression negative test.
    ///
    /// The <c>ScaledIntegerBitPackedCodec</c> constant-column path (<c>bitWidth == 0</c>) has no
    /// payload-derived ceiling for the decoded <c>count</c> — every row maps to zero bytes in the
    /// body, so the payload carries no bound on count. Instead it uses a named
    /// <c>BoundConstantColumnRows</c> call (a <c>Bound*</c>-prefixed guard) that enforces an
    /// absolute ceiling against the caller-supplied segment-level maximum.
    ///
    /// This test verifies that the <c>Bound*</c> guard prefix is recognised as a satisfying
    /// guard by BSSG021, so the constant-column allocation does NOT produce a diagnostic.
    /// If the recogniser dropped <c>Bound</c> from its guard-name list, this test would go RED.
    /// </summary>
    [Fact]
    public async Task BoundNamedCall_BeforeAllocation_BitWidthZeroPattern_IsNotFlagged()
    {
        // Models the ScaledIntegerBitPackedCodec bitWidth==0 decode pattern:
        //   - decode-shaped method (Decode*, ReadOnlySpan<byte> + int payload siblings)
        //   - count from a method parameter (footer-supplied, attacker-controllable)
        //   - BoundConstantColumnRows (Bound*-prefix) called before new T[count]
        var diagnostics = await RunAsync("""
            static class C {
                static void BoundConstantColumnRows(long requested, long maxRows) {
                    if (requested > maxRows) throw new InvalidDataException("constant column count exceeds segment ceiling");
                }
                static decimal[] DecodeConstantColumn(ReadOnlySpan<byte> payload, int count, long maxDecodableRows) {
                    BoundConstantColumnRows((long)count, maxDecodableRows);
                    return new decimal[count];
                }
            }
            """);

        diagnostics.Should().BeEmpty(
            "a Bound*-prefixed guard call before the allocation must silence BSSG021 — "
            + "this is the constant-column bitWidth==0 suppression pattern used by "
            + "ScaledIntegerBitPackedCodec");
    }

    // ─────────────────────────────── guard dominance (table) ───────────────────────────────

    // Helper guards every case may call. None of them is decode-shaped, so their own parameters
    // are not taint sources; each throws when the length exceeds the limit.
    private const string DominanceHelpers = """
        sealed class Limits {
            public void CheckWithin(long length, long limit) { if (length > limit) throw new InvalidDataException("corrupt"); }
        }
        static class G {
            public static void EnsureWithin(long length, long limit) { if (length > limit) throw new InvalidDataException("corrupt"); }
            public static int EnsureWithinValue(long length, long limit) { if (length > limit) throw new InvalidDataException("corrupt"); return 0; }
            public static bool CheckOk(long length, long limit) => length <= limit;
        }
        """;

    /// <summary>
    /// Every syntactic shape in which a bound can appear before a decoded-length allocation,
    /// paired with whether the bound runs (and exits) on every path that reaches it. A bound
    /// that some path skips — the other branch, a short-circuit, a null-conditional, a switch
    /// arm, a loop body or incrementor, a lambda, a `goto` over it, a `catch` that swallows its
    /// throw, an exit that is itself conditional, or a gate in the condition — must leave the
    /// allocation flagged. The expected value comes from C# evaluation semantics, not from the
    /// analyzer: a "1" row has a concrete input (the gate false / the branch not taken) on which
    /// the forged length reaches the allocation with no bound evaluated.
    /// </summary>
    public static TheoryData<string, string, int> DominanceCases() => new()
    {
        // ── must flag: a path reaches the allocation without the bound ──
        { "if-guard in then-branch, alloc in else-branch", """
            if (strict) { if (length > header.Length) throw new InvalidDataException("corrupt"); return Array.Empty<byte>(); }
            else { return new byte[length]; }
            """, 1 },
        { "guard call in then-branch, alloc in else-branch", """
            if (strict) { G.EnsureWithin(length, header.Length); return Array.Empty<byte>(); }
            else { return new byte[length]; }
            """, 1 },
        { "null-conditional guard call", """
            limits?.CheckWithin(length, header.Length);
            return new byte[length];
            """, 1 },
        { "guard call in a switch-expression arm", """
            var ok = mode switch { 1 => G.EnsureWithinValue(length, header.Length), _ => 0 };
            return new byte[length];
            """, 1 },
        { "guard call on the right of ??=", """
            cached ??= G.EnsureWithinValue(length, header.Length);
            return new byte[length];
            """, 1 },
        { "guard call on the right of &&", """
            _ = strict && G.CheckOk(length, header.Length);
            return new byte[length];
            """, 1 },
        { "guard call in a conditional-expression arm", """
            var ok = strict ? G.EnsureWithinValue(length, header.Length) : 0;
            return new byte[length];
            """, 1 },
        { "goto jumps over the guard", """
            if (strict) goto alloc;
            G.EnsureWithin(length, header.Length);
            alloc:
            return new byte[length];
            """, 1 },
        { "try/catch swallows the if-guard throw", """
            try { if (length > header.Length) throw new InvalidDataException("corrupt"); } catch (InvalidDataException) { }
            return new byte[length];
            """, 1 },
        { "try/catch swallows the guard-call throw", """
            try { G.EnsureWithin(length, header.Length); } catch (InvalidDataException) { }
            return new byte[length];
            """, 1 },
        { "guard in a second for incrementor", """
            for (var i = 0; i < mode; i++, G.EnsureWithin(length, header.Length)) { }
            return new byte[length];
            """, 1 },
        { "guard in a while body", """
            while (mode-- > 0) { G.EnsureWithin(length, header.Length); }
            return new byte[length];
            """, 1 },
        { "guard in a switch-statement section", """
            switch (mode) { case 1: G.EnsureWithin(length, header.Length); break; }
            return new byte[length];
            """, 1 },
        { "guard inside a lambda that never runs here", """
            var limit = header.Length;
            Action check = () => G.EnsureWithin(length, limit);
            return new byte[length];
            """, 1 },
        { "guard call nested in a CanSeek if", """
            if (stream.CanSeek) G.EnsureWithin(length, stream.Length);
            return new byte[length];
            """, 1 },
        { "bound gated by ?: on CanSeek", """
            if (stream.CanSeek ? length > stream.Length - stream.Position : false) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "bound gated by non-short-circuit & on CanSeek", """
            if (stream.CanSeek & length > stream.Length - stream.Position) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "bound gated by && on CanSeek", """
            if (stream.CanSeek && length > stream.Length - stream.Position) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "bound inside a switch-expression condition", """
            if (mode switch { 1 => length > header.Length, _ => false }) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "exit inside the bound's branch is itself conditional", """
            if (length > header.Length) { if (strict) throw new InvalidDataException("corrupt"); }
            return new byte[length];
            """, 1 },
        { "exit inside the bound's branch is swallowed by try/catch", """
            if (length > header.Length) { try { throw new InvalidDataException("corrupt"); } catch (InvalidDataException) { } }
            return new byte[length];
            """, 1 },

        // ── must stay silent: the bound runs and exits on every path ──
        { "unconditional if-throw", """
            if (length > header.Length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "block-bodied unconditional exit", """
            if (length > header.Length) { var message = "corrupt"; throw new InvalidDataException(message); }
            return new byte[length];
            """, 0 },
        { "negated bound", """
            if (!(length <= header.Length)) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "else-branch exit", """
            if (length <= header.Length) { } else { throw new InvalidDataException("corrupt"); }
            return new byte[length];
            """, 0 },
        { "|| with an orthogonal operand still bounds", """
            if (strict || length > header.Length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "| over two bounds", """
            if (length < 0 | length > header.Length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "unconditional guard call", """
            G.EnsureWithin(length, header.Length);
            return new byte[length];
            """, 0 },
        { "plain member guard call", """
            limits.CheckWithin(length, header.Length);
            return new byte[length];
            """, 0 },
        { "guard call in a declaration initializer", """
            var checkedLength = G.EnsureWithinValue(length, header.Length);
            return new byte[length];
            """, 0 },
        { "guard call in an if condition", """
            if (!G.CheckOk(length, header.Length)) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "guard call inside try/finally", """
            try { G.EnsureWithin(length, header.Length); } finally { mode = 0; }
            return new byte[length];
            """, 0 },
        { "guard call inside a using block", """
            using (var scratch = new MemoryStream()) { G.EnsureWithin(length, header.Length); }
            return new byte[length];
            """, 0 },
        { "guard and alloc in the same else-branch", """
            if (strict) { return Array.Empty<byte>(); }
            else { G.EnsureWithin(length, header.Length); return new byte[length]; }
            """, 0 },
        { "guard and alloc in the same switch section", """
            switch (mode) { case 1: G.EnsureWithin(length, header.Length); return new byte[length]; }
            return Array.Empty<byte>();
            """, 0 },
        { "guard on the left of &&", """
            _ = G.CheckOk(length, header.Length) && strict;
            return new byte[length];
            """, 0 },
    };

    /// <summary>
    /// The comparison a bound check makes, independent of where it sits. A relational test of the
    /// decoded length bounds the allocation only when its exit fires exactly when the length is
    /// ABOVE a ceiling that does not itself derive from the length: <c>length &gt; ceiling</c> (or
    /// <c>ceiling &lt; length</c>) exiting, or the negated form exiting from the else branch. The
    /// expected value comes from integer ordering, not from the analyzer: each "1" row names a
    /// forged length (e.g. <c>int.MaxValue</c>) that passes the check and reaches the allocation.
    /// </summary>
    public static TheoryData<string, string, int> BoundShapeCases() => new()
    {
        // ── must flag: the comparison lets an arbitrarily large length through ──
        { "sign-only check (length < 0 exits)", """
            if (length < 0) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "sign-only check spelled ThrowIf-free (0 > length exits)", """
            if (0 > length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "inverted bound (length < header.Length exits)", """
            if (length < header.Length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "inverted bound with the ceiling on the left (header.Length > length exits)", """
            if (header.Length > length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "zero early-out spelled relationally (length <= 0 returns)", """
            if (length <= 0) return Array.Empty<byte>();
            return new byte[length];
            """, 1 },
        { "comparison of the length with itself", """
            if (length > length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "ceiling derived from the length itself", """
            var twice = length * 2;
            if (length > twice) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "negated upper bound exits on the in-range side", """
            if (!(length > header.Length)) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "else-branch exit on the violating side inverted", """
            if (length > header.Length) { } else { throw new InvalidDataException("corrupt"); }
            return new byte[length];
            """, 1 },
        { "subtracted length compared upward (header.Length - length > 0 exits)", """
            if (header.Length - length > 0) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "sign-only || sign-only", """
            if (length < 0 || length < -1) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "32-bit sum wraps (length + 4 > header.Length)", """
            if (length + 4 > header.Length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "32-bit sum wraps with the offset first (4 + length > header.Length)", """
            if (4 + length > header.Length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "32-bit product wraps (length * 4 > header.Length)", """
            if (length * 4 > header.Length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },
        { "32-bit product wraps through an intermediate", """
            var requiredBytes = length * 4;
            if (requiredBytes > header.Length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 1 },

        // ── must stay silent: the exit fires whenever the length exceeds a trusted ceiling ──
        { "upper bound vs payload", """
            if (length > header.Length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "ceiling on the left (header.Length < length exits)", """
            if (header.Length < length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "inclusive upper bound", """
            if (length >= header.Length + 1) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "stream remaining ceiling", """
            if (length > stream.Length - stream.Position) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "constant cap", """
            if (length > 1 << 20) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "sign check || upper bound", """
            if (length < 0 || length > header.Length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "offset plus length against the payload, widened to 64 bits", """
            if (4L + length > header.Length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "64-bit product through an intermediate", """
            var requiredBytes = (long)length * 4;
            if (requiredBytes > header.Length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "checked 32-bit sum (overflow throws instead of wrapping)", """
            if (checked(length + 4) > header.Length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "remaining minus length below zero exits", """
            if (header.Length - length < 0) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "negated in-range test", """
            if (!(length <= header.Length)) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "in-range test with else exit", """
            if (length <= header.Length) { } else { throw new InvalidDataException("corrupt"); }
            return new byte[length];
            """, 0 },
    };

    [Theory]
    [MemberData(nameof(BoundShapeCases))]
    public async Task BoundShape_OnlyAnUpperBoundAgainstATrustedCeilingSilences(string shape, string statements, int expected)
    {
        await AssertDominanceCaseAsync(shape, statements, expected).ConfigureAwait(true);
    }

    /// <summary>
    /// A bound proves the allocated value only while the bounded symbols still hold the value it
    /// compared. A reassignment between the bound and the allocation — straight-line, or inside a
    /// loop that re-runs the allocation but not the bound — allocates a value no check has seen.
    /// The "1" rows each have an input (the second read returns <c>int.MaxValue</c>) on which the
    /// allocation runs with an unchecked length.
    /// </summary>
    public static TheoryData<string, string, int> ReassignmentCases() => new()
    {
        // ── must flag: the allocated value is not the bounded value ──
        { "straight-line re-read after the bound", """
            if (length > header.Length) throw new InvalidDataException("corrupt");
            length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4));
            return new byte[length];
            """, 1 },
        { "compound assignment after the bound", """
            if (length > header.Length) throw new InvalidDataException("corrupt");
            length += mode;
            return new byte[length];
            """, 1 },
        { "increment after the bound", """
            if (length > header.Length) throw new InvalidDataException("corrupt");
            length++;
            return new byte[length];
            """, 1 },
        { "out argument after the bound", """
            if (length > header.Length) throw new InvalidDataException("corrupt");
            int.TryParse("1", out length);
            return new byte[length];
            """, 1 },
        { "guard call, then re-read", """
            G.EnsureWithin(length, header.Length);
            length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4));
            return new byte[length];
            """, 1 },
        { "bound before the loop, re-read inside the loop before the allocation", """
            if (length > header.Length) throw new InvalidDataException("corrupt");
            var last = Array.Empty<byte>();
            for (var i = 0; i < mode; i++) {
                length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4 + 4 * i));
                last = new byte[length];
            }
            return last;
            """, 1 },
        { "bound before the loop, re-read inside the loop after the allocation", """
            if (length > header.Length) throw new InvalidDataException("corrupt");
            var last = Array.Empty<byte>();
            while (mode-- > 0) {
                last = new byte[length];
                length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4));
            }
            return last;
            """, 1 },

        // ── must stay silent: the bound sees the value that is allocated ──
        { "re-read, then bound", """
            length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4));
            if (length > header.Length) throw new InvalidDataException("corrupt");
            return new byte[length];
            """, 0 },
        { "re-read and bound inside the loop", """
            var last = Array.Empty<byte>();
            for (var i = 0; i < mode; i++) {
                length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4 + 4 * i));
                if (length > header.Length) throw new InvalidDataException("corrupt");
                last = new byte[length];
            }
            return last;
            """, 0 },
        { "bound before the loop, loop never reassigns", """
            if (length > header.Length) throw new InvalidDataException("corrupt");
            var last = Array.Empty<byte>();
            for (var i = 0; i < mode; i++) { last = new byte[length]; }
            return last;
            """, 0 },
        { "unrelated local reassigned after the bound", """
            if (length > header.Length) throw new InvalidDataException("corrupt");
            mode = 3;
            return new byte[length];
            """, 0 },
    };

    [Theory]
    [MemberData(nameof(ReassignmentCases))]
    public async Task StaleBound_AReassignmentBetweenBoundAndAllocationLeavesItFlagged(string shape, string statements, int expected)
    {
        await AssertDominanceCaseAsync(shape, statements, expected).ConfigureAwait(true);
    }

    [Theory]
    [MemberData(nameof(DominanceCases))]
    public async Task GuardDominance_FlagsEveryPathThatSkipsTheBound(string shape, string statements, int expected)
    {
        await AssertDominanceCaseAsync(shape, statements, expected).ConfigureAwait(true);
    }

    private static async Task AssertDominanceCaseAsync(string shape, string statements, int expected)
    {
        var source = DominanceHelpers + $$"""
            static class C {
                static byte[] DecodeFrame(Stream stream, ReadOnlySpan<byte> header, bool strict, int mode, int? cached, Limits limits) {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            {{statements}}
                }
            }
            """;

        // A snippet that does not compile would make either expectation vacuous.
        var compilation = TestHelpers.CreateCompilationFromSources(new[] { Preamble + source });
        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty($"the '{shape}' case must compile");

        var diagnostics = await RunAsync(source);

        diagnostics.Should().HaveCount(expected, $"shape '{shape}'");
    }
}
