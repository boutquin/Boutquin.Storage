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
using System.Reflection;

namespace Boutquin.Storage.SourceGenerator.Tests;

// Phase 3 ordered-wire runtime tests. Compile generator-produced [Key] records into live
// assemblies and exercise:
//   - Per-kind Serialize/Deserialize round-trip across every supported leaf type.
//   - Composite-key round-trip (string + long) so terminator-escape interacts cleanly with
//     declaration-order concatenation.
//   - Property-based: 10_000 random tuples: byte-lex sort of Serialize output
//     matches CompareTo sort of decoded records.
public sealed class OrderedWireTests
{
    // Round-trip for every supported ordered-wire kind. 2.0.0 close-out (Slice A) extends
    // the table to cover all 22 primitives (Tier 1 + Tier 2 + Tier 3 + enums/nullables via
    // the existing dispatch paths). Deferred kinds at this point: none.
    [Theory]
    [InlineData("sbyte", "-42")]
    [InlineData("byte", "200")]
    [InlineData("short", "-12345")]
    [InlineData("ushort", "54321")]
    [InlineData("int", "int.MinValue")]
    [InlineData("uint", "uint.MaxValue")]
    [InlineData("long", "100_000L")]
    [InlineData("ulong", "ulong.MaxValue")]
    [InlineData("bool", "true")]
    [InlineData("char", "'x'")]
    [InlineData("string", "\"hello\\0world\"")]   // Embedded null exercises terminator-escape.
    [InlineData("System.DateOnly", "new System.DateOnly(2026, 4, 21)")]
    [InlineData("System.TimeOnly", "new System.TimeOnly(12, 34, 56)")]
    [InlineData("System.TimeSpan", "System.TimeSpan.FromMinutes(-90)")]
    [InlineData("System.Guid", "new System.Guid(\"12345678-1234-5678-1234-567812345678\")")]
    [InlineData("System.Half", "(System.Half)3.14f")]
    [InlineData("float", "-2.5f")]
    [InlineData("double", "1e100")]
    [InlineData("System.Int128", "System.Int128.MinValue")]
    [InlineData("System.UInt128", "System.UInt128.MaxValue")]
    [InlineData("System.DateTimeOffset", "new System.DateTimeOffset(2026, 4, 22, 12, 0, 0, System.TimeSpan.FromHours(-5))")]
    [InlineData("decimal", "-1234.5678m")]
    public void Key_Primitive_Round_Trips(string clrType, string valueLiteral)
    {
        // Phase A2 — decimal keys require [DecimalScale(N)]. Declare scale 4 here to preserve
        // the test literal -1234.5678m across the round-trip; non-decimal keys do not need it.
        var decimalScaleAttr = clrType == "decimal" ? "[property: DecimalScale(4)] " : string.Empty;
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                public partial record struct K([property: Tag(1)] {{decimalScaleAttr}}{{clrType}} V);

                public static class KFactory
                {
                    public static K Make() => new K({{valueLiteral}});
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var factory = asm.GetType("TestNamespace.KFactory")!;
        var original = factory.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(original);
        var round = Deserialize(asm, "TestNamespace.K", bytes);
        round.Should().Be(original, "ordered-wire round-trip must reconstruct the same value");
    }

    // Composite key with a variable-length string first, then a long. Byte-lex must still
    // match CompareTo on the decoded tuple — this is the main terminator-escape guard.
    [Fact]
    public void Composite_Key_String_Then_Long_Byte_Lex_Matches_Compare()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                public partial record struct CK(
                    [property: Tag(1)] string Region,
                    [property: Tag(2)] long Id);

                public static class CKFactory
                {
                    public static CK Make(string r, long i) => new CK(r, i);
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var factory = asm.GetType("TestNamespace.CKFactory")!;
        var make = factory.GetMethod("Make")!;

        var samples = new[]
        {
            ("", 0L),
            ("a", 1L),
            ("a", 2L),
            ("ab", -5L),
            ("b", long.MinValue),
            ("b", long.MaxValue),
            ("zzz", 0L),
        };

        var encoded = samples
            .Select(s => (Record: make.Invoke(null, [s.Item1, s.Item2])!, Bytes: Serialize(make.Invoke(null, [s.Item1, s.Item2])!)))
            .ToList();

        var byteSort = encoded.OrderBy(e => e.Bytes, ByteLexComparer.Instance).ToList();
        var compareSort = encoded
            .OrderBy(e => e.Record, new ReflectionComparableComparer())
            .ToList();

        byteSort.Select(e => e.Record).Should().Equal(
            compareSort.Select(e => e.Record),
            "byte-lex sort of ordered-wire bytes must match CompareTo sort");
    }

    // Property-based: 10,000 random (string, long) tuples, sort by
    // byte-lex and by CompareTo, assert identical orderings.
    [Fact]
    public void Property_Test_10k_Random_Tuples_Byte_Lex_Matches_Compare_Sort()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                public partial record struct PK(
                    [property: Tag(1)] string S,
                    [property: Tag(2)] long L);

                public static class PKFactory
                {
                    public static PK Make(string s, long l) => new PK(s, l);
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var factory = asm.GetType("TestNamespace.PKFactory")!;
        var make = factory.GetMethod("Make")!;

        // Deterministic PRNG so failures are reproducible.
        var rng = new Random(0xC0FFEE);
        var samples = new List<(object Record, byte[] Bytes)>(10_000);
        for (var i = 0; i < 10_000; i++)
        {
            // Mix of string lengths and long magnitudes; include embedded nulls occasionally.
            var len = rng.Next(0, 8);
            var chars = new char[len];
            for (var j = 0; j < len; j++)
            {
                // ASCII printable plus occasional 0x00 (exercises escape).
                var roll = rng.Next(0, 100);
                chars[j] = roll < 5 ? '\0' : (char)rng.Next('a', 'z' + 1);
            }
            var s = new string(chars);
            var l = (long)((ulong)rng.NextInt64() ^ ((ulong)rng.NextInt64() << 32));
            var rec = make.Invoke(null, [s, l])!;
            var bytes = Serialize(rec);
            samples.Add((rec, bytes));
        }

        var byteSort = samples.OrderBy(e => e.Bytes, ByteLexComparer.Instance).Select(e => e.Record).ToList();
        var compareSort = samples.OrderBy(e => e.Record, new ReflectionComparableComparer()).Select(e => e.Record).ToList();

        byteSort.Should().Equal(compareSort,
            "byte-lex order of ordered-wire bytes must match CompareTo order on 10_000 random tuples");
    }

    // Property test per new ordered-wire
    // kind in composite-key position (string + T). 1000 random tuples per kind so byte-lex
    // sort matches CompareTo sort on the decoded records. DateTimeOffset additionally
    // asserts the UTC-instant-primary ordering invariant for same-instant pairs.
    [Theory]
    [InlineData("float")]
    [InlineData("double")]
    [InlineData("System.Half")]
    [InlineData("System.Int128")]
    [InlineData("System.UInt128")]
    [InlineData("System.DateTimeOffset")]
    [InlineData("System.TimeSpan")]
    [InlineData("System.DateOnly")]
    [InlineData("System.TimeOnly")]
    public void Composite_String_Plus_New_Kind_Byte_Lex_Matches_Compare(string clrType)
    {
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                public partial record struct PK(
                    [property: Tag(1)] string S,
                    [property: Tag(2)] {{clrType}} V);

                public static class PKFactory
                {
                    public static PK Make(string s, {{clrType}} v) => new PK(s, v);
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var factory = asm.GetType("TestNamespace.PKFactory")!;
        var make = factory.GetMethod("Make")!;

        var rng = new Random(0xDECAF);
        var samples = new List<(object Record, byte[] Bytes)>(1_000);
        for (var i = 0; i < 1_000; i++)
        {
            var s = RandomAsciiOrNull(rng, maxLen: 6);
            var v = GenerateRandomValueForKind(clrType, rng);
            var rec = make.Invoke(null, [s, v])!;
            var bytes = Serialize(rec);
            samples.Add((rec, bytes));
        }

        var byteSort = samples.OrderBy(e => e.Bytes, ByteLexComparer.Instance).Select(e => e.Record).ToList();
        var compareSort = samples.OrderBy(e => e.Record, new ReflectionComparableComparer()).Select(e => e.Record).ToList();

        byteSort.Should().Equal(compareSort,
            $"byte-lex of (string, {clrType}) composite key must match CompareTo on 1000 random tuples");
    }

    // decimal property test uses a constrained fixed-scale distribution — see
    // docs/source-generator-type-reference.md for the cross-scale caveat. At a consistent
    // scale the ordering invariant holds; this test pins the fixed-scale contract.
    [Fact]
    public void Composite_String_Plus_FixedScale_Decimal_Byte_Lex_Matches_Compare()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                public partial record struct PK(
                    [property: Tag(1)] string S,
                    [property: Tag(2)] [property: DecimalScale(4)] decimal V);

                public static class PKFactory
                {
                    public static PK Make(string s, decimal v) => new PK(s, v);
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var factory = asm.GetType("TestNamespace.PKFactory")!;
        var make = factory.GetMethod("Make")!;

        var rng = new Random(0xD3C1);
        var samples = new List<(object Record, byte[] Bytes)>(1_000);
        var produced = 0;
        while (produced < 1_000)
        {
            var s = RandomAsciiOrNull(rng, maxLen: 6);
            // Scale fixed at 4 (finance price convention). To keep every sample at the same
            // post-normalization scale, skip raw values divisible by 10 (which would normalize
            // to a smaller scale and break the byte-lex-by-mantissa invariant at equal scale).
            var rawUnits = rng.NextInt64(-10_000_000L, 10_000_000L);
            if (rawUnits == 0 || rawUnits % 10 == 0)
            {
                continue;
            }
            var isNeg = rawUnits < 0;
            var abs = (ulong)Math.Abs(rawUnits);
            var v = new decimal(
                (int)(abs & 0xFFFF_FFFFul),
                (int)((abs >> 32) & 0xFFFF_FFFFul),
                0,
                isNeg,
                scale: 4);
            var rec = make.Invoke(null, [s, v])!;
            var bytes = Serialize(rec);
            samples.Add((rec, bytes));
            produced++;
        }

        var byteSort = samples.OrderBy(e => e.Bytes, ByteLexComparer.Instance).Select(e => e.Record).ToList();
        var compareSort = samples.OrderBy(e => e.Record, new ReflectionComparableComparer()).Select(e => e.Record).ToList();

        byteSort.Should().Equal(compareSort,
            "byte-lex of (string, decimal@scale=4) must match CompareTo on 1000 random fixed-scale tuples");
    }

    // DateTimeOffset sub-clause: UTC-instant primary + offset
    // tiebreaker. Two DateTimeOffsets representing the same UTC instant at different offsets
    // must produce byte-identical first 8 bytes (UTC-instant prefix), and the full record
    // sort must be (utc_a, off_a) < (utc_b, off_b) compared lexicographically.
    [Fact]
    public void DateTimeOffset_Same_Utc_Instant_Shares_Leading_Eight_Bytes()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                public partial record struct DK([property: Tag(1)] System.DateTimeOffset V);

                public static class DKFactory
                {
                    public static DK Make(System.DateTimeOffset v) => new DK(v);
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var factory = asm.GetType("TestNamespace.DKFactory")!;
        var make = factory.GetMethod("Make")!;

        var utcAnchor = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);
        var offsets = new[] { TimeSpan.FromHours(-5), TimeSpan.Zero, TimeSpan.FromHours(5) };
        var encoded = offsets
            .Select(utcAnchor.ToOffset)
            .Select(dto => Serialize(make.Invoke(null, [dto])!))
            .ToArray();

        for (var i = 1; i < encoded.Length; i++)
        {
            encoded[i].Take(8).Should().Equal(encoded[0].Take(8),
                "same UTC instant must produce byte-identical first 8 bytes");
        }
    }

    // A [Key]'s equality must be exactly CompareTo() == 0, and equal keys must hash equal.
    // The LSM engine keys its tombstone maps by equality while ordering by CompareTo, so a
    // key pair that is Equals-true / CompareTo-nonzero (same instant, different offset) lets a
    // delete of one key shadow the other, distinct, live key.
    [Theory]
    [InlineData("record struct", "public partial record struct DK([property: Tag(1)] System.DateTimeOffset V);")]
    [InlineData("readonly record struct", "public readonly partial record struct DK([property: Tag(1)] System.DateTimeOffset V);")]
    [InlineData("sealed record class", "public sealed partial record class DK([property: Tag(1)] System.DateTimeOffset V);")]
    [InlineData("record class", "public partial record class DK([property: Tag(1)] System.DateTimeOffset V);")]
    [InlineData("plain struct", "public partial struct DK { [Tag(1)] public System.DateTimeOffset V { get; } public DK(System.DateTimeOffset v) { V = v; } }")]
    [InlineData("nullable", "public partial record struct DK([property: Tag(1)] System.DateTimeOffset? V);")]
    [InlineData("composite", "public partial record struct DK([property: Tag(1)] int Id, [property: Tag(2)] System.DateTimeOffset V);")]
    public void DateTimeOffset_Key_Equality_Agrees_With_CompareTo(string shape, string declaration)
    {
        var isComposite = shape == "composite";
        var isNullable = shape == "nullable";
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                {{declaration}}

                public static class DKFactory
                {
                    public static DK Make(System.DateTimeOffset v) => new DK({{(isComposite ? "7, " : "")}}v);
                    {{(isNullable ? "public static DK MakeNull() => new DK(null);" : "")}}
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var factory = asm.GetType("TestNamespace.DKFactory")!;
        var make = factory.GetMethod("Make")!;
        var instant = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var keys = new List<object>
        {
            make.Invoke(null, [instant])!,
            make.Invoke(null, [instant.ToOffset(TimeSpan.FromHours(1))])!,
            make.Invoke(null, [instant.ToOffset(TimeSpan.FromHours(-5))])!,
            make.Invoke(null, [instant.AddTicks(1)])!,
            make.Invoke(null, [instant])!,
        };
        if (isNullable)
        {
            keys.Add(factory.GetMethod("MakeNull")!.Invoke(null, null)!);
            keys.Add(factory.GetMethod("MakeNull")!.Invoke(null, null)!);
        }

        var compareTo = keys[0].GetType().GetMethod("CompareTo", [keys[0].GetType()])!;
        foreach (var a in keys)
        {
            foreach (var b in keys)
            {
                var cmp = (int)compareTo.Invoke(a, [b])!;
                a.Equals(b).Should().Be(cmp == 0,
                    $"[{shape}] Equals must be exactly CompareTo()==0 for {a} vs {b} (CompareTo={cmp})");
                if (cmp == 0)
                {
                    a.GetHashCode().Should().Be(b.GetHashCode(), $"[{shape}] equal keys must hash equal");
                }
            }
        }
    }

    private static string RandomAsciiOrNull(Random rng, int maxLen)
    {
        var len = rng.Next(0, maxLen + 1);
        var chars = new char[len];
        for (var j = 0; j < len; j++)
        {
            chars[j] = rng.Next(0, 100) < 5 ? '\0' : (char)rng.Next('a', 'z' + 1);
        }
        return new string(chars);
    }

    private static object GenerateRandomValueForKind(string clrType, Random rng) => clrType switch
    {
        "float" => (float)(rng.NextDouble() * 2e10 - 1e10),
        "double" => rng.NextDouble() * 2e200 - 1e200,
        "System.Half" => (Half)((float)rng.NextDouble() * 200f - 100f),
        "System.Int128" => GenerateInt128(rng),
        "System.UInt128" => GenerateUInt128(rng),
        "System.DateTimeOffset" => GenerateDateTimeOffset(rng),
        // Negative TimeSpans exercise the Int64 sign-flip path in composite position.
        "System.TimeSpan" => TimeSpan.FromTicks(rng.NextInt64(long.MinValue / 2, long.MaxValue / 2)),
        "System.DateOnly" => DateOnly.FromDayNumber(rng.Next(0, 3_652_059)),
        "System.TimeOnly" => new TimeOnly(rng.NextInt64(0, TimeSpan.TicksPerDay)),
        _ => throw new InvalidOperationException($"Unhandled kind: {clrType}"),
    };

    private static Int128 GenerateInt128(Random rng)
    {
        Span<byte> buf = stackalloc byte[16];
        rng.NextBytes(buf);
        var low = BitConverter.ToUInt64(buf.Slice(0, 8));
        var high = BitConverter.ToUInt64(buf.Slice(8, 8));
        return new Int128(high, low);
    }

    private static UInt128 GenerateUInt128(Random rng)
    {
        Span<byte> buf = stackalloc byte[16];
        rng.NextBytes(buf);
        var low = BitConverter.ToUInt64(buf.Slice(0, 8));
        var high = BitConverter.ToUInt64(buf.Slice(8, 8));
        return new UInt128(high, low);
    }

    private static DateTimeOffset GenerateDateTimeOffset(Random rng)
    {
        // Year in [2000, 2050] with random month/day/hour/minute; offset in ±14h in 15-min steps.
        var year = rng.Next(2000, 2051);
        var month = rng.Next(1, 13);
        var day = rng.Next(1, DateTime.DaysInMonth(year, month) + 1);
        var hour = rng.Next(0, 24);
        var minute = rng.Next(0, 60);
        var offsetStep = rng.Next(-56, 57); // −14h..+14h in 15-min steps
        return new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromMinutes(offsetStep * 15));
    }

    // A DatasetKey shape (strings + enum) compiles through ordered
    // wire, serializes, deserializes, and sorts correctly.
    [Fact]
    public void DatasetKey_Shape_With_Enum_Encodes_Decodes_And_Sorts()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                public enum BarGranularity { Daily, Hourly, Minute }

                [Key]
                public partial record struct PriceBarDatasetKey(
                    [property: Tag(1)] string Provider,
                    [property: Tag(2)] BarGranularity Frequency,
                    [property: Tag(3)] string Ticker);

                public static class PriceBarDatasetKeyFactory
                {
                    public static PriceBarDatasetKey Make(string p, int f, string t)
                        => new PriceBarDatasetKey(p, (BarGranularity)f, t);
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var factory = asm.GetType("TestNamespace.PriceBarDatasetKeyFactory")!;
        var make = factory.GetMethod("Make")!;

        var a = make.Invoke(null, ["Tiingo", 0 /* Daily */, "AAPL"])!;
        var b = make.Invoke(null, ["Tiingo", 2 /* Minute */, "AAPL"])!;
        var aBytes = Serialize(a);
        var bBytes = Serialize(b);

        // Round-trip.
        Deserialize(asm, "TestNamespace.PriceBarDatasetKey", aBytes).Should().Be(a);
        Deserialize(asm, "TestNamespace.PriceBarDatasetKey", bBytes).Should().Be(b);

        // Byte-lex order: Daily < Minute because 0 < 2 on the enum underlying.
        ByteLexComparer.Instance.Compare(aBytes, bBytes).Should().BeNegative();
    }

    // A nullable [Key] field encodes a null discriminator (0x00) ahead of the present-value
    // discriminator (0x01), so an absent key MUST sort byte-lex strictly before ANY present value —
    // including int.MinValue, whose sign-flipped big-endian body starts at 0x00. This pins the
    // ordering guarantee at the null/present boundary, which the random-tuple property test does not
    // target directly.
    [Fact]
    public void Nullable_Key_Null_Sorts_Strictly_Before_Any_Present_Value()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                public partial record struct NK([property: Tag(1)] int? V);

                public static class NKFactory
                {
                    public static NK Make(int? v) => new NK(v);
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var make = asm.GetType("TestNamespace.NKFactory")!.GetMethod("Make")!;

        var recNull = make.Invoke(null, [null])!;
        var recMin = make.Invoke(null, [int.MinValue])!;
        var recZero = make.Invoke(null, [0])!;

        var nullBytes = Serialize(recNull);
        var minBytes = Serialize(recMin);
        var zeroBytes = Serialize(recZero);

        ByteLexComparer.Instance.Compare(nullBytes, minBytes).Should().BeNegative(
            "the null discriminator 0x00 must sort before the present-value discriminator 0x01, even for int.MinValue");
        ByteLexComparer.Instance.Compare(nullBytes, zeroBytes).Should().BeNegative();

        // The generated CompareTo must AGREE with byte-lex on every pair (the bug was that it didn't
        // even compile for a nullable field). Same sign on each ordered pair.
        var generated = new ReflectionComparableComparer();
        foreach (var (x, y) in new[] { (recNull, recMin), (recNull, recZero), (recMin, recZero) })
        {
            Math.Sign(generated.Compare(x, y))
                .Should().Be(Math.Sign(ByteLexComparer.Instance.Compare(Serialize(x), Serialize(y))),
                    "generated CompareTo must match ordered-wire byte-lex for nullable keys");
        }

        Deserialize(asm, "TestNamespace.NK", nullBytes).Should().Be(recNull);
    }

    // A nullable Guid [Key] must compare its present value by the same big-endian byte-lex the writer
    // emits (CompareGuidByteLex), not Guid.CompareTo — and null still sorts first.
    [Fact]
    public void Nullable_Guid_Key_Compares_ByteLex_And_NullSortsFirst()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                public partial record struct NGK([property: Tag(1)] System.Guid? V);

                public static class NGKFactory
                {
                    public static NGK Make(System.Guid? v) => new NGK(v);
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var make = asm.GetType("TestNamespace.NGKFactory")!.GetMethod("Make")!;

        var rng = new Random(20260609);
        var records = new List<object> { make.Invoke(null, [null])! };
        for (var i = 0; i < 200; i++)
        {
            var buf = new byte[16];
            rng.NextBytes(buf);
            records.Add(make.Invoke(null, [new Guid(buf)])!);
        }

        var byByteLex = records.OrderBy(Serialize, ByteLexComparer.Instance).ToList();
        var byCompareTo = records.OrderBy(r => r, new ReflectionComparableComparer()).ToList();

        byCompareTo.Select(Serialize).Should().BeEquivalentTo(
            byByteLex.Select(Serialize), o => o.WithStrictOrdering(),
            "nullable-Guid CompareTo must match byte-lex ordering, with null first");
    }

    // A nullable enum [Key] field must generate code at all — the classifier dropped the inner
    // enum's underlying numeric kind when wrapping it in Nullable, so the emitter hit "Enum missing
    // EnumUnderlying" and produced ZERO source for the whole record (a warning, not an error, so the
    // type silently became non-serializable). It must encode null-first then compare the present
    // value by the enum's underlying byte-lex, agreeing with the ordered-wire bytes.
    [Fact]
    public void Nullable_Enum_Key_Generates_RoundTrips_And_NullSortsFirst()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                public enum Gran : byte { Daily, Hourly, Minute }

                [Key]
                public partial record struct NEK([property: Tag(1)] Gran? V);

                public static class NEKFactory
                {
                    public static NEK Make(int? v) => new NEK(v.HasValue ? (Gran?)(Gran)v.Value : null);
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var make = asm.GetType("TestNamespace.NEKFactory")!.GetMethod("Make")!;

        var recNull = make.Invoke(null, [null])!;
        var recDaily = make.Invoke(null, [0])!;
        var recMinute = make.Invoke(null, [2])!;

        var nullBytes = Serialize(recNull);
        var dailyBytes = Serialize(recDaily);
        var minuteBytes = Serialize(recMinute);

        ByteLexComparer.Instance.Compare(nullBytes, dailyBytes).Should().BeNegative(
            "the null discriminator 0x00 must sort before any present enum value");
        ByteLexComparer.Instance.Compare(dailyBytes, minuteBytes).Should().BeNegative(
            "Daily (0) sorts before Minute (2) on the enum's byte underlying");

        var generated = new ReflectionComparableComparer();
        foreach (var (x, y) in new[] { (recNull, recDaily), (recNull, recMinute), (recDaily, recMinute) })
        {
            Math.Sign(generated.Compare(x, y))
                .Should().Be(Math.Sign(ByteLexComparer.Instance.Compare(Serialize(x), Serialize(y))),
                    "generated CompareTo must match ordered-wire byte-lex for nullable-enum keys");
        }

        Deserialize(asm, "TestNamespace.NEK", nullBytes).Should().Be(recNull);
        Deserialize(asm, "TestNamespace.NEK", minuteBytes).Should().Be(recMinute);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Compile-and-load helpers (mirror TaggedWireTests, different default entry point).
    // ═════════════════════════════════════════════════════════════════════════════════════

    // UTF-8 byte order is code-POINT order; String.CompareOrdinal is code-UNIT order. The two
    // diverge exactly for U+E000..U+FFFF vs supplementary-plane code points (U+10000+): in
    // UTF-16, supplementary chars are surrogate pairs starting 0xD800..0xDBFF, which sort
    // BELOW 0xE000..0xFFFF code units — but their UTF-8 encodings (F0..F4 lead bytes) sort
    // ABOVE (EE..EF leads). The generated CompareTo must track the byte-lex of the encoded
    // form (the SSTable binary-search invariant), not UTF-16 code-unit order.
    [Fact]
    public void Key_String_NonBmp_CompareTo_Matches_ByteLex()
    {
        var source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                public partial record struct K([property: Tag(1)] string V);
            }
            """;

        var asm = CompileAndLoad(source);
        var t = asm.GetType("TestNamespace.K")!;
        var a = Activator.CreateInstance(t, "")!;        // U+E000 — BMP private-use, UTF-8 EE 80 80
        var b = Activator.CreateInstance(t, "𐀀")!;  // U+10000 — supplementary, UTF-8 F0 90 80 80

        var compareToSign = Math.Sign(new ReflectionComparableComparer().Compare(a, b));
        var byteLexSign = Math.Sign(ByteLexComparer.Instance.Compare(Serialize(a), Serialize(b)));

        compareToSign.Should().Be(byteLexSign,
            "generated CompareTo must agree with ordered-wire byte-lex for non-BMP key strings");
    }

    // Unpaired surrogates are not encodable in UTF-8. Encoding.UTF8's default replacement
    // fallback silently maps every lone surrogate to U+FFFD, which (a) collides distinct keys
    // ("\uD800" and "\uDFFF" produce identical bytes — one SSTable slot, silent overwrite) and
    // (b) makes Deserialize(Serialize(k)) != k. The writer must reject ill-formed UTF-16 the
    // same way it rejects NaN float/double keys: fail fast with ArgumentException.
    [Fact]
    public void Key_String_LoneSurrogate_Serialize_Throws()
    {
        var source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                public partial record struct K([property: Tag(1)] string V);
            }
            """;

        var asm = CompileAndLoad(source);
        var t = asm.GetType("TestNamespace.K")!;
        var a = Activator.CreateInstance(t, "\uD800")!; // lone high surrogate — ill-formed UTF-16

        var act = () => Serialize(a);

        act.Should().Throw<TargetInvocationException>(
                "a lone surrogate cannot round-trip through UTF-8 and must be rejected at encode time")
            .WithInnerException<ArgumentException>();
    }

    // The DateTimeOffset encoding is 8 BE UtcTicks + 2 BE sign-flipped offset-minutes
    // ("UTC-instant primary, offset tiebreaker"). DateTimeOffset.CompareTo compares the UTC
    // instant ONLY, so a same-instant/different-offset pair compares equal while its encodings
    // differ in the trailing 2 bytes — sign(0) != sign(±1) at the equality boundary: a memtable
    // dedups the two as one key while the SSTable stores two distinct byte keys. The generated
    // CompareTo must order instant-first-then-offset, matching the bytes.
    [Fact]
    public void Key_DateTimeOffset_SameInstant_DifferentOffset_CompareTo_Matches_ByteLex()
    {
        var source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                public partial record struct K([property: Tag(1)] System.DateTimeOffset V);
            }
            """;

        var asm = CompileAndLoad(source);
        var t = asm.GetType("TestNamespace.K")!;
        var instant = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);
        var a = Activator.CreateInstance(t, instant)!;
        var b = Activator.CreateInstance(t, instant.ToOffset(TimeSpan.FromHours(5)))!;

        var compareToSign = Math.Sign(new ReflectionComparableComparer().Compare(a, b));
        var byteLexSign = Math.Sign(ByteLexComparer.Instance.Compare(Serialize(a), Serialize(b)));

        compareToSign.Should().Be(byteLexSign,
            "generated CompareTo must agree with ordered-wire byte-lex for same-instant different-offset DateTimeOffset keys");
    }

    // Bucket-4 pin: an enum with NEGATIVE members on a [Key] must order consistently between
    // the generated CompareTo (cast to the signed underlying) and the ordered-wire bytes
    // (signed BE + sign-bit flip on the underlying). Negative members are where a naive
    // unsigned interpretation would invert the order.
    [Fact]
    public void Key_Enum_WithNegativeMembers_CompareTo_Matches_ByteLex()
    {
        var source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                public enum Balance : int { Deficit = -5, Zero = 0, Surplus = 7 }

                [Key]
                public partial record struct K([property: Tag(1)] Balance V);

                public static class KFactory
                {
                    public static K[] Make() => new[] { new K(Balance.Surplus), new K(Balance.Deficit), new K(Balance.Zero) };
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var records = ((Array)asm.GetType("TestNamespace.KFactory")!.GetMethod("Make")!.Invoke(null, null)!)
            .Cast<object>().ToList();

        var byCompareTo = records.OrderBy(r => r, new ReflectionComparableComparer()).Select(Serialize).ToList();
        var byByteLex = records.Select(Serialize).OrderBy(b => b, ByteLexComparer.Instance).ToList();

        byCompareTo.Should().BeEquivalentTo(byByteLex, o => o.WithStrictOrdering(),
            "generated CompareTo must match ordered-wire byte-lex for enums with negative members");
    }

    private static Assembly CompileAndLoad(string source)
    {
        var asmName = "OrderedAsm_" + System.Guid.NewGuid().ToString("N");
        var compilation = TestHelpers.CreateCompilation(source, assemblyName: asmName);
        var generator = new StorageSourceGenerator();
        var driver = CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        errors.Should().BeEmpty(because: "generated code must compile; got:\n" +
            string.Join("\n", errors.Select(d => d.ToString())));

        using var ms = new MemoryStream();
        var result = outputCompilation.Emit(ms);
        result.Success.Should().BeTrue();
        ms.Position = 0;
        return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    private static byte[] Serialize(object record)
    {
        using var ms = new MemoryStream();
        record.GetType().GetMethod("Serialize", [typeof(Stream)])!.Invoke(record, [ms]);
        return ms.ToArray();
    }

    private static object Deserialize(Assembly asm, string typeFqn, byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        var t = asm.GetType(typeFqn)!;
        return t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [ms])!;
    }

    private sealed class ByteLexComparer : IComparer<byte[]>
    {
        public static readonly ByteLexComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var min = Math.Min(x.Length, y.Length);
            for (var i = 0; i < min; i++)
            {
                var d = x[i].CompareTo(y[i]);
                if (d != 0)
                {
                    return d;
                }
            }
            return x.Length.CompareTo(y.Length);
        }
    }

    // Uses the generated IComparable<T>.CompareTo via reflection — works for any [Key] type.
    private sealed class ReflectionComparableComparer : IComparer<object>
    {
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var method = x.GetType().GetMethod("CompareTo", [x.GetType()])
                ?? throw new InvalidOperationException($"Type {x.GetType().FullName} has no CompareTo.");
            return (int)method.Invoke(x, [y])!;
        }
    }
}
