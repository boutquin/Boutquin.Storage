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

// Phase 2 tagged-wire format tests. Each test compiles a generator-produced record into a
// throwaway assembly, exercises Serialize/Deserialize end-to-end, and asserts the wire
// matches the DDIA Ch. 4 guarantees: tagged, varint-encoded, forward- and backward-compatible.
//
// These tests rely on runtime assembly emission (Roslyn CSharpCompilation.Emit) rather than
// inspecting generator output text — the wire format is an observable contract on bytes, not
// on emitted source. This is the only kind of generator test that can lock wire shape.
public sealed class TaggedWireTests
{
    // Varint compacts small longs. A single-field record with
    // Volume = 100_000 should carry a 3-byte varint payload (1-byte tag + 3-byte varint = 4
    // bytes of field) plus the length prefix + schema-format version byte.
    [Fact]
    public void Small_Long_Volume_Serializes_With_Varint_Compaction()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct SingleValue([property: Tag(1)] long Volume);
            """;

        var asm = CompileAndLoad(source);
        var value = InstantiateRecord(asm, "TestNamespace.SingleValue", 100_000L);
        var bytes = Serialize(value);

        // Payload shape: [tag0=Varint, schema-version=1 (1+1=2 bytes)] + [tag1=Varint, zigzag(100000)=3 bytes]
        // = 2 + 1 + 3 = 6 bytes of payload. Length prefix for 6 bytes = 1 byte. Total = 7 bytes.
        //
        // A 1.x positional writer would produce 8 bytes (one fixed int64). DDIA's predicted
        // byte savings materialize for typical daily-volume magnitudes.
        bytes.Length.Should().BeLessThanOrEqualTo(8,
            "tagged-wire with varint should match or beat the 8-byte fixed long for typical volumes");
    }

    // Round-trip integrity for every supported primitive kind. If this
    // test passes, the emitter's write and read paths are symmetric for that kind.
    [Theory]
    [InlineData("int", "42")]
    [InlineData("long", "100000")]
    [InlineData("string", "\"hello\"")]
    [InlineData("bool", "true")]
    [InlineData("double", "3.14159")]
    [InlineData("float", "2.5f")]
    [InlineData("byte", "255")]
    [InlineData("char", "'x'")]
    [InlineData("sbyte", "-42")]
    [InlineData("short", "-12345")]
    [InlineData("ushort", "65535")]
    [InlineData("uint", "4000000000U")]
    [InlineData("ulong", "18446744073709551615UL")]
    [InlineData("System.DateOnly", "new System.DateOnly(2026, 4, 21)")]
    [InlineData("System.TimeOnly", "new System.TimeOnly(13, 45, 30, 123)")]
    [InlineData("System.TimeSpan", "System.TimeSpan.FromMinutes(-30)")]
    [InlineData("System.Guid", "new System.Guid(\"12345678-1234-5678-1234-567812345678\")")]
    [InlineData("decimal", "123.456m")]
    [InlineData("System.Half", "(System.Half)1.5")]
    [InlineData("System.Int128", "System.Int128.MaxValue")]
    [InlineData("System.Int128", "System.Int128.MinValue")]
    [InlineData("System.UInt128", "System.UInt128.MaxValue")]
    public void Supported_Primitive_Round_Trips(string clrType, string valueLiteral)
    {
        // Block-scoped namespace so the appended factory shares the namespace without
        // triggering CS8955 (can't mix file-scoped + block-scoped in one file).
        var factorySource = $$"""
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [StorageSerializable]
                public partial record struct Holder([property: Tag(1)] {{clrType}} V);

