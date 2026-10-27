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
using System.Buffers.Binary;
using System.Reflection;

namespace Boutquin.Storage.SourceGenerator.Tests;

// F-W1-03 — the tagged-wire-emitted Deserialize must surface a TYPED corruption exception
// (InvalidDataException — the codec/emitter convention, see CorruptionGuards) when a forged wire
// payload carries an invalid decimal flags word or an out-of-range DateTimeOffset, NOT a raw BCL
// ArgumentException / ArgumentOutOfRangeException leaking out of the public Deserialize API. The
// round-5 fix covered only the DateOnly/TimeOnly arms; the DateTimeOffset and Decimal arms still
// emitted bare BCL construction.
public sealed class ForgedDecimalDateTimeOffsetDecodeTests
{
    // ---- Decimal ([StorageSerializable]) --------------------------------------------------

    [Fact]
    public void Tagged_Decimal_ForgedInvalidFlags_Throws_Typed_Not_Raw_Argument()
    {
        // [StorageSerializable] decimal is a fixed 16-byte block: lo/mid/hi/flags little-endian.
        // The flags word lives in the last 4 bytes of that block. Forge scale = 31 (0x001F0000).
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] decimal V);
                public static class RFactory
                {
                    public static R Make() => new R(123.45m);
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNamespace.RFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(original);

        // The decimal block is the only 16-byte payload region; its flags word is the trailing
        // little-endian int32. Forge a scale-31 flags word in place of the valid one.
        var validBits = decimal.GetBits(123.45m);
        var validFlags = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(validFlags, validBits[3]);
        var idx = LastIndexOf(bytes, validFlags);
        idx.Should().BeGreaterThanOrEqualTo(0, "the decimal flags word must appear in the serialized payload");
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(idx, 4), 0x001F0000);

        AssertTypedCorruption(
            () => Deserialize(asm, "TestNamespace.R", bytes),
            "a forged decimal flags word with scale > 28 must surface a typed corruption exception, "
            + "not a raw ArgumentException");
    }

    // ---- DateTimeOffset ([StorageSerializable]) -------------------------------------------

    [Fact]
    public void Tagged_DateTimeOffset_ForgedOutOfRangeOffset_Throws_Typed_Not_Raw_AOORE()
    {
        // [StorageSerializable] DateTimeOffset is a nested LEN block: varint(local DateTime ticks)
        // followed by zigzag(offset minutes). Forge the offset zigzag from a valid value (-300 min,
        // zigzag 599 → 2 bytes) to 1000 min (zigzag 2000 → 2 bytes) — same varint width so the
        // nested LEN block stays byte-consistent (no LEN repatch). 1000 min > the ±14h (840) cap.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateTimeOffset V);
                public static class RFactory
                {
                    public static R Make() => new R(new System.DateTimeOffset(2026, 4, 21, 12, 34, 56, System.TimeSpan.FromHours(-5)));
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNamespace.RFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(original);

        // Valid offset is -5h = -300 minutes; its zigzag encoding is the needle. Splice in 1000 min
        // (same 2-byte zigzag width) so the nested LEN block stays consistent.
        var validZig = EncodeVarint(ZigZag(-300));
        var forgedZig = EncodeVarint(ZigZag(1000));
        validZig.Length.Should().Be(forgedZig.Length, "both offsets must encode to the same varint width");
        var idx = LastIndexOf(bytes, validZig);
        idx.Should().BeGreaterThanOrEqualTo(0, "the offset zigzag varint must appear in the serialized payload");

        var forged = (byte[])bytes.Clone();
        System.Array.Copy(forgedZig, 0, forged, idx, forgedZig.Length);

        AssertTypedCorruption(
            () => Deserialize(asm, "TestNamespace.R", forged),
            "a forged DateTimeOffset offset beyond ±14h must surface a typed corruption exception, "
            + "not a raw ArgumentOutOfRangeException");
    }

    [Fact]
    public void Tagged_DateTimeOffset_ForgedOutOfRangeTicks_Throws_Typed_Not_Raw_AOORE()
    {
        // Forge the local-ticks varint to long.MaxValue (>> DateTime.MaxValue.Ticks) so the bare
        // new DateTime(__ticks, ...) would throw AOORE. The valid ticks varint is the needle.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateTimeOffset V);
                public static class RFactory
                {
                    public static R Make() => new R(new System.DateTimeOffset(2026, 4, 21, 12, 34, 56, System.TimeSpan.Zero));
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNamespace.RFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(original);

        var validTicks = (ulong)new DateTimeOffset(2026, 4, 21, 12, 34, 56, TimeSpan.Zero).DateTime.Ticks;
        var validTicksEnc = EncodeVarint(validTicks);
        var forgedTicksEnc = EncodeVarint(unchecked((ulong)long.MaxValue));
        var idx = IndexOf(bytes, validTicksEnc);
        idx.Should().BeGreaterThanOrEqualTo(0, "the local-ticks varint must appear in the serialized payload");

        var forged = new byte[bytes.Length - validTicksEnc.Length + forgedTicksEnc.Length];
        System.Array.Copy(bytes, 0, forged, 0, idx);
        System.Array.Copy(forgedTicksEnc, 0, forged, idx, forgedTicksEnc.Length);
        System.Array.Copy(bytes, idx + validTicksEnc.Length, forged, idx + forgedTicksEnc.Length,
            bytes.Length - idx - validTicksEnc.Length);

        AssertTypedCorruption(
            () => Deserialize(asm, "TestNamespace.R", RepatchLengthPrefix(forged)),
            "a forged out-of-range local-ticks value must surface a typed corruption exception, "
            + "not a raw ArgumentOutOfRangeException");
    }

    // ---- Round-trip regression ------------------------------------------------------------

    [Fact]
    public void Valid_Decimal_And_DateTimeOffset_Still_Round_Trip()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [StorageSerializable]
                public partial record struct R(
                    [property: Tag(1)] decimal M,
                    [property: Tag(2)] System.DateTimeOffset D);
                public static class RFactory
                {
                    public static R Make() => new R(123.45m, new System.DateTimeOffset(2026, 4, 21, 12, 34, 56, System.TimeSpan.FromHours(-5)));
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNamespace.RFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(original);
        var round = Deserialize(asm, "TestNamespace.R", bytes);
        round.Should().Be(original, "valid decimal + DateTimeOffset must still round-trip after the decode guards");
    }

    // --- helpers -------------------------------------------------------------------------------

    private static void AssertTypedCorruption(Func<object> act, string because)
    {
        var outer = act.Should().Throw<TargetInvocationException>(because).Which;
        outer.InnerException.Should().BeOfType<InvalidDataException>(because);
    }

    private static ulong ZigZag(long value) => (ulong)((value << 1) ^ (value >> 63));

    private static byte[] EncodeVarint(ulong value)
    {
        var buf = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }

            buf.Add(b);
        }
        while (value != 0);

        return buf.ToArray();
    }

    private static ulong DecodeVarint(byte[] bytes, ref int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = bytes[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return result;
    }

    private static byte[] RepatchLengthPrefix(byte[] payload)
    {
        var pos = 0;
        var oldLen = DecodeVarint(payload, ref pos);
        var bodyLen = (ulong)(payload.Length - pos);
        if (oldLen == bodyLen)
        {
            return payload;
        }

        var newLenEnc = EncodeVarint(bodyLen);
        var result = new byte[newLenEnc.Length + (payload.Length - pos)];
        System.Array.Copy(newLenEnc, 0, result, 0, newLenEnc.Length);
        System.Array.Copy(payload, pos, result, newLenEnc.Length, payload.Length - pos);
        return result;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static Assembly CompileAndLoad(string source)
    {
        var asmName = "ForgedDecDto_" + System.Guid.NewGuid().ToString("N");
        var compilation = TestHelpers.CreateCompilation(source, assemblyName: asmName);
        var generator = new StorageSourceGenerator();
        _ = CSharpGeneratorDriver
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
}
