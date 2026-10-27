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

// S1 (round 5) — forged temporal decode. The source-generator-emitted Deserialize bodies must
// surface a TYPED corruption exception (InvalidDataException — the codec/emitter convention,
// see CorruptionGuards) when a forged wire payload carries an out-of-range DateOnly day-number
// or TimeOnly tick count, NOT a raw BCL ArgumentOutOfRangeException leaking out of the public
// Deserialize API. This is the same defect class as F-V1-01 / F-U2-01, on the broadest surface
// (every [Key]/[StorageSerializable] record's generated deserializer).
public sealed class ForgedTemporalDecodeTests
{
    // ---- Ordered wire ([Key]) -------------------------------------------------------------

    [Fact]
    public void Ordered_DateOnly_Forged_OutOfRange_DayNumber_Throws_Typed_Not_Raw_AOORE()
    {
        // [Key] DateOnly is encoded as a 4-byte big-endian UInt32 day number (single property →
        // the whole ordered-wire payload is exactly those 4 bytes). Forge 0x7FFFFFFF, far above
        // the valid DateOnly day-number ceiling (3 652 058).
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                public partial record struct K([property: Tag(1)] System.DateOnly V);
                public static class KFactory
                {
                    public static K Make() => new K(new System.DateOnly(2026, 4, 21));
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNamespace.KFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(original);
        bytes.Length.Should().Be(4, "single-property ordered-wire DateOnly key is a 4-byte BE day number");

        // Overwrite the 4-byte BE day number with 0x7FFFFFFF (out of DateOnly range).
        bytes[0] = 0x7F; bytes[1] = 0xFF; bytes[2] = 0xFF; bytes[3] = 0xFF;

        AssertTypedCorruption(
            () => Deserialize(asm, "TestNamespace.K", bytes),
            "a forged out-of-range day number must surface a typed corruption exception, "
            + "not a raw ArgumentOutOfRangeException");
    }

    [Fact]
    public void Ordered_TimeOnly_Forged_OutOfRange_Ticks_Throws_Typed_Not_Raw_AOORE()
    {
        // [Key] TimeOnly is encoded as an 8-byte big-endian UInt64 tick count. Forge a tick value
        // above TimeOnly.MaxValue.Ticks (863_999_999_999) — but below long.MaxValue so the
        // (long) cast does not itself overflow; the raw bug is the bare `new TimeOnly(ticks)`.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [Key]
                public partial record struct K([property: Tag(1)] System.TimeOnly V);
                public static class KFactory
                {
                    public static K Make() => new K(new System.TimeOnly(12, 34, 56));
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNamespace.KFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(original);
        bytes.Length.Should().Be(8, "single-property ordered-wire TimeOnly key is an 8-byte BE tick count");

        // 0x0000_0100_0000_0000 == 1_099_511_627_776 ticks — above the TimeOnly ceiling, well
        // below long.MaxValue.
        System.Array.Clear(bytes, 0, bytes.Length);
        bytes[2] = 0x01;

        AssertTypedCorruption(
            () => Deserialize(asm, "TestNamespace.K", bytes),
            "a forged out-of-range tick count must surface a typed corruption exception, "
            + "not a raw ArgumentOutOfRangeException");
    }

    // ---- Tagged wire ([StorageSerializable]) ----------------------------------------------

    [Fact]
    public void Tagged_DateOnly_Forged_InInt32_OutOfRange_DayNumber_Throws_Typed_Not_Raw_AOORE()
    {
        // [StorageSerializable] DateOnly is varint-encoded. Forge a day number that is in-Int32
        // (so checked((int)) succeeds) but out of DateOnly range — this is precisely the arm the
        // emitter's `checked()` does NOT cover, per the S1 finding.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.DateOnly V);
                public static class RFactory
                {
                    public static R Make() => new R(new System.DateOnly(2026, 4, 21));
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNamespace.RFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var validBytes = Serialize(original);

        // Re-encode: replace the day-number varint with the forged in-Int32, out-of-range value.
        var validDayNumber = (ulong)new DateOnly(2026, 4, 21).DayNumber;
        var forgedBytes = SpliceVarint(validBytes, validDayNumber, forged: 2_000_000_000UL);

        AssertTypedCorruption(
            () => Deserialize(asm, "TestNamespace.R", forgedBytes),
            "an in-Int32 but out-of-DateOnly-range forged day number must surface a typed "
            + "corruption exception, not a raw ArgumentOutOfRangeException");
    }

    [Fact]
    public void Tagged_TimeOnly_Forged_OutOfRange_Ticks_Throws_Typed_Not_Raw_AOORE()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [StorageSerializable]
                public partial record struct R([property: Tag(1)] System.TimeOnly V);
                public static class RFactory
                {
                    public static R Make() => new R(new System.TimeOnly(12, 34, 56));
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNamespace.RFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var validBytes = Serialize(original);

        var validTicks = (ulong)new TimeOnly(12, 34, 56).Ticks;
        // 1_099_511_627_776 ticks — above the TimeOnly ceiling, below long.MaxValue.
        var forgedBytes = SpliceVarint(validBytes, validTicks, forged: 1_099_511_627_776UL);

        AssertTypedCorruption(
            () => Deserialize(asm, "TestNamespace.R", forgedBytes),
            "a forged out-of-range tick count must surface a typed corruption exception, "
            + "not a raw ArgumentOutOfRangeException");
    }

    // Valid temporal values must still round-trip unchanged — the wire bytes are not altered by
    // the guard, only the deserialize range-check.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Valid_Temporal_Still_Round_Trips(bool tagged)
    {
        var attr = tagged ? "StorageSerializable" : "Key";
        var source = $$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [{{attr}}]
                public partial record struct R(
                    [property: Tag(1)] System.DateOnly D,
                    [property: Tag(2)] System.TimeOnly T);
                public static class RFactory
                {
                    public static R Make() => new R(new System.DateOnly(2026, 4, 21), new System.TimeOnly(12, 34, 56));
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNamespace.RFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var bytes = Serialize(original);
        var round = Deserialize(asm, "TestNamespace.R", bytes);
        round.Should().Be(original, "valid temporal values must still round-trip after the decode guard");
    }

    // --- helpers -------------------------------------------------------------------------------

    // Deserialize is invoked by reflection, so the real exception arrives wrapped in
    // TargetInvocationException. Assert the unwrapped exception is the TYPED corruption exception
    // (InvalidDataException), not a raw ArgumentOutOfRangeException / OverflowException.
    private static void AssertTypedCorruption(Func<object> act, string because)
    {
        var outer = act.Should().Throw<TargetInvocationException>(because).Which;
        outer.InnerException.Should().BeOfType<InvalidDataException>(because);
    }

    // Locate the ULEB128 encoding of `original` inside `bytes` and replace it with the ULEB128 of
    // `forged`, returning the spliced payload. Keeps every surrounding byte (tag, length prefix,
    // schema version) intact.
    private static byte[] SpliceVarint(byte[] bytes, ulong original, ulong forged)
    {
        var originalEnc = EncodeVarint(original);
        var idx = IndexOf(bytes, originalEnc);
        idx.Should().BeGreaterThanOrEqualTo(0, "the original varint must appear in the serialized payload");

        var forgedEnc = EncodeVarint(forged);
        var result = new byte[bytes.Length - originalEnc.Length + forgedEnc.Length];
        System.Array.Copy(bytes, 0, result, 0, idx);
        System.Array.Copy(forgedEnc, 0, result, idx, forgedEnc.Length);
        System.Array.Copy(bytes, idx + originalEnc.Length, result, idx + forgedEnc.Length,
            bytes.Length - idx - originalEnc.Length);

        // The tagged-wire payload is length-prefixed (a varint LEN at the front). Re-patch it so
        // the forged (longer) body remains internally consistent. The LEN is the first varint; it
        // counts the bytes after itself.
        return RepatchLengthPrefix(result);
    }

    // Recompute the leading length-prefix varint to cover the (possibly resized) body. Tagged-wire
    // records frame the whole payload as: LEN(varint) || body. Ordered-wire single keys have no
    // such prefix; this is a no-op for them because SpliceVarint is only used on tagged records.
    private static byte[] RepatchLengthPrefix(byte[] payload)
    {
        // Read existing LEN.
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

    private static Assembly CompileAndLoad(string source)
    {
        var asmName = "ForgedTemporal_" + System.Guid.NewGuid().ToString("N");
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
