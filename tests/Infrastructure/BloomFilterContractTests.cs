// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

#nullable disable

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the filter-identity contract between a Bloom filter and the store it fronts: the store
/// finds a key through <c>CompareTo</c>, the filter hashes the key's serialized bytes, so equal
/// keys must serialize identically or the decorator reports a stored key absent (a false
/// negative, the one answer a Bloom filter may never give).
/// </summary>
public sealed class BloomFilterContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"BloomContract_{Guid.NewGuid():N}");
    private int _fileCounter;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<(BulkKeyValueStoreWithBloomFilter<SerializableWrapper<T>, SerializableWrapper<int>> Decorator,
        AppendOnlyFileStorageEngine<SerializableWrapper<T>, SerializableWrapper<int>> Inner)> NewStoreAsync<T>()
    {
        var storageFile = new StorageFile(_directory, $"bloom-contract-{_fileCounter++}.dat");
        var inner = new AppendOnlyFileStorageEngine<SerializableWrapper<T>, SerializableWrapper<int>>(
            storageFile,
            new BinaryEntrySerializer<SerializableWrapper<T>, SerializableWrapper<int>>());
        var filter = BloomFilter.ForSerializable<SerializableWrapper<T>>(expectedElements: 100, falsePositiveProbability: 0.001);
        var decorator = await BulkKeyValueStoreWithBloomFilter<SerializableWrapper<T>, SerializableWrapper<int>>
            .CreateAsync(inner, filter);
        return (decorator, inner);
    }

    private async Task AssertDecoratorAgreesWithInnerAsync<T>(T stored, T queried)
    {
        var (decorator, inner) = await NewStoreAsync<T>();
        using var _ = decorator;
        var storedKey = new SerializableWrapper<T>(stored);
        var queriedKey = new SerializableWrapper<T>(queried);
        await decorator.SetAsync(storedKey, 7);

        var innerFound = (await inner.TryGetValueAsync(queriedKey)).Found;
        var innerContains = await inner.ContainsKeyAsync(queriedKey);
        var decoratorFound = (await decorator.TryGetValueAsync(queriedKey)).Found;
        var decoratorContains = await decorator.ContainsKeyAsync(queriedKey);

        decoratorFound.Should().Be(innerFound, "the filter must not hide a key the store finds ({0} vs {1})", stored, queried);
        decoratorContains.Should().Be(innerContains);
        (await decorator.TryGetValueAsync(storedKey)).Found.Should().BeTrue();
    }

    [Fact]
    public Task DoubleNegativeZero_StoredAsPositiveZero_DecoratorFindsIt() =>
        AssertDecoratorAgreesWithInnerAsync(0.0d, -0.0d);

    [Fact]
    public Task DoublePositiveZero_StoredAsNegativeZero_DecoratorFindsIt() =>
        AssertDecoratorAgreesWithInnerAsync(-0.0d, 0.0d);

    [Fact]
    public Task FloatNegativeZero_StoredAsPositiveZero_DecoratorFindsIt() =>
        AssertDecoratorAgreesWithInnerAsync(0.0f, -0.0f);

    [Fact]
    public Task DoubleNanPayloads_DecoratorFindsEitherPayload() =>
        AssertDecoratorAgreesWithInnerAsync(
            BitConverter.Int64BitsToDouble(0x7FF8000000000001L),
            BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000123UL)));

    [Fact]
    public Task FloatNanPayloads_DecoratorFindsEitherPayload() =>
        AssertDecoratorAgreesWithInnerAsync(
            BitConverter.Int32BitsToSingle(0x7FC00001),
            BitConverter.Int32BitsToSingle(unchecked((int)0xFFC00123u)));

    [Fact]
    public Task StringsDifferingOnlyByIgnorableCharacter_DecoratorAgreesWithInner() =>
        AssertDecoratorAgreesWithInnerAsync("ab", "a­b");

    [Theory]
    [InlineData("src/Infrastructure/DataStructures/BloomFilter.cs")]
    [InlineData("src/Infrastructure/DataStructures/CountingBloomFilter.cs")]
    [InlineData("src/Infrastructure/StorageWithBloomFilter/BulkKeyValueStoreWithBloomFilter.cs")]
    public async Task FilterSurface_StatesThatEqualKeysMustProduceEqualBytes(string relativePath)
    {
        // A filter hashes bytes while the store matches keys by CompareTo, and the filter cannot
        // canonicalise an arbitrary key type, so "equal keys serialize identically" is an
        // obligation on the key type. A custom key that breaks it makes the decorator answer
        // "absent" for a stored key. The obligation must be stated where an implementer of a key
        // type (or a caller of ForSerializable) reads it, not only in the false-positive direction.
        var source = await File.ReadAllTextAsync(LocateRepoFile(relativePath));
        source.Should().Contain("equal items must produce equal bytes");
    }

    private static string LocateRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Boutquin.Storage.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the repository root is an ancestor of the test output directory");
        return Path.Combine(dir!.FullName, relative);
    }
}
