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
/// Verifies naming conventions across Domain and Infrastructure assemblies.
/// </summary>
public sealed class NamingConventionTests : BaseArchitectureTest
{
    [Fact]
    public void Interfaces_ShouldStartWithI()
    {
        foreach (var assembly in AllAssemblies)
        {
            var result = Types.InAssembly(assembly)
                .That()
                .AreInterfaces()
                .Should()
                .HaveNameStartingWith("I")
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                because: $"interfaces in {assembly.GetName().Name} must start with 'I': [{GetFailingTypes(result)}]");
        }
    }

    [Fact]
    public void StorageEngines_ShouldEndWithStorageEngine()
    {
        // Types that implement IStorageEngine transitively (via IBulkStorageEngine) but
        // are not top-level storage engines: wrappers, internal components, and in-memory stores
        var predicate = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ImplementInterface(typeof(Domain.Interfaces.IStorageEngine<,>))
            .And()
            .DoNotHaveNameStartingWith("InMemoryKeyValueStore") // cache/MemTable, not a storage engine
            .And()
            .DoNotHaveNameStartingWith("BulkKeyValueStoreWith") // decorator wrapper
            .And()
            .DoNotHaveNameStartingWith("LogSegmentFile"); // internal component of LogSegmentedStorageEngine

        predicate.GetTypes()
            .Should().NotBeEmpty(
                because: "IStorageEngine must have at least one qualifying implementation in Infrastructure; " +
                         "an empty selector indicates a rename or namespace move that silently broke the naming guard");

        var result = predicate
            .Should()
            .HaveNameEndingWith("StorageEngine")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"IStorageEngine implementations must end with 'StorageEngine': [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void CompactionStrategies_ShouldEndWithStrategy()
    {
        var predicate = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ImplementInterface(typeof(Domain.Interfaces.ICompactionStrategy<>));

        predicate.GetTypes()
            .Should().NotBeEmpty(
                because: "ICompactionStrategy must have at least one implementation in Infrastructure; " +
                         "an empty selector indicates a rename or namespace move that silently broke the naming guard");

        var result = predicate
            .Should()
            .HaveNameEndingWith("Strategy")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"ICompactionStrategy<TKey> implementations must end with 'Strategy': [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void HashAlgorithms_ShouldNotEndWithAlgorithm()
    {
        // Hash algorithms should have domain-specific names (Murmur3, XxHash32, Fnv1aHash),
        // not generic suffixes
        var predicate = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ImplementInterface<Domain.Interfaces.IHashAlgorithm>();

        predicate.GetTypes()
            .Should().NotBeEmpty(
                because: "IHashAlgorithm must have at least one implementation in Infrastructure; " +
                         "an empty selector indicates a rename or namespace move that silently broke the naming guard");

        var result = predicate
            .Should()
            .NotHaveNameEndingWith("Algorithm")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"IHashAlgorithm implementations should have domain-specific names: [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void ObjectStores_ShouldEndWithObjectStore()
    {
        var predicate = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ImplementInterface<Domain.Interfaces.ObjectStore.IObjectStore>();

        predicate.GetTypes()
            .Should().NotBeEmpty(
                because: "IObjectStore must have at least one implementation in Infrastructure; " +
                         "an empty selector indicates a rename or namespace move that silently broke the naming guard");

        var result = predicate
            .Should()
            .HaveNameEndingWith("ObjectStore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"IObjectStore implementations must end with 'ObjectStore': [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void CheckpointStores_ShouldEndWithCheckpointStore()
    {
        var predicate = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ImplementInterface<Domain.Interfaces.Streaming.ICheckpointStore>();

        predicate.GetTypes()
            .Should().NotBeEmpty(
                because: "ICheckpointStore must have at least one implementation in Infrastructure; " +
                         "an empty selector indicates a rename or namespace move that silently broke the naming guard");

        var result = predicate
            .Should()
            .HaveNameEndingWith("CheckpointStore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"ICheckpointStore implementations must end with 'CheckpointStore': [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void EventLogs_ShouldEndWithEventLog()
    {
        var predicate = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ImplementInterface(typeof(Domain.Interfaces.Events.IEventLog<>));

        predicate.GetTypes()
            .Should().NotBeEmpty(
                because: "IEventLog must have at least one implementation in Infrastructure; " +
                         "an empty selector indicates a rename or namespace move that silently broke the naming guard");

        var result = predicate
            .Should()
            .HaveNameEndingWith("EventLog")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"IEventLog implementations must end with 'EventLog': [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void TimeSeriesStores_ShouldEndWithTimeSeriesStore()
    {
        var predicate = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ImplementInterface(typeof(Domain.Interfaces.TimeSeries.ITimeSeriesStore<,>));

        predicate.GetTypes()
            .Should().NotBeEmpty(
                because: "ITimeSeriesStore must have at least one implementation in Infrastructure; " +
                         "an empty selector indicates a rename or namespace move that silently broke the naming guard");

        var result = predicate
            .Should()
            .HaveNameEndingWith("TimeSeriesStore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"ITimeSeriesStore implementations must end with 'TimeSeriesStore': [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void DatasetCoverageIndexes_ShouldEndWithDatasetCoverageIndex()
    {
        var predicate = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ImplementInterface(typeof(Domain.Interfaces.TimeSeries.IDatasetCoverageIndex<>));

        predicate.GetTypes()
            .Should().NotBeEmpty(
                because: "IDatasetCoverageIndex must have at least one implementation in Infrastructure; " +
                         "an empty selector indicates a rename or namespace move that silently broke the naming guard");

        var result = predicate
            .Should()
            .HaveNameEndingWith("DatasetCoverageIndex")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"IDatasetCoverageIndex implementations must end with 'DatasetCoverageIndex': [{GetFailingTypes(result)}]");
    }

    [Fact]
    public void Serializers_ShouldEndWithSerializer()
    {
        var predicate = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ImplementInterface(typeof(Domain.Interfaces.IEntrySerializer<,>));

        predicate.GetTypes()
            .Should().NotBeEmpty(
                because: "IEntrySerializer must have at least one implementation in Infrastructure; " +
                         "an empty selector indicates a rename or namespace move that silently broke the naming guard");

        var result = predicate
            .Should()
            .HaveNameEndingWith("Serializer")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            because: $"IEntrySerializer implementations must end with 'Serializer': [{GetFailingTypes(result)}]");
    }
}
