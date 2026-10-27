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

// Phase 2 tag-validation diagnostics: BSSG010 (missing), BSSG011 (duplicate), BSSG012 (range),
// BSSG013 (reserved collision).
public sealed class TagValidationTests
{
    // BSSG010 — every property on a [StorageSerializable] / [Key] type MUST declare [Tag(N)].
    [Fact]
    public void BSSG010_Fires_When_StorageSerializable_Property_Missing_Tag()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Value(long Amount);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG010");
    }

    [Fact]
    public void BSSG010_Fires_When_Key_Property_Missing_Tag()
    {
        // Phase 2 enforces [Tag] on [Key] too — the ordered-wire emitter in Phase 3 ignores
        // the tag but the compile-time requirement is universal.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct MyKey(long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG010");
    }

    [Fact]
    public void BSSG010_Does_Not_Fire_When_All_Properties_Have_Tag()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Value([property: Tag(1)] long Amount, [property: Tag(2)] string Label);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().NotContain(d => d.Id == "BSSG010");
    }

    // BSSG011 — duplicate tag within a type.
    [Fact]
    public void BSSG011_Fires_On_Duplicate_Tag()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Value(
                [property: Tag(1)] long A,
                [property: Tag(1)] long B);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG011");
    }

    // BSSG012 — tag out of range (≤ 0 or > 2^28 − 1).
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BSSG012_Fires_On_NonPositive_Tag(int tag)
    {
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Value([property: Tag({{tag}})] long A);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG012");
    }

    [Fact]
    public void BSSG012_Fires_On_Tag_Above_Max()
    {
        // 2^28 (268_435_456) is one past the permitted max — tag numbers must fit in the
        // high 28 bits of a 31-bit tag-and-wire-type varint (low 3 bits are wire type).
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Value([property: Tag(268435456)] long A);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG012");
    }

    [Fact]
    public void BSSG012_Allows_Tag_At_Max()
    {
        // 2^28 − 1 = 268_435_455 is the last legal tag.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Value([property: Tag(268435455)] long A);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().NotContain(d => d.Id == "BSSG012");
    }

    // BSSG013 — tag reuses a [ReservedTag(N)] value. Promoted to Error in v2.0.0.
    [Fact]
    public void BSSG013_Fires_When_Tag_Collides_With_ReservedTag()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            [ReservedTag(3)]
            public partial record struct Value([property: Tag(3)] long A);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var bssg013 = runResult.Diagnostics.Where(d => d.Id == "BSSG013").ToList();
        bssg013.Should().NotBeEmpty();
        bssg013[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [Fact]
    public void BSSG013_Does_Not_Fire_When_Tag_Avoids_Reserved()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            [ReservedTag(3)]
            public partial record struct Value([property: Tag(4)] long A);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().NotContain(d => d.Id == "BSSG013");
    }

    // BSSG010 message must name the first unused tag as remediation (CodeAction remediation path).
    [Fact]
    public void BSSG010_Message_Suggests_Next_Unused_Tag()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Value(
                [property: Tag(1)] long A,
                long B);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        var bssg010 = runResult.Diagnostics.SingleOrDefault(d => d.Id == "BSSG010");
        bssg010.Should().NotBeNull();
        bssg010!.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Should()
            .Contain("Tag(2)", "generator must suggest the lowest unused positive tag");
    }
}
