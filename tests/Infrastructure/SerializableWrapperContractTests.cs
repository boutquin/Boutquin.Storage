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

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the key contract of <see cref="SerializableWrapper{T}"/>: the ordering relation
/// (<c>CompareTo</c>), the equality relation (<c>Equals</c>) and the serialized bytes must be one
/// relation. The storage engines order and search by <c>CompareTo</c> but filter point lookups by
/// serialized key bytes (the SSTable bloom filter), so any pair of keys that compare equal while
/// serializing differently — or compare equal while being unequal — loses data.
/// </summary>
public sealed class SerializableWrapperContractTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(), $"SerializableWrapperContract_{Guid.NewGuid():N}");

    public SerializableWrapperContractTests() => Directory.CreateDirectory(_testDirectory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task StringKeys_CanonicallyEquivalentButOrdinallyDistinct_StayDistinctBeforeAndAfterFlush()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            SerializableWrapper<string> precomposed = "Å";
            SerializableWrapper<string> decomposed = "Å";

            precomposed.Equals(decomposed).Should().BeFalse("the two strings are ordinally distinct");
            precomposed.CompareTo(decomposed).Should().NotBe(0,
                "CompareTo must agree with Equals: two keys that are not equal must not compare equal");

            using var engine = LsmEngineFactory.Build<SerializableWrapper<string>, SerializableWrapper<string>>(
                1000, Path.Combine(_testDirectory, "nfc"), "k");
            await engine.SetAsync(precomposed, "value-for-precomposed").ConfigureAwait(true);
            await engine.SetAsync(decomposed, "value-for-decomposed").ConfigureAwait(true);

            (await engine.TryGetValueAsync(precomposed).ConfigureAwait(true)).Value.Value.Should().Be("value-for-precomposed");
            (await engine.TryGetValueAsync(decomposed).ConfigureAwait(true)).Value.Value.Should().Be("value-for-decomposed");

            await engine.FlushAsync().ConfigureAwait(true);

            (await engine.TryGetValueAsync(precomposed).ConfigureAwait(true)).Value.Value.Should().Be("value-for-precomposed");
            (await engine.TryGetValueAsync(decomposed).ConfigureAwait(true)).Value.Value.Should().Be("value-for-decomposed");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task StringKeys_SegmentWrittenUnderOneCulture_IsFullyReadableUnderAnother()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            var keys = new List<string>();
            foreach (var a in "abcdefghijklmnopqrstuvwxyz")
            {
                foreach (var b in "abcdefghijklmnopqrstuvwxyz")
                {
                    keys.Add(string.Concat(a, b));
                }
            }

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            using var engine = LsmEngineFactory.Build<SerializableWrapper<string>, SerializableWrapper<string>>(
                100_000, Path.Combine(_testDirectory, "culture"), "k");
            foreach (var k in keys)
            {
                await engine.SetAsync(k, "v-" + k).ConfigureAwait(true);
            }

            await engine.FlushAsync().ConfigureAwait(true);

            // Danish collation sorts "aa" after "z"; the persisted order must not depend on it.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("da-DK");
            var missed = new List<string>();
            foreach (var k in keys)
            {
                var (value, found) = await engine.TryGetValueAsync(k).ConfigureAwait(true);
                if (!found || value.Value != "v-" + k)
                {
                    missed.Add(k);
                }
            }

            missed.Should().BeEmpty("key order must be a property of the key bytes, not of the process culture");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void StringValues_UnderInterfaceTypeArgument_CompareIndependentlyOfCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("da-DK");
            var aa = new SerializableWrapper<IComparable>("aa");
            var zz = new SerializableWrapper<IComparable>("zz");

            Math.Sign(aa.CompareTo(zz)).Should().Be(-1,
                "a runtime string compares by code point even when T is an interface");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void DoubleKeys_ThatCompareEqual_SerializeToIdenticalBytes()
    {
        var negativeZero = new SerializableWrapper<double>(-0.0);
        var positiveZero = new SerializableWrapper<double>(0.0);
        var nanWithPayload = new SerializableWrapper<double>(BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0001UL));
        var defaultNan = new SerializableWrapper<double>(double.NaN);

        negativeZero.CompareTo(positiveZero).Should().Be(0);
        nanWithPayload.CompareTo(defaultNan).Should().Be(0);

        Bytes(negativeZero).Should().Equal(Bytes(positiveZero),
            "keys that CompareTo reports equal must serialize to the same bytes the bloom filter is keyed on");
        Bytes(nanWithPayload).Should().Equal(Bytes(defaultNan),
            "keys that CompareTo reports equal must serialize to the same bytes the bloom filter is keyed on");
    }

    [Fact]
    public void FloatKeys_ThatCompareEqual_SerializeToIdenticalBytes()
    {
        var negativeZero = new SerializableWrapper<float>(-0.0f);
        var positiveZero = new SerializableWrapper<float>(0.0f);
        var nanWithPayload = new SerializableWrapper<float>(BitConverter.UInt32BitsToSingle(0x7FC0_0001U));
        var defaultNan = new SerializableWrapper<float>(float.NaN);

        negativeZero.CompareTo(positiveZero).Should().Be(0);
        nanWithPayload.CompareTo(defaultNan).Should().Be(0);

        Bytes(negativeZero).Should().Equal(Bytes(positiveZero),
            "keys that CompareTo reports equal must serialize to the same bytes the bloom filter is keyed on");
        Bytes(nanWithPayload).Should().Equal(Bytes(defaultNan),
            "keys that CompareTo reports equal must serialize to the same bytes the bloom filter is keyed on");
    }

    [Fact]
    public async Task DoubleKeys_StoredUnderNegativeZero_AreFoundUnderPositiveZeroAfterFlush()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<double>, SerializableWrapper<string>>(
            1000, Path.Combine(_testDirectory, "zero"), "k");
        await engine.SetAsync(-0.0, "stored-under-negative-zero").ConfigureAwait(true);

        (await engine.TryGetValueAsync(0.0).ConfigureAwait(true)).Found.Should().BeTrue();

        await engine.FlushAsync().ConfigureAwait(true);

        var (value, found) = await engine.TryGetValueAsync(0.0).ConfigureAwait(true);
        found.Should().BeTrue("a lookup that hits in the memtable must still hit once the key is flushed");
        value.Value.Should().Be("stored-under-negative-zero");
    }

    private static byte[] Bytes<T>(SerializableWrapper<T> wrapper)
    {
        using var stream = new MemoryStream();
        wrapper.Serialize(stream);
        return stream.ToArray();
    }
}
