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

// Structural tests for StorageDiagnosticSuppressor. The suppressor's load-bearing
// contract for MarketData consumers is:
//   1. Declare a CA1036-suppression descriptor (BSSGS001)
//   2. Declare an S1210-suppression descriptor (BSSGS002)
//   3. Both descriptors carry justifications that mention the source generator
//
// A full analyzer-pipeline integration test would need to reproduce the exact
// `Boutquin.Storage.Domain.Attributes.KeyAttribute` metadata-symbol-resolution
// behaviour the suppressor depends on — which conflicts with the test harness
// that declares a [Conditional]-stripped stub of that attribute. The structural
// tests below verify the surface that ships to consumers.
public sealed class StorageDiagnosticSuppressorTests
{
    [Fact]
    public void SupportedSuppressions_HasExactlyTwoEntries()
    {
        var suppressor = new StorageDiagnosticSuppressor();
        suppressor.SupportedSuppressions.Should().HaveCount(2,
            "the suppressor's published surface is BSSGS001 (CA1036) + BSSGS002 (S1210); " +
            "any third entry must be deliberate and pinned by this test");
    }

    [Fact]
    public void SupportedSuppressions_BSSGS001_SuppressesCa1036()
    {
        var suppressor = new StorageDiagnosticSuppressor();
        var bssgs001 = suppressor.SupportedSuppressions.FirstOrDefault(s => s.Id == "BSSGS001");
        bssgs001.Should().NotBeNull("BSSGS001 is the CA1036 suppression descriptor");
        bssgs001!.SuppressedDiagnosticId.Should().Be("CA1036");
    }

    [Fact]
    public void SupportedSuppressions_BSSGS002_SuppressesS1210()
    {
        var suppressor = new StorageDiagnosticSuppressor();
        var bssgs002 = suppressor.SupportedSuppressions.FirstOrDefault(s => s.Id == "BSSGS002");
        bssgs002.Should().NotBeNull("BSSGS002 is the S1210 suppression descriptor");
        bssgs002!.SuppressedDiagnosticId.Should().Be("S1210");
    }

    [Fact]
    public void Ca1036Descriptor_HasJustificationMentioningGenerator()
    {
        var suppressor = new StorageDiagnosticSuppressor();
        var bssgs001 = suppressor.SupportedSuppressions.First(s => s.Id == "BSSGS001");
        bssgs001.Justification.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Contain("source generator",
            "the justification must explain WHY CA1036 is spurious on [Key] types");
    }

    [Fact]
    public void S1210Descriptor_HasJustificationMentioningGenerator()
    {
        var suppressor = new StorageDiagnosticSuppressor();
        var bssgs002 = suppressor.SupportedSuppressions.First(s => s.Id == "BSSGS002");
        bssgs002.Justification.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Contain("source generator");
    }

    [Fact]
    public void Suppressor_IsConcreteSuppressor_NotAnalyzer()
    {
        var suppressor = new StorageDiagnosticSuppressor();
        // The base type chain locks the integration: Roslyn dispatches DiagnosticSuppressor
        // and DiagnosticAnalyzer on different code paths. Marking this as DiagnosticSuppressor
        // is what makes the BSSGS001/BSSGS002 entries take effect rather than fire as new
        // diagnostics.
        suppressor.Should().BeAssignableTo<Microsoft.CodeAnalysis.Diagnostics.DiagnosticSuppressor>();
    }

    [Fact]
    public void Suppressor_HasDiagnosticAnalyzerAttribute()
    {
        // Roslyn discovers suppressors via the [DiagnosticAnalyzer] attribute even though
        // they don't analyze. Without the attribute the suppression engine wouldn't load
        // the type at all.
        var attr = typeof(StorageDiagnosticSuppressor).GetCustomAttributes(
            typeof(Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzerAttribute), inherit: false);
        attr.Should().HaveCountGreaterThan(0,
            "[DiagnosticAnalyzer] is required for Roslyn to discover the suppressor");
    }

    [Fact]
    public void Suppressor_DiagnosticAnalyzerAttribute_TargetsCSharp()
    {
        var attrs = typeof(StorageDiagnosticSuppressor).GetCustomAttributes(
            typeof(Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzerAttribute), inherit: false);
        var attr = (Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzerAttribute)attrs[0];
        attr.Languages.Should().Contain(LanguageNames.CSharp);
    }

    [Fact]
    public void SupportedSuppressions_IdsAreNonEmpty()
    {
        var suppressor = new StorageDiagnosticSuppressor();
        foreach (var s in suppressor.SupportedSuppressions)
        {
            s.Id.Should().NotBeNullOrWhiteSpace();
            s.SuppressedDiagnosticId.Should().NotBeNullOrWhiteSpace();
            s.Justification.ToString(System.Globalization.CultureInfo.InvariantCulture).Should().NotBeNullOrWhiteSpace();
        }
    }
}
