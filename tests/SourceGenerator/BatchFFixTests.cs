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
using System.Globalization;

namespace Boutquin.Storage.SourceGenerator.Tests;

// Regression tests for the batch-F review fixes. Each test pins the specific failure mode the
// corresponding fix addresses; the descriptive constants reference the fix number for traceability.
public sealed partial class BatchFFixTests
{
    // ───── Fix 1 — emit the actual type-declaration keyword, not an unconditional "record struct" ─────
    //
    // Before the fix, AppendTypeDeclaration emitted `partial record struct {name}` for every annotated
    // type. Annotating a `record class` (or `struct` / `class`) therefore produced a second partial
    // declaration whose keyword conflicted with the user's → CS0261 in consumer code. The generated
    // partial must carry the SAME type-declaration keyword the user wrote.

    [Fact]
    public void Fix1_RecordClassKey_EmitsRecordClassPartial_NotRecordStruct()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record class OrderKey([property: Tag(1)] long Id);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);

        var generated = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .Single(s => s.Contains("OrderKey", StringComparison.Ordinal));

        // The emitted partial must match the user's keyword exactly — a "record struct" partial
        // layered on a "record class" declaration is CS0261.
        generated.Should().Contain("partial record class OrderKey");
        generated.Should().NotContain("partial record struct OrderKey");
    }

    [Fact]
    public void Fix1_PlainStructKey_EmitsStructPartial_NotRecordStruct()
    {
        // A plain (non-record) struct on the [Key] path uses no `with` expression — Serialize /
        // Deserialize / CompareTo / Equals do not need record semantics — so it is cleanly
        // supportable. The emitted partial must say `partial struct`, never `partial record struct`.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial struct CoordinateKey
            {
                [Tag(1)]
                public int X { get; set; }
                [Tag(2)]
                public int Y { get; set; }

                public CoordinateKey(int x, int y) { X = x; Y = y; }
            }
            """;

        var (output, runResult) = TestHelpers.RunGenerator(source);

        var generated = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .Single(s => s.Contains("CoordinateKey", StringComparison.Ordinal));

        generated.Should().Contain("partial struct CoordinateKey");
        generated.Should().NotContain("partial record struct CoordinateKey");

        // And the merged compilation must be free of the keyword-mismatch error.
        output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().NotContain(d => d.Id == "CS0261");
    }

    [Fact]
    public void Fix1_RecordClassStorageSerializable_EmitsRecordClassPartial()
    {
        // [StorageSerializable] record class: this test pins only the declaration keyword
        // (a "record struct" partial layered on a "record class" declaration is CS0261). The
        // ProjectTo *runtime* behaviour for a record class is pinned separately by
        // ProjectToEmissionTests.StorageSerializable_RecordClass_ProjectTo_Subset_ReturnsPopulatedInstance:
        // default(TSelf) is null for a record class, so the body seeds __result via a non-null
        // `this with { ... = default }` clone rather than default(TSelf), which would NRE.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record class Quote([property: Tag(1)] double Price);
            """;

        var (output, runResult) = TestHelpers.RunGenerator(source);

        var generated = runResult.GeneratedTrees
            .Select(t => t.ToString())
            .Single(s => s.Contains("Quote", StringComparison.Ordinal));

        generated.Should().Contain("partial record class Quote");
        generated.Should().NotContain("partial record struct Quote");

        output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().NotContain(d => d.Id == "CS0261");
    }

    // ───── Fix 9 — BSSG003 message states the inherited-property scanning limitation ─────

    [Fact]
    public void Fix9_Bssg003_Message_MentionsInheritedPropertyLimitation()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            public partial class Base
            {
                [Tag(1)]
                public string Inherited { get; set; }
            }
            [StorageSerializable]
            public partial class Derived : Base
            {
                [Tag(2)]
                public string Direct { get; set; }
            }
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);

        var bssg003 = runResult.Diagnostics.Single(d => d.Id == "BSSG003");
        bssg003.GetMessage(CultureInfo.InvariantCulture)
            .Should().Contain("inherited", "the BSSG003 message must warn that inherited properties are not scanned");
    }

    // ───── Fix 5 — ToDiagnostic carries the descriptor's real title, not the rule id ─────

    [Fact]
    public void Fix5_ProducedDiagnostic_HasDescriptorTitle_NotRuleId()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct MissingTagType([property: Tag(1)] long A, long B);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);

        // BSSG010 (MissingTag) fires on property B. Its rendered descriptor must carry the
        // human-readable title, not "BSSG010" duplicated into the title slot.
        var diag = runResult.Diagnostics.Single(d => d.Id == "BSSG010");
        var title = diag.Descriptor.Title.ToString(CultureInfo.InvariantCulture);
        title.Should().Be(DiagnosticDescriptors.MissingTag.Title.ToString(CultureInfo.InvariantCulture));
        title.Should().NotBe("BSSG010");
    }
}