                public static class HolderFactory
                {
                    public static Holder Make() => new Holder({{valueLiteral}});
                }
            }
            """;

        var asm = CompileAndLoad(factorySource);
        var factory = asm.GetType("TestNamespace.HolderFactory")!;
        var original = factory.GetMethod("Make")!.Invoke(null, null)!;

        var bytes = Serialize(original);
        var roundtrip = Deserialize(asm, "TestNamespace.Holder", bytes);

        roundtrip.Should().Be(original,
            "tagged-wire serialization must round-trip every supported primitive kind");
    }

    // A nullable-enum property must generate tagged-wire code and round-trip both a present value
    // and null. The classifier dropped the inner enum's underlying kind when wrapping it in
    // Nullable, so the emitter produced ZERO source for the whole record (a warning, not an error)
    // and the type was silently non-serializable.
    [Theory]
    [InlineData(2)]   // present enum value
    [InlineData(-1)]  // null
    public void Nullable_Enum_Tagged_Wire_Round_Trips(int rawValue)
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                public enum Gran : byte { Daily, Hourly, Minute }

                [StorageSerializable]
                public partial record struct Holder([property: Tag(1)] Gran? V);

                public static class HolderFactory
                {
                    public static Holder Make(int raw) => new Holder(raw < 0 ? (Gran?)null : (Gran)raw);
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var original = asm.GetType("TestNamespace.HolderFactory")!
            .GetMethod("Make")!.Invoke(null, [rawValue])!;

        var bytes = Serialize(original);
        var roundtrip = Deserialize(asm, "TestNamespace.Holder", bytes);

        roundtrip.Should().Be(original,
            "tagged-wire serialization must round-trip a nullable enum, present and null");
    }

    // Forward compatibility: a reader compiled against an older
    // schema (fewer tags) skips unknown tags gracefully and populates known fields.
    [Fact]
    public void Old_Reader_Skips_Unknown_Tag_From_Newer_Writer()
    {
        // New schema: three fields (tags 1, 2, 3).
        const string newSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace NewSchema;
            [StorageSerializable]
            public partial record struct Evolved(
                [property: Tag(1)] long A,
                [property: Tag(2)] long B,
                [property: Tag(3)] string C);
            """;
        var newAsm = CompileAndLoad(newSchema);
        var newValue = InstantiateRecord(newAsm, "NewSchema.Evolved", 10L, 20L, "thirty");
        var bytes = Serialize(newValue);

        // Old schema: only two fields (tags 1 and 2).
        const string oldSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace OldSchema;
            [StorageSerializable]
            public partial record struct Evolved(
                [property: Tag(1)] long A,
                [property: Tag(2)] long B);
            """;
        var oldAsm = CompileAndLoad(oldSchema);

        // Old reader must succeed, populating A and B from the new data.
        var roundtrip = Deserialize(oldAsm, "OldSchema.Evolved", bytes);
        var aValue = roundtrip.GetType().GetProperty("A")!.GetValue(roundtrip);
        var bValue = roundtrip.GetType().GetProperty("B")!.GetValue(roundtrip);

        aValue.Should().Be(10L);
        bValue.Should().Be(20L);
    }

    // Backward compatibility: a reader with a field the writer
    // omitted sees the default for that field.
    [Fact]
    public void New_Reader_Defaults_Missing_Tag_From_Older_Writer()
    {
        // Old schema: only tag 1.
        const string oldSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace OldSchema;
            [StorageSerializable]
            public partial record struct Evolved([property: Tag(1)] long A);
            """;
        var oldAsm = CompileAndLoad(oldSchema);
        var oldValue = InstantiateRecord(oldAsm, "OldSchema.Evolved", 99L);
        var bytes = Serialize(oldValue);

        // New schema: tag 1 plus tag 2 (the old writer didn't emit tag 2).
        const string newSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace NewSchema;
            [StorageSerializable]
            public partial record struct Evolved(
                [property: Tag(1)] long A,
                [property: Tag(2)] string B);
            """;
        var newAsm = CompileAndLoad(newSchema);
        var roundtrip = Deserialize(newAsm, "NewSchema.Evolved", bytes);

        var aValue = roundtrip.GetType().GetProperty("A")!.GetValue(roundtrip);
        var bValue = roundtrip.GetType().GetProperty("B")!.GetValue(roundtrip);

        aValue.Should().Be(99L, "existing field should deserialize unchanged");
        // Absent tag for a non-nullable string yields string.Empty, NOT null. DDIA p.
        // 171 prescribes "" for missing strings; previously the emitter defaulted to `null`
        // which NRE'd a forward-compatible reader walking a record whose writer omitted the tag.
        bValue.Should().Be(string.Empty);
    }

    // Narrowing scalar reads surface OverflowException on truncation. A writer that
    // emitted a wider scalar (long) must not have its value silently truncated when a reader
    // schema declares a narrower scalar (short) for the same tag. DDIA p. 171 frames this as
    // a hazard the reader must surface, not silently swallow.
    [Fact]
    public void Narrowing_Scalar_Read_Throws_OverflowException_On_Truncation()
    {
        // Writer schema: tag 1 holds a long (big enough that it cannot fit a short).
        const string wideSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace WideSchema;
            [StorageSerializable]
            public partial record struct Wide([property: Tag(1)] long Volume);
            """;
        var wideAsm = CompileAndLoad(wideSchema);
        // 100_000 cannot fit a short (range [-32768, 32767]); the read must surface OverflowException.
        var wideValue = InstantiateRecord(wideAsm, "WideSchema.Wide", 100_000L);
        var bytes = Serialize(wideValue);

        // Reader schema: same tag, narrower type (short).
        const string narrowSchema = """
            using Boutquin.Storage.Domain.Attributes;
            namespace NarrowSchema;
            [StorageSerializable]
            public partial record struct Narrow([property: Tag(1)] short Volume);
            """;
        var narrowAsm = CompileAndLoad(narrowSchema);

        Action act = () => Deserialize(narrowAsm, "NarrowSchema.Narrow", bytes);
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<OverflowException>(
                "narrowing scalar reads use checked() so a writer's value that does not fit the reader's narrower type surfaces at the read site");
    }

    // The fail-loud narrowing contract extends to the Tier-3 reads that narrow internally: a DateOnly
    // day-number varint that exceeds Int32 must throw, not truncate to the low 32 bits and
    // silently produce a wrong date (the DDIA Ch. 5 truncation hazard, same as the short/int
    // scalar case above).
    [Fact]
    public void Reader_DateOnly_DayNumberBeyondInt32_Throws()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace DateOnlyOverflow;
            [StorageSerializable]
            public partial record struct R([property: Tag(1)] System.DateOnly D);
            """;
        var asm = CompileAndLoad(source);

        using var stream = new MemoryStream();
        using (var payload = new MemoryStream())
        {
            payload.WriteByte(0x00); // tag-0 varint
            payload.WriteByte(0x01); // schema-format version 1
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteTag(
                payload, 1, Boutquin.Storage.Domain.Helpers.WireType.Varint);
            // Day number 2^32 + 100: low 32 bits are 100, so an unchecked (int) cast silently
            // yields DateOnly day 100 (0001-04-11) instead of failing.
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(payload, (1UL << 32) + 100);
            var data = payload.ToArray();
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(stream, (ulong)data.Length);
            stream.Write(data);
        }

        Action act = () => Deserialize(asm, "DateOnlyOverflow.R", stream.ToArray());
        // S1: the generated decoder now routes the day-number through CorruptionGuards.ToDateOnly,
        // which range-checks the full 64-bit decoded value and throws the TYPED InvalidDataException
        // corruption signal — instead of leaking a raw BCL exception (the prior checked((int)) only
        // caught the >Int32 arm as OverflowException; the in-Int32-out-of-range arm leaked AOORE).
        // The "fail loud, not truncate" intent is preserved; only the exception type tightens.
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidDataException>(
                "a day-number varint beyond DateOnly's domain is corrupt or evolved data and must fail loud with the typed corruption signal, not truncate");
    }

    [Fact]
    public void Reader_DateTimeOffset_OffsetMinutesBeyondInt32_Throws()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace DtoOffsetOverflow;
            [StorageSerializable]
            public partial record struct R([property: Tag(1)] System.DateTimeOffset At);
            """;
        var asm = CompileAndLoad(source);

        using var stream = new MemoryStream();
        using (var payload = new MemoryStream())
        {
            payload.WriteByte(0x00); // tag-0 varint
            payload.WriteByte(0x01); // schema-format version 1
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteTag(
                payload, 1, Boutquin.Storage.Domain.Helpers.WireType.LengthPrefixed);
            using var block = new MemoryStream();
            // Valid clock ticks followed by a zigzag offset-minutes of 2^33 + 120 — far beyond the
            // DateTimeOffset ±14h (840-minute) cap. The low 32 bits are 120, so the prior unchecked
            // (int) cast would have silently yielded a +02:00 offset; the prior checked() backstop
            // caught only the >Int32 arm as OverflowException.
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(
                block, (ulong)new DateTime(2026, 4, 22, 12, 0, 0, DateTimeKind.Unspecified).Ticks);
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteZigZagInt64(block, (1L << 33) + 120);
            var blockBytes = block.ToArray();
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(payload, (ulong)blockBytes.Length);
            payload.Write(blockBytes);
            var data = payload.ToArray();
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(stream, (ulong)data.Length);
            stream.Write(data);
        }

        Action act = () => Deserialize(asm, "DtoOffsetOverflow.R", stream.ToArray());
        // F-W1-03: the generated decoder now routes the decoded offset minutes through
        // CorruptionGuards.ToDateTimeOffsetFromLocal, which range-checks the ±14h offset cap and
        // throws the TYPED InvalidDataException corruption signal instead of the raw OverflowException
        // the prior checked((int)) backstop produced. The "fail loud, not truncate" intent is
        // preserved; only the exception type tightens (mirrors the round-5 TimeOnly/DateOnly arms).
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidDataException>(
                "an offset-minutes value beyond the DateTimeOffset ±14h cap is corrupt or evolved data and must fail loud with the typed corruption signal, not truncate");
    }

    // Characterization pins for the reinterpret-cast Tier-3 reads. Post-S1/F-W1-03, DateOnly,
    // TimeOnly, decimal, AND DateTimeOffset all route their decoded value through the CorruptionGuards
    // range-checking helpers, which throw the typed InvalidDataException corruption signal. A corrupt
    // varint reinterpreting to an out-of-domain value must fail loud — never silently fabricate one.
    [Fact]
    public void Reader_CorruptTimeOnlyTicks_FailsLoud()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TimeOnlyCorruptTicks;
            [StorageSerializable]
            public partial record struct R([property: Tag(1)] System.TimeOnly At);
            """;
        var asm = CompileAndLoad(source);

        using var stream = new MemoryStream();
        using (var payload = new MemoryStream())
        {
            payload.WriteByte(0x00); // tag-0 varint
            payload.WriteByte(0x01); // schema-format version 1
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteTag(
                payload, 1, Boutquin.Storage.Domain.Helpers.WireType.Varint);
            // Reinterprets to a negative long tick count via (long) — the TimeOnly ctor
            // is the backstop.
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(payload, ulong.MaxValue);
            var data = payload.ToArray();
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(stream, (ulong)data.Length);
            stream.Write(data);
        }

        Action act = () => Deserialize(asm, "TimeOnlyCorruptTicks.R", stream.ToArray());
        // S1: the generated decoder now routes the tick count through CorruptionGuards.ToTimeOnly,
        // which range-checks [0, 863 999 999 999] and throws the TYPED InvalidDataException
        // corruption signal instead of the raw ArgumentOutOfRangeException the bare TimeOnly ctor
        // backstop produced. The "fail loud, not fabricate" intent is preserved; the type tightens.
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidDataException>(
                "a tick count outside TimeOnly's domain is corrupt data and must fail loud with the typed corruption signal, not fabricate a time");
    }

    [Fact]
    public void Reader_CorruptDateTimeOffsetClockTicks_FailsLoud()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace DtoCorruptClockTicks;
            [StorageSerializable]
            public partial record struct R([property: Tag(1)] System.DateTimeOffset At);
            """;
        var asm = CompileAndLoad(source);

        using var stream = new MemoryStream();
        using (var payload = new MemoryStream())
        {
            payload.WriteByte(0x00); // tag-0 varint
            payload.WriteByte(0x01); // schema-format version 1
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteTag(
                payload, 1, Boutquin.Storage.Domain.Helpers.WireType.LengthPrefixed);
            using var block = new MemoryStream();
            // Clock ticks reinterpreting to a negative long — out of the DateTime tick domain.
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(block, ulong.MaxValue);
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteZigZagInt64(block, 0);
            var blockBytes = block.ToArray();
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(payload, (ulong)blockBytes.Length);
            payload.Write(blockBytes);
            var data = payload.ToArray();
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(stream, (ulong)data.Length);
            stream.Write(data);
        }

        Action act = () => Deserialize(asm, "DtoCorruptClockTicks.R", stream.ToArray());
        // F-W1-03: the generated decoder now routes the decoded clock ticks through
        // CorruptionGuards.ToDateTimeOffsetFromLocal, which range-checks the DateTime tick domain and
        // throws the TYPED InvalidDataException corruption signal instead of the raw
        // ArgumentOutOfRangeException the bare DateTime ctor backstop produced. The "fail loud, not
        // fabricate" intent is preserved; only the exception type tightens.
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidDataException>(
                "a clock-tick count outside DateTime's domain is corrupt data and must fail loud with the typed corruption signal, not fabricate an instant");
    }

    // Corrupt-input allocation bound: the count prefix of a collection block is
    // corruption-controlled data. A 5-byte block declaring int.MaxValue elements must fail
    // loud as corrupt data, not attempt a List backing array of ~17 GB (OutOfMemoryException).
    [Fact]
    public void Reader_CollectionCountBeyondBlockBytes_ThrowsInvalidData()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace CollectionCountBomb;
            [StorageSerializable]
            public partial record struct R([property: Tag(1)] IEnumerable<string> Tags);
            """;
        var asm = CompileAndLoad(source);

        using var stream = new MemoryStream();
        using (var payload = new MemoryStream())
        {
            payload.WriteByte(0x00); // tag-0 varint
            payload.WriteByte(0x01); // schema-format version 1
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteTag(
                payload, 1, Boutquin.Storage.Domain.Helpers.WireType.LengthPrefixed);
            using var block = new MemoryStream();
            // Element count of int.MaxValue inside a block that carries no element bytes.
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(block, int.MaxValue);
            var blockBytes = block.ToArray();
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(payload, (ulong)blockBytes.Length);
            payload.Write(blockBytes);
            var data = payload.ToArray();
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(stream, (ulong)data.Length);
            stream.Write(data);
        }

        Action act = () => Deserialize(asm, "CollectionCountBomb.R", stream.ToArray());
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidDataException>(
                "an element count beyond the block's byte budget is corrupt data and must fail loud, not allocate");
    }

    // Protobuf semantics: repeated occurrences of the same field tag CONCATENATE. The packed
    // and unpacked readers already append; the count-prefixed reader must not replace the
    // accumulated list when a foreign writer emits two blocks for one tag.
    [Fact]
    public void Reader_DuplicateCountPrefixedBlocks_Concatenate()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace DupBlocks;
            [StorageSerializable]
            public partial record struct R([property: Tag(1)] IEnumerable<string> Tags);
            """;
        var asm = CompileAndLoad(source);

        using var stream = new MemoryStream();
        using (var payload = new MemoryStream())
        {
            payload.WriteByte(0x00); // tag-0 varint
            payload.WriteByte(0x01); // schema-format version 1
            WriteCountPrefixedStringBlock(payload, "alpha", "beta");
            WriteCountPrefixedStringBlock(payload, "gamma");
            var data = payload.ToArray();
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(stream, (ulong)data.Length);
            stream.Write(data);
        }

        var value = Deserialize(asm, "DupBlocks.R", stream.ToArray());
        var tags = ((System.Collections.Generic.IEnumerable<string>)value.GetType().GetProperty("Tags")!.GetValue(value)!).ToList();

        tags.Should().Equal(["alpha", "beta", "gamma"],
            "repeated occurrences of one tag concatenate (Protobuf semantics), matching the packed/unpacked readers");
    }

    private static void WriteCountPrefixedStringBlock(Stream payload, params string[] elements)
    {
        Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteTag(
            payload, 1, Boutquin.Storage.Domain.Helpers.WireType.LengthPrefixed);
        using var block = new MemoryStream();
        Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(block, (ulong)elements.Length);
        foreach (var element in elements)
        {
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteString(block, element);
        }

        var blockBytes = block.ToArray();
        Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(payload, (ulong)blockBytes.Length);
        payload.Write(blockBytes);
    }

    // Bucket-4 pin: the generated reader's tag-0 version gate for a HIGHER version than it
    // knows. (The missing-tag-0 case is pinned above; the version≠1 case was only covered for
    // the hand-rolled SerializableWrapper, not generated code.)
    [Fact]
    public void Reader_SchemaFormatVersionTwo_ThrowsUnsupportedFormatVersion()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace VersionTwo;
            [StorageSerializable]
            public partial record struct R([property: Tag(1)] long A);
            """;
        var asm = CompileAndLoad(source);

        using var stream = new MemoryStream();
        using (var payload = new MemoryStream())
        {
            payload.WriteByte(0x00); // tag-0 varint
            payload.WriteByte(0x02); // schema-format version 2 — unknown to this reader
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteTag(
                payload, 1, Boutquin.Storage.Domain.Helpers.WireType.Varint);
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteZigZagInt64(payload, 42);
            var data = payload.ToArray();
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(stream, (ulong)data.Length);
            stream.Write(data);
        }

        Action act = () => Deserialize(asm, "VersionTwo.R", stream.ToArray());
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<Boutquin.Storage.Domain.Exceptions.UnsupportedFormatVersionException>()
            .WithMessage("*version 2*");
    }

    // Error-surface consistency: a null collection (or null record-class nested value) must get
    // the same guarded InvalidOperationException a null string gets, not a raw
    // NullReferenceException from deep inside Serialize.
    [Fact]
    public void Serialize_NullCollection_ThrowsGuardedInvalidOperation()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace NullColl;
            [StorageSerializable]
            public partial record struct R([property: Tag(1)] IEnumerable<int> Values);
            """;
        var asm = CompileAndLoad(source);
        var t = asm.GetType("NullColl.R")!;
        var value = Activator.CreateInstance(t, [(System.Collections.Generic.IEnumerable<int>?)null])!;

        var act = () => Serialize(value);

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*Values*");
    }

    // Bucket-4 pin: IEEE 754 special values round-trip BIT-EXACTLY on the tagged wire — the
    // little-endian fixed encodings must preserve NaN payloads (including the sign bit) and
    // infinities, not collapse them through a float-arithmetic path. (The ordered wire rejects
    // NaN keys; the tagged VALUE wire must carry them faithfully.)
    [Theory]
    [InlineData(0x7FF8_0000_0000_0001UL)] // quiet NaN with payload bit set
    [InlineData(0xFFF8_0000_0000_0000UL)] // negative quiet NaN
    [InlineData(0x7FF0_0000_0000_0000UL)] // +Infinity
    [InlineData(0xFFF0_0000_0000_0000UL)] // -Infinity
    [InlineData(0x8000_0000_0000_0000UL)] // -0.0
    public void Double_SpecialBitPatterns_RoundTrip_BitExactly(ulong bits)
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace DoubleBits;
            [StorageSerializable]
            public partial record struct R([property: Tag(1)] double V);
            """;
        var asm = CompileAndLoad(source);
        var t = asm.GetType("DoubleBits.R")!;
        var original = Activator.CreateInstance(t, BitConverter.UInt64BitsToDouble(bits))!;

        var round = Deserialize(asm, "DoubleBits.R", Serialize(original));

        var roundBits = BitConverter.DoubleToUInt64Bits((double)t.GetProperty("V")!.GetValue(round)!);
        roundBits.Should().Be(bits, "the tagged wire must preserve IEEE 754 bit patterns exactly");
    }

    // Bucket-4 pin: an EMPTY packed collection round-trips (the writer's packed block carries
    // zero element bodies; the reader must produce an empty list, not fail or fabricate).
    [Fact]
    public void EmptyPackedCollection_RoundTrips()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            using System.Linq;
            namespace EmptyPacked;
            [StorageSerializable]
            public partial record struct R([property: Tag(1)] IEnumerable<int> Values);
            public static class F { public static R Make() => new R(System.Linq.Enumerable.Empty<int>()); }
            """;
        var asm = CompileAndLoad(source);
        var original = asm.GetType("EmptyPacked.F")!.GetMethod("Make")!.Invoke(null, null)!;

        var round = Deserialize(asm, "EmptyPacked.R", Serialize(original));

        var values = (System.Collections.Generic.IEnumerable<int>)round.GetType().GetProperty("Values")!.GetValue(round)!;
        values.Should().BeEmpty("an empty packed collection must round-trip to an empty list");
    }

    // Reading 1.x-style (untagged, no schema-format version) data must fail fast with
    // UnsupportedFormatVersionException, not silently corrupt.
    [Fact]
    public void Reader_Rejects_Data_Missing_Tag_Zero_Version_Byte()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Simple([property: Tag(1)] long A);
            """;
        var asm = CompileAndLoad(source);

        // Craft bytes with a length prefix but no tag-0 schema-format version — just a
        // tag-1 varint payload. Simulates a 1.x-style untagged stream that happens to be
        // length-prefixed upstream.
        using var stream = new MemoryStream();
        using (var payload = new MemoryStream())
        {
            // tag 1, varint wire, value 42 (zigzag = 84)
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteTag(
                payload, 1, Boutquin.Storage.Domain.Helpers.WireType.Varint);
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteZigZagInt64(payload, 42);
            var data = payload.ToArray();
            Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(stream, (ulong)data.Length);
            stream.Write(data);
        }
        stream.Position = 0;

        Action act = () => Deserialize(asm, "TestNamespace.Simple", stream.ToArray());
        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<Boutquin.Storage.Domain.Exceptions.UnsupportedFormatVersionException>();
    }

    // BSSG012 must reject [Tag(0)]. (Tag 0 is reserved for the schema-format version.)
    // Already covered in TagValidationTests; this test is a second guard at the tagged-wire
    // layer to document the contract from the emitter's perspective.
    [Fact]
    public void User_Cannot_Assign_Tag_Zero_Through_The_Emitter_Path()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace;
            [StorageSerializable]
            public partial record struct Invalid([property: Tag(0)] long A);
            """;

        var (_, runResult) = TestHelpers.RunGenerator(source);
        runResult.Diagnostics.Should().Contain(d => d.Id == "BSSG012");
    }

    // Nested [StorageSerializable] record round-trips over tagged wire.
    // An outer record that has a property typed as another [StorageSerializable] record must
    // emit and recover the nested value correctly — the inner Serialize output is written as a
    // LengthPrefixed field and the inner Deserialize is invoked on read.
    [Fact]
    public void Nested_StorageSerializable_Record_Round_Trips()
    {
        const string source = """
            using Boutquin.Storage.Domain.Attributes;
            namespace TestNamespace
            {
                [StorageSerializable]
                public partial record struct Inner([property: Tag(1)] int X, [property: Tag(2)] string Label);

                [StorageSerializable]
                public partial record struct Outer([property: Tag(1)] long Id, [property: Tag(2)] Inner Nested);

                public static class OuterFactory
                {
                    public static Outer Make() => new Outer(99L, new Inner(7, "hello"));
                }
            }
            """;

        var asm = CompileAndLoad(source);
        var factory = asm.GetType("TestNamespace.OuterFactory")!;
        var original = factory.GetMethod("Make")!.Invoke(null, null)!;

        var bytes = Serialize(original);
        var roundtrip = Deserialize(asm, "TestNamespace.Outer", bytes);

        roundtrip.Should().Be(original,
            "nested [StorageSerializable] records must round-trip through tagged-wire serialize/deserialize");
    }

    // Primitive collection round-trips over tagged wire (length-prefixed).
    [Fact]
    public void Primitive_Collection_Round_Trips()
    {
        const string factorySource = """
            using Boutquin.Storage.Domain.Attributes;
            using System.Collections.Generic;
            namespace TestNamespace
            {
                [StorageSerializable]
                public partial record struct Bag([property: Tag(1)] IEnumerable<long> Values);

                public static class BagFactory
                {
                    public static Bag Make() => new Bag(new long[] { 1, 2, 3, 100_000, -1 });
                }
            }
            """;
        var asm = CompileAndLoad(factorySource);
        var factory = asm.GetType("TestNamespace.BagFactory")!;
        var original = factory.GetMethod("Make")!.Invoke(null, null)!;

        var bytes = Serialize(original);
        var roundtrip = Deserialize(asm, "TestNamespace.Bag", bytes);

        var originalValues = (IEnumerable<long>)original.GetType().GetProperty("Values")!.GetValue(original)!;
        var roundtripValues = (IEnumerable<long>)roundtrip.GetType().GetProperty("Values")!.GetValue(roundtrip)!;

        roundtripValues.Should().Equal(originalValues);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Helpers — compile the generator output into a live assembly via reflection emit so we
    // can actually execute Serialize/Deserialize on generated records.
    // ═════════════════════════════════════════════════════════════════════════════════════

    private static Assembly CompileAndLoad(string source)
    {
        // Unique assembly names per compile so AssemblyLoadContext.Default doesn't refuse a
        // reload during the same test-process lifetime.
        var asmName = "TestAsm_" + System.Guid.NewGuid().ToString("N");
        var compilation = TestHelpers.CreateCompilation(source, assemblyName: asmName);
        var generator = new StorageSourceGenerator();
        var driver = CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        // Surface any compile errors in the generated code before emit — diagnostics from the
        // emitter would otherwise fail with an opaque "emit failed" later.
        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        errors.Should().BeEmpty(
            because: "generated code must compile before we can exercise it; got:\n" +
                     string.Join("\n", errors.Select(d => d.ToString())));

        using var ms = new MemoryStream();
        var result = outputCompilation.Emit(ms);
        result.Success.Should().BeTrue(
            because: "generated code must emit successfully for runtime tests");

        ms.Position = 0;
        return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    private static object InstantiateRecord(Assembly asm, string typeFqn, params object[] ctorArgs)
    {
        var t = asm.GetType(typeFqn) ?? throw new InvalidOperationException($"Type {typeFqn} not found.");
        return Activator.CreateInstance(t, ctorArgs)!;
    }

    private static byte[] Serialize(object record)
    {
        var method = record.GetType().GetMethod("Serialize", [typeof(Stream)])
            ?? throw new InvalidOperationException("Record lacks Serialize(Stream).");
        using var ms = new MemoryStream();
        method.Invoke(record, [ms]);
        return ms.ToArray();
    }

    private static object Deserialize(Assembly asm, string typeFqn, byte[] bytes)
    {
        var t = asm.GetType(typeFqn) ?? throw new InvalidOperationException($"Type {typeFqn} not found.");
        var method = t.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Type {typeFqn} lacks static Deserialize(Stream).");
        using var ms = new MemoryStream(bytes, writable: false);
        return method.Invoke(null, [ms])!;
    }
}
