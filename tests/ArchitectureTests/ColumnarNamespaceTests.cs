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
using Boutquin.Storage.ArchitectureTests.Helpers;
using FluentAssertions;
using NetArchTest.Rules;

namespace Boutquin.Storage.ArchitectureTests;

/// <summary>
/// Architecture guardrails for the columnar time-series format namespace
/// (<c>Boutquin.Storage.Infrastructure.TimeSeries.Columnar</c> and
/// <c>Boutquin.Storage.Domain.*.TimeSeries.Columnar</c>). Pins naming conventions for
/// the pluggable segment-format hierarchy and prevents accidental coupling to
/// unrelated Infrastructure feature namespaces (distributed systems, replication,
/// consensus, etc.) so the columnar subsystem stays self-contained.
/// </summary>
public sealed class ColumnarNamespaceTests : BaseArchitectureTest
{
    private const string CoverletInstrumentationNamespace = "Coverlet.Core.Instrumentation";

    [Fact]
    public void SegmentFormatImplementations_ShouldEndWithSegmentFormat()
    {
        var predicate = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ImplementInterface(typeof(Domain.Interfaces.ISegmentFormat<,>));

        predicate.GetTypes()
            .Should().NotBeEmpty(
                because: "ISegmentFormat must have at least one implementation in Infrastructure; " +
                         "an empty selector indicates a rename or namespace move that silently broke the naming guard");

        var result = predicate
            .Should()
            .HaveNameEndingWith("SegmentFormat")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"ISegmentFormat implementations must end with 'SegmentFormat': [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void ColumnarSegmentFormatImplementations_ShouldEndWithColumnarSegmentFormat()
    {
        // NetArchTest 1.4.5 IsSuccessful returns True on an empty selector (vacuous pass).
        // Guard: assert the candidate set is non-empty before running the naming check so the
        // test fails loudly if a rename or namespace move accidentally produces zero matches.
        var predicate = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ImplementInterface(typeof(Domain.Interfaces.TimeSeries.Columnar.IColumnarSegmentFormat<>));

        predicate.GetTypes()
            .Should().NotBeEmpty(
                because: "IColumnarSegmentFormat must have at least one implementation in Infrastructure; " +
                         "an empty selector indicates a rename or namespace move that silently broke the naming guard");

        var result = predicate
            .Should()
            .HaveNameEndingWith("ColumnarSegmentFormat")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"IColumnarSegmentFormat implementations must end with 'ColumnarSegmentFormat': [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void ColumnarInfrastructure_ShouldNotDependOnUnrelatedFeatures()
    {
        // The columnar subsystem is a self-contained segment-format layer. It must not reach
        // into unrelated Infrastructure feature namespaces — doing so would indicate
        // accidental coupling between orthogonal concerns (e.g., distributed systems,
        // consensus, replication, CRDTs, MVCC) and the on-disk columnar format.
        //
        // Vacuity guard: assert the namespace selector returns at least one type before
        // running the dependency check. NetArchTest 1.4.5 returns IsSuccessful = true on an
        // empty candidate set (trivially-passing vacuous truth), so a rename or namespace move
        // that produces zero matches would silently pass the dependency test with no signal.
        var columnarTypes = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ResideInNamespaceMatching(@"Boutquin\.Storage\.Infrastructure\.TimeSeries\.Columnar.*")
            .And()
            .DoNotResideInNamespace(CoverletInstrumentationNamespace)
            .GetTypes();

        columnarTypes.Should().NotBeEmpty(
            because: "columnar infrastructure types must exist for the dependency isolation test to be meaningful; " +
                     "an empty selector indicates a rename or namespace move that silently broke the isolation guard");

        var result = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ResideInNamespaceMatching(@"Boutquin\.Storage\.Infrastructure\.TimeSeries\.Columnar.*")
            .And()
            .DoNotResideInNamespace(CoverletInstrumentationNamespace)
            .Should()
            .NotHaveDependencyOnAny(
                "Boutquin.Storage.Infrastructure.WriteAheadLog",
                "Boutquin.Storage.Infrastructure.LogSegmentFileStorage",
                "Boutquin.Storage.Infrastructure.StorageWithBloomFilter",
                "Boutquin.Storage.Infrastructure.SortedStringTable",
                "Boutquin.Storage.Infrastructure.Indexing")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"Columnar infrastructure must not depend on unrelated feature namespaces: [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void DomainColumnarNamespaces_ShouldOnlyContainInterfaces_ValueObjects_AndEnums()
    {
        // Types under Boutquin.Storage.Domain.*.TimeSeries.Columnar must obey the Domain
        // namespace-purity rules: interfaces in Interfaces.*, records in ValueObjects.*,
        // enums in Enums. Regression guard for accidental concrete-type placement.
        var offenders = DomainAssembly.GetTypes()
            .Where(t =>
                t.Namespace is { } ns
                && ns.Contains(".TimeSeries.Columnar", StringComparison.Ordinal)
                && t.IsPublic)
            .Where(t =>
            {
                if (t.Namespace!.StartsWith("Boutquin.Storage.Domain.Interfaces", StringComparison.Ordinal))
                {
                    return !t.IsInterface;
                }

                if (t.Namespace.StartsWith("Boutquin.Storage.Domain.ValueObjects", StringComparison.Ordinal))
                {
                    // Record class has <Clone>$; record struct has PrintMembers(StringBuilder).
                    var isRecordClass = t.GetMethod("<Clone>$",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance) != null;
                    var isRecordStruct = t.IsValueType
                        && t.GetMethod("PrintMembers",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                            null, [typeof(System.Text.StringBuilder)], null) != null;
                    return !(isRecordClass || isRecordStruct);
                }

                if (t.Namespace.StartsWith("Boutquin.Storage.Domain.Enums", StringComparison.Ordinal))
                {
                    return !t.IsEnum;
                }

                // Type outside the three permitted Domain sub-namespaces — always an offender.
                return true;
            })
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty(
            "Domain.*.TimeSeries.Columnar types must be interfaces (in Interfaces.*), records (in ValueObjects.*), or enums (in Enums.*)");
    }
}
