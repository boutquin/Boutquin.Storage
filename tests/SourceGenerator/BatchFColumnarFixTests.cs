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
namespace Boutquin.Storage.SourceGenerator.Tests;

// Batch-F columnar-generator regression tests (fixes 2 and 3). Shares RunColumnar / the
// columnar attribute source defined on the ColumnarGeneratorTests partial.
public sealed partial class ColumnarGeneratorTests
{
    // ───── Fix 3 — BSSG020 (ColumnarFieldNotEncodable) aborts columnar emission ─────
    //
    // Before the fix, the columnar RegisterSourceOutput abort predicate only covered BSSG002
    // (TypeMustBePartial) and BSSG007 (IncompatibleColumnEncoding). A settable property whose
    // type has no columnar encoding raised BSSG020 but emission proceeded with the column
    // silently dropped — Rehydrate then left that property at default(T) on every read
    // (runtime data loss). When BSSG020 fires, NO columnar source must be emitted for the type.

    [Fact]
    public void Fix3_Bssg020_AbortsColumnarEmission_NoSourceEmitted()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                // Guid has no columnar encoding (not in ColumnFieldTypeCode); settable ⇒ BSSG020.
                public Guid Id { get; init; }
            }
            """;

        var (_, result) = RunColumnar(source);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "BSSG020")
            .Which.Severity.Should().Be(DiagnosticSeverity.Error);

        // The whole point of the fix: emission is aborted so no partial with a lossy
        // ColumnarFieldPlan / Rehydrate is produced.
        result.GeneratedTrees
            .Should().NotContain(
                t => t.FilePath.EndsWith("Bar.Columnar.g.cs", StringComparison.Ordinal),
                "BSSG020 is data-loss-grade and must block columnar source emission");
    }

    // ───── Fix 2 (REFUTED) — the columnar sort comparer for DateTimeOffset / Guid keeps
    // Comparer<T>.Default semantics, matching the reflection fallback. ─────
    //
    // The finding claimed the generated comparer should switch to OrderedByteHelpers byte-lex
    // for DateTimeOffset / Guid so zone-map pruning agrees. Verification refuted the premise:
    // neither DateTimeOffset nor Guid is a columnar-storable type (absent from ColumnFieldTypeCode,
    // ChooseDefault returns null, no ColumnEncoding accepts them), so they NEVER carry zone maps —
    // the named "wrongly skip blocks" failure cannot occur. They appear only as SORT keys, and the
    // generated comparer (Comparer<T>.Default) already agrees with the reflection fallback
    // (DateTimeOffset.CompareTo / Guid.CompareTo). Switching to byte-lex would BREAK that agreement.
    // This test pins the no-change disposition: a get-only DateTimeOffset / Guid sort property emits
    // a Comparer<T>.Default.Compare switch arm, not a byte-lex call.

    [Fact]
    public void Fix2_Refuted_SortComparer_ForDateTimeOffsetAndGuid_UsesComparerDefault_NotByteLex()
    {
        const string source = """
            using System;
            using Boutquin.Storage.Domain.Attributes;
            namespace Test;
            [ColumnarStorable]
            public partial record Bar
            {
                public DateOnly Date { get; init; }
                // Computed get-only ⇒ excluded as a column (derived state) but still a sortable key.
                // (A get-only AUTO-property would be stored state and is rejected with BSSG025.)
                public DateTimeOffset Stamp => DateTimeOffset.UnixEpoch.AddDays(Date.DayNumber);
                public Guid Id => Guid.Empty;
            }
            """;

        var (_, result) = RunColumnar(source);

        var code = result.GeneratedTrees
            .Single(t => t.FilePath.EndsWith("Bar.Columnar.g.cs", StringComparison.Ordinal))
            .ToString();

        // Generated sort arms use the strongly-typed Comparer<T>.Default chain (the same ordering
        // DateTimeOffset.CompareTo / Guid.CompareTo gives — what the reflection fallback uses).
        code.Should().Contain("global::System.Collections.Generic.Comparer<global::System.DateTimeOffset>.Default.Compare");
        code.Should().Contain("global::System.Collections.Generic.Comparer<global::System.Guid>.Default.Compare");

        // It must NOT reach into the ordered-wire byte-lex helpers — those govern [Key] SSTable
        // ordering, an unrelated path with no consumer of the columnar row sort order.
        code.Should().NotContain("CompareDateTimeOffsetByteLex");
        code.Should().NotContain("CompareGuidByteLex");
    }
}
