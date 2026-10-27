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

// Phase A2 — BSSG017 decimal-scale canonicalisation diagnostics.
public sealed class DecimalScaleValidationTests
{
    [Fact]
    public void BSSG017_Fires_On_Key_Decimal_Property_Without_DecimalScale()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct PriceKey([property: Tag(1)] decimal Price);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG017");
    }

    [Fact]
    public void BSSG017_Fires_On_Key_Nullable_Decimal_Property_Without_DecimalScale()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct PriceKey([property: Tag(1)] decimal? Price);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG017");
    }

    [Fact]
    public void BSSG017_Does_Not_Fire_When_DecimalScale_Declared_In_Range()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct PriceKey([property: Tag(1)] [property: DecimalScale(4)] decimal Price);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().NotContain(d => d.Id == "BSSG017");
    }

    [Fact]
    public void BSSG017_Fires_On_Out_Of_Range_Scale()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct PriceKey([property: Tag(1)] [property: DecimalScale(29)] decimal Price);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG017");
    }

    [Fact]
    public void BSSG017_Does_Not_Fire_On_StorageSerializable_Decimal_Property()
    {
        // Value-side decimals use tagged wire (16-byte decimal.GetBits), which preserves
        // scale in the bits themselves — no canonicalisation needed.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct PriceValue([property: Tag(1)] decimal Amount);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().NotContain(d => d.Id == "BSSG017");
    }

    [Fact]
    public void BSSG017_Does_Not_Fire_On_Key_Without_Decimal_Property()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [Key]
            public partial record struct LongKey([property: Tag(1)] long Value);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().NotContain(d => d.Id == "BSSG017");
    }
}
