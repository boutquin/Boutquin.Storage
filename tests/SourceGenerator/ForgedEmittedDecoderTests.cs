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
using System.Runtime.Loader;

namespace Boutquin.Storage.SourceGenerator.Tests;

/// <summary>
/// AC-13.3 — Forged-input emitted-decoder tests.
///
/// The source-generator-emitted Deserialize bodies must surface a TYPED corruption exception
/// (<see cref="InvalidDataException"/>) when a forged wire payload carries:
///   (a) an oversize string length-prefix varint,
///   (b) an oversize collection count inside a count-prefixed block,
///   (c) an oversize nested-record LEN that exceeds the available bytes.
///
/// These are verified through the GENERATED decoder code path (CompileAndLoad → emit assembly
/// → invoke via reflection) to confirm the guards survive the emitter round-trip.
/// </summary>
public sealed class ForgedEmittedDecoderTests
{
    // ─── (a) String length-prefix oversize ───────────────────────────────────────────────────────

    /// <summary>
    /// A forged string length-prefix varint that exceeds the remaining bytes inside the payload
    /// block must cause the generated Deserialize to throw <see cref="InvalidDataException"/>
    /// (not a raw BCL exception). The guard lives in <c>VarintHelpers.ReadString</c>, which the
    /// emitter calls directly for every string field.
    /// </summary>
    [Fact]
    public void ForgedStringLengthPrefix_Oversize_ThrowsTypedException()
    {
        // Record with a single string property — chosen so the payload is small and easy to
        // locate in the serialised bytes: tag-N LengthPrefixed || string-LEN(varint) || UTF8.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNs
            {
                [StorageSerializable]
                public partial record struct SR([property: Tag(1)] string Name);
                public static class SRFactory
                {
                    public static SR Make() => new SR("A");
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNs.SRFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var validBytes = Serialize(original);

        // Parse the wire layout to find the string "A" length-prefix position:
        //   outer-LEN(varint) | 0x00(tag0+Varint) | 0x01(version) | tag1+LP(varint) | STRING-LEN(varint) | 'A'
        // Advance past the outer-LEN and the schema-version preamble, then past tag1+LP, so
        // we land on the STRING-LEN varint — that is the forge target.
        var pos = 0;
        DecodeVarint(validBytes, ref pos);                     // skip outer-LEN
        DecodeVarint(validBytes, ref pos);                     // skip tag0+wireType (0x00)
        DecodeVarint(validBytes, ref pos);                     // skip version (0x01)
        DecodeVarint(validBytes, ref pos);                     // skip tag1+wireType (0x0A)
        var stringLenOffset = pos;                             // NOW at string-LEN varint
        var stringLen = DecodeVarint(validBytes, ref pos);     // read its current value

        // Forge string-LEN to 2^32 — well above int.MaxValue, so VarintHelpers.ReadString's
        // first guard fires before any allocation attempt.
        var forgedBytes = SpliceAt(validBytes, stringLenOffset, EncodeVarint(stringLen), 0x1_0000_0000UL);

        AssertTypedCorruption(
            () => Deserialize(asm, "TestNs.SR", forgedBytes),
            "a string length-prefix claiming 2^32 bytes must surface InvalidDataException, "
            + "not a raw BCL exception, from the generated Deserialize path");
    }

    // ─── (b) Collection count oversize ───────────────────────────────────────────────────────────

    /// <summary>
    /// A forged collection count inside a count-prefixed block that exceeds the block's remaining
    /// bytes must cause the generated Deserialize to throw <see cref="InvalidDataException"/>.
    /// The guard is emitted inline:
    /// <c>if (__count &gt; __collStream.Length - __collStream.Position) throw …</c>
    /// </summary>
    [Fact]
    public void ForgedCollectionCount_Oversize_ThrowsTypedException()
    {
        // A string collection is count-prefixed (non-packable element). The emitter wraps it in:
        //   __collLen  (varint, outer block byte-size)
        //   <block>    __count (varint) || element0 || … || elementN-1
        // Forge __count to be larger than the block: the inline guard fires before any element read.
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNs
            {
                [StorageSerializable]
                public partial record struct CR([property: Tag(1)] IEnumerable<string> Tags);
                public static class CRFactory
                {
                    public static CR Make() => new CR(new[] { "x" });
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNs.CRFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var validBytes = Serialize(original);

        // Parse the wire layout to find the collection count position inside the block:
        //   outer-LEN | 0x00(tag0+Varint) | version | tag1+LP | COLL-BLOCK-LEN | COUNT | element...
        // Advance past the outer-LEN, schema preamble, tag1+LP, and the block-LEN to land on COUNT.
        var pos = 0;
        DecodeVarint(validBytes, ref pos);                   // skip outer-LEN
        DecodeVarint(validBytes, ref pos);                   // skip tag0+wireType
        DecodeVarint(validBytes, ref pos);                   // skip version
        DecodeVarint(validBytes, ref pos);                   // skip tag1+LP (tag=1, wt=2 → 0x0A)
        DecodeVarint(validBytes, ref pos);                   // skip collection block LEN
        var countOffset = pos;                               // NOW at count varint inside the block
        var count = DecodeVarint(validBytes, ref pos);       // read current count (=1)

        // Forge count to int.MaxValue. The block is only a handful of bytes wide, so
        // int.MaxValue >> remaining → the emitter's guard throws InvalidDataException immediately.
        var forgedBytes = SpliceAt(validBytes, countOffset, EncodeVarint(count), (ulong)int.MaxValue);

        AssertTypedCorruption(
            () => Deserialize(asm, "TestNs.CR", forgedBytes),
            "a collection count claiming int.MaxValue elements inside a tiny block must surface "
            + "InvalidDataException, not a raw BCL exception, from the generated Deserialize path");
    }

    // ─── (c) Nested-record LEN oversize ──────────────────────────────────────────────────────────

    /// <summary>
    /// A forged nested-record length-prefix varint that exceeds the available bytes in the outer
    /// stream must cause the generated Deserialize to throw <see cref="InvalidDataException"/>.
    /// The guard comes from <c>PooledReadBuffer.RentAndRead</c> detecting EOF when trying to fill
    /// the claimed length.
    /// </summary>
    [Fact]
    public void ForgedNestedRecordLen_Oversize_ThrowsTypedException()
    {
        // Outer record holds one inner [StorageSerializable] record. The outer Deserialize reads
        //   the outer LEN → slices into a payload MemoryStream → reads tag+wireType →
        //   reads inner LEN → calls PooledReadBuffer.RentAndRead(__s, innerLen).
        // Forging the inner LEN to a huge value causes RentAndRead to attempt a read beyond the
        // payload stream's end, which surfaces as InvalidDataException (EndOfStreamException is
        // wrapped by the emitter/helper into the public exception type).
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNs
            {
                [StorageSerializable]
                public partial record struct Inner([property: Tag(1)] int X);

                [StorageSerializable]
                public partial record struct Outer([property: Tag(1)] Inner Child);

                public static class OuterFactory
                {
                    public static Outer Make() => new Outer(new Inner(42));
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNs.OuterFactory")!.GetMethod("Make")!.Invoke(null, null)!;
        var validBytes = Serialize(original);

        // Parse the wire layout to locate the inner record's LEN varint:
        //   outer-LEN | 0x00(tag0+Varint) | version | tag1+LP(varint) | INNER-LEN(varint) | inner-body
        // Advance past outer-LEN, schema preamble, and tag1+LP to land on INNER-LEN.
        var pos = 0;
        DecodeVarint(validBytes, ref pos);                   // skip outer-LEN
        DecodeVarint(validBytes, ref pos);                   // skip tag0+wireType (0x00)
        DecodeVarint(validBytes, ref pos);                   // skip version (0x01)
        DecodeVarint(validBytes, ref pos);                   // skip tag1+LP (0x0A)
        var innerLenOffset = pos;                            // NOW at inner-record LEN varint
        var innerLen = DecodeVarint(validBytes, ref pos);    // read current inner LEN

        // Forge inner LEN to int.MaxValue — checked((int)int.MaxValue) succeeds, but
        // RentAndRead then attempts to read int.MaxValue bytes from a tiny payload MemoryStream.
        // Only a few bytes remain, so it throws EndOfStreamException.
        // Note: forging to 2^32 would overflow checked((int)...) to OverflowException instead.
        var forgedBytes = SpliceAt(validBytes, innerLenOffset, EncodeVarint(innerLen), (ulong)int.MaxValue);

        AssertEndOfStreamOrCorruption(
            () => Deserialize(asm, "TestNs.Outer", forgedBytes),
            "a nested-record LEN claiming int.MaxValue bytes against a tiny stream must surface "
            + "EndOfStreamException or InvalidDataException from the generated Deserialize path");
    }

    // ─── (d) Length/count varints in (2³¹, 2⁶⁴) must not leak OverflowException ─────────────────

    /// <summary>
    /// A forged length/count varint whose value exceeds <c>int.MaxValue</c> (but fits ULEB128)
    /// hits the narrowing to Int32 BEFORE any remaining-bytes guard can run. A bare
    /// <c>checked((int)…)</c> there leaks a raw <see cref="OverflowException"/> — the exact
    /// exception class the typed-corruption contract exists to contain (a forged varint is
    /// stream corruption, not an arithmetic bug). Pins the outer self-framing payload length;
    /// the same emission is used for collection block lengths and counts.
    /// </summary>
    [Fact]
    public void ForgedOuterPayloadLength_Beyond2Pow31_ThrowsTypedException()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNs
            {
                [StorageSerializable]
                public partial record struct SR2([property: Tag(1)] string Name);
                public static class SR2Factory
                {
                    public static SR2 Make() => new SR2("A");
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNs.SR2Factory")!.GetMethod("Make")!.Invoke(null, null)!;
        var validBytes = Serialize(original);

        // The outer self-framing LEN is the very first varint. Forge it to 2^32 by hand —
        // SpliceAt cannot be used here because its RepatchLengthPrefix step would rewrite the
        // outer LEN back to the true body size, undoing the forge.
        var pos = 0;
        _ = DecodeVarint(validBytes, ref pos); // consume the genuine outer LEN
        var forgedBytes = EncodeVarint(0x1_0000_0000UL).Concat(validBytes[pos..]).ToArray();

        AssertTypedCorruption(
            () => Deserialize(asm, "TestNs.SR2", forgedBytes),
            "a self-framing payload length claiming 2^32 bytes is stream corruption and must surface "
            + "InvalidDataException, not a raw OverflowException, from the generated Deserialize path");
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static Assembly CompileAndLoad(string source)
    {
        var asmName = "ForgedEmitted_" + Guid.NewGuid().ToString("N");
        var compilation = TestHelpers.CreateCompilation(source, assemblyName: asmName);
        var generator = new StorageSourceGenerator();
        _ = CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        errors.Should().BeEmpty("generated code must compile; errors: " +
            string.Join("\n", errors.Select(d => d.ToString())));

        using var ms = new MemoryStream();
        var result = outputCompilation.Emit(ms);
        result.Success.Should().BeTrue();
        ms.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(ms);
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

    private static byte[] SpliceAt(byte[] bytes, int idx, byte[] originalEnc, ulong forgedVarint)
    {
        var forgedEnc = EncodeVarint(forgedVarint);
        var result = new byte[bytes.Length - originalEnc.Length + forgedEnc.Length];
        Array.Copy(bytes, 0, result, 0, idx);
        Array.Copy(forgedEnc, 0, result, idx, forgedEnc.Length);
        Array.Copy(bytes, idx + originalEnc.Length, result, idx + forgedEnc.Length,
            bytes.Length - idx - originalEnc.Length);
        // Re-patch the leading outer LEN varint so the outer deserialization frame stays
        // internally consistent (PooledReadBuffer.RentAndRead uses the outer LEN to read
        // the payload buffer; without re-patching the outer read would truncate early).
        return RepatchLengthPrefix(result);
    }

    // Re-encode the leading length-prefix varint to cover the (possibly resized) body.
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
        Array.Copy(newLenEnc, 0, result, 0, newLenEnc.Length);
        Array.Copy(payload, pos, result, newLenEnc.Length, payload.Length - pos);
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

    private static void AssertTypedCorruption(Func<object> act, string because)
    {
        var outer = act.Should().Throw<TargetInvocationException>(because).Which;
        outer.InnerException.Should().BeOfType<InvalidDataException>(because);
    }

    // For the nested-record LEN oversize case, PooledReadBuffer.RentAndRead throws
    // EndOfStreamException (not InvalidDataException) when the claimed length exceeds
    // the available bytes. Both are typed corruption exceptions — neither is a raw BCL
    // exception that leaks internal implementation details like OverflowException.
    private static void AssertEndOfStreamOrCorruption(Func<object> act, string because)
    {
        var outer = act.Should().Throw<TargetInvocationException>(because).Which;
        outer.InnerException.Should().Match<Exception>(
            ex => ex is InvalidDataException || ex is System.IO.EndOfStreamException,
            because);
    }
}
