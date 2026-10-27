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
using Boutquin.Storage.Domain.Attributes;
using Boutquin.Storage.Infrastructure.Schemas;

namespace Boutquin.Storage.Infrastructure.Tests.Schemas;

// Generations of one enum whose UNDERLYING integer type changes while its name stays the same —
// the shape an enum evolving in place presents to the checker. The generated encoder writes an
// enum as its underlying integer (zig-zag varint when signed, plain varint when unsigned), so the
// underlying type, not the enum's name, is the field's wire identity.
public static partial class EnumUnderlyingInt
{
    public enum Shade { None = 0, One = 1, Two = 2 }

    [StorageSerializable]
    public partial record struct Paint([property: Tag(1)] Shade Color);

    [StorageSerializable]
    public partial record struct MaybePaint([property: Tag(1)] Shade? Color);

    [StorageSerializable]
    public partial record struct Palette([property: Tag(1)] IEnumerable<Shade> Colors);
}

public static partial class EnumUnderlyingUInt
{
    public enum Shade : uint { None = 0, One = 1, Two = 2 }

    [StorageSerializable]
    public partial record struct Paint([property: Tag(1)] Shade Color);

    [StorageSerializable]
    public partial record struct MaybePaint([property: Tag(1)] Shade? Color);

    [StorageSerializable]
    public partial record struct Palette([property: Tag(1)] IEnumerable<Shade> Colors);
}

public static partial class EnumUnderlyingLong
{
    public enum Shade : long { None = 0, One = 1, Two = 2 }

    [StorageSerializable]
    public partial record struct Paint([property: Tag(1)] Shade Color);

    [StorageSerializable]
    public partial record struct Palette([property: Tag(1)] IEnumerable<Shade> Colors);
}

public static partial class EnumUnderlyingSByte
{
    public enum Shade : sbyte { None = 0, One = 1, Two = 2 }

    [StorageSerializable]
    public partial record struct Paint([property: Tag(1)] Shade Color);
}

public static partial class EnumUnderlyingIntCopy
{
    public enum Shade { None = 0, One = 1, Two = 2 }

    [StorageSerializable]
    public partial record struct Paint([property: Tag(1)] Shade Color);

    [StorageSerializable]
    public partial record struct Palette([property: Tag(1)] IEnumerable<Shade> Colors);
}

/// <summary>
/// Tests for <see cref="TaggedWireCompatibilityChecker"/> — the Protobuf-family wire-compatibility
/// rules over <see cref="TaggedWireSchema"/>.
/// </summary>
public sealed class TaggedWireCompatibilityCheckerTests
{
    private readonly TaggedWireCompatibilityChecker _checker = new();

    private static TaggedWireSchema Schema(IReadOnlyList<TaggedWireField> fields, params int[] reserved)
        => new(fields, reserved);

    private static TaggedWireField Field(int tag, string name, string type, bool optional = false)
        => new(tag, name, type, optional);

    [Fact]
    public void Backward_AddNewTag_Compatible()
    {
        var existing = Schema([Field(1, "Name", "string")]);
        var candidate = Schema([Field(1, "Name", "string"), Field(2, "Age", "int", optional: true)]);

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeTrue();
    }

    [Fact]
    public void Forward_OmitWriterTag_Compatible()
    {
        // Older reader (candidate) sees a field the newer writer (existing) had; unknown-tag-skip.
        var existing = Schema([Field(1, "Name", "string"), Field(2, "Age", "int")]);
        var candidate = Schema([Field(1, "Name", "string")]);

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Forward).Should().BeTrue();
    }

    [Fact]
    public void IntToLong_VarintWideningOnStableTag_IsBackwardOnly()
    {
        // Widening a stable tag's int to long is safe in the BACKWARD direction only: the new
        // (long) reader fills in the missing bits of old int bytes (DDIA 2e Ch. 5 — "the parser
        // can fill in any missing bits with zeros"). In the FORWARD direction an old int reader
        // decodes new 64-bit values with checked((int)…) and overflows on any value past 2³¹ —
        // the book's truncation hazard, made loud by the generated reader. Full = both → fails.
        var existing = Schema([Field(1, "Value", "int")]);
        var candidate = Schema([Field(1, "Value", "long")]); // same tag + name, varint widening

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeTrue(
            "the new long reader decodes every value the old int writer produced");
        _checker.IsCompatible(existing, candidate, CompatibilityMode.Forward).Should().BeFalse(
            "an old int reader overflows on new long values past int.MaxValue");
        _checker.IsCompatible(existing, candidate, CompatibilityMode.Full).Should().BeFalse(
            "Full requires both directions and the forward direction is unsafe");
    }

    [Fact]
    public void LongToInt_VarintNarrowingOnStableTag_IsForwardOnly()
    {
        // Narrowing long→int is the book's truncation hazard in the BACKWARD direction: the new
        // int reader hits old 64-bit values with checked((int)…) → OverflowException at read
        // time on any stored value past 2³¹. The FORWARD direction is safe (an old long reader
        // decodes every int the new writer produces). A checker that blesses narrowing in
        // Backward or Full turns an accepted schema registration into a read-time crash on
        // durably stored data.
        var existing = Schema([Field(1, "Value", "long")]);
        var candidate = Schema([Field(1, "Value", "int")]); // same tag + name, varint narrowing

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeFalse(
            "the new int reader overflows on old long values past int.MaxValue");
        _checker.IsCompatible(existing, candidate, CompatibilityMode.Forward).Should().BeTrue(
            "the old long reader decodes every value the new int writer produces");
        _checker.IsCompatible(existing, candidate, CompatibilityMode.Full).Should().BeFalse(
            "Full requires both directions and the backward direction is unsafe");
    }

    [Theory]
    [InlineData("ulong", "uint")]   // unsigned narrowing
    [InlineData("long", "short")]   // multi-step signed narrowing
    [InlineData("int", "sbyte")]    // signed narrowing to 8-bit
    public void VarintNarrowing_OtherWidths_FailsBackwardAndFull(string existingType, string candidateType)
    {
        var existing = Schema([Field(1, "Value", existingType)]);
        var candidate = Schema([Field(1, "Value", candidateType)]);

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeFalse();
        _checker.IsCompatible(existing, candidate, CompatibilityMode.Full).Should().BeFalse();
    }

    [Fact]
    public void GenericTypeSpellings_QualifiedAndKeyword_AreOneWireIdentity()
    {
        // Normalization must strip namespace qualification per identifier segment. A whole-string
        // LastIndexOf('.') on "List<System.Int64>" cuts into the type-argument list (→ "int64>"),
        // so the same collection type spelled two ways lands in two different classes and a
        // legitimate re-registration is rejected.
        var existing = Schema([Field(1, "Xs", "List<long>")]);
        var candidate = Schema([Field(1, "Xs", "List<System.Int64>")]);

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Full).Should().BeTrue(
            "List<long> and List<System.Int64> are the same wire identity spelled two ways");
    }

    [Fact]
    public void DifferentGenericTypes_SharingTypeArgumentSimpleName_AreIncompatible()
    {
        // The corrupted normalization also collapses DIFFERENT generic types into one class when
        // their final type-argument simple names match ("List<A.Foo>" and "HashSet<B.Foo>" both
        // → "foo>") — a false-compatible verdict on a stable tag.
        var existing = Schema([Field(1, "Xs", "List<A.Foo>")]);
        var candidate = Schema([Field(1, "Xs", "HashSet<B.Foo>")]);

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Full).Should().BeFalse(
            "a List and a HashSet of different element types are not one wire identity");
    }

    [Fact]
    public void SameWidthSpellings_Int32AndInt_CompatibleInAllModes()
    {
        // Same width, same family — two spellings of one wire identity.
        var existing = Schema([Field(1, "Value", "System.Int32")]);
        var candidate = Schema([Field(1, "Value", "int")]);

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeTrue();
        _checker.IsCompatible(existing, candidate, CompatibilityMode.Forward).Should().BeTrue();
        _checker.IsCompatible(existing, candidate, CompatibilityMode.Full).Should().BeTrue();
    }

    [Fact]
    public void IntToUint_SignedToUnsignedVarint_IsIncompatible()
    {
        // int and uint share the Varint *framing*, but the emitter writes a signed int as zig-zag
        // (Protobuf sint32) and an unsigned uint as plain varint (Protobuf int32/uint32). The byte
        // encodings are not interchangeable: a reader on the new uint schema reading old int bytes
        // calls ReadVarUInt64 on zig-zag bytes and silently corrupts the value (e.g. int -1 → uint 1).
        // Comparing the framing WireType alone passed this as compatible (finding #5, sibling of the
        // float/double case).
        var existing = Schema([Field(1, "Value", "int")]);
        var candidate = Schema([Field(1, "Value", "uint")]);

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeFalse();
    }

    [Fact]
    public void UintToUlong_PlainVarintWideningOnStableTag_IsBackwardOnly()
    {
        // Positive control: two unsigned integers share the plain-varint encoding, so widening uint
        // to ulong is wire-compatible in the BACKWARD direction (new ulong reader, old uint bytes).
        // Full fails: the forward direction leaves an old uint reader overflowing on new 64-bit values.
        var existing = Schema([Field(1, "Value", "uint")]);
        var candidate = Schema([Field(1, "Value", "ulong")]);

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeTrue();
        _checker.IsCompatible(existing, candidate, CompatibilityMode.Full).Should().BeFalse();
    }

    [Fact]
    public void TypeChangeAcrossWireTypesOnStableTag_Incompatible()
    {
        // int (Varint) → double (Fixed64) is a genuine wire-type change on a stable tag: the bytes a
        // writer emitted under the tag cannot be decoded as the new type. This is the real
        // incompatibility the checker must catch (distinct from the wire-compatible int→long above).
        var existing = Schema([Field(1, "Value", "int")]);
        var candidate = Schema([Field(1, "Value", "double")]);

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeFalse();
    }

    [Theory]
    [InlineData("guid", "decimal")]            // Fixed128 bucket — unrelated 16-byte layouts
    [InlineData("int128", "uint128")]          // Fixed128 — signed/unsigned 16-byte mis-sign
    [InlineData("guid", "int128")]             // Fixed128
    [InlineData("decimal", "uint128")]         // Fixed128
    [InlineData("string", "datetimeoffset")]   // LengthPrefixed — UTF-8 vs ticks+offset block
    [InlineData("bool", "uint")]               // plain varint — bool vs unsigned int
    [InlineData("char", "ushort")]             // plain varint
    [InlineData("dateonly", "ulong")]          // plain varint — DayNumber vs number
    [InlineData("timeonly", "uint")]           // plain varint — Ticks vs number
    [InlineData("int", "timespan")]            // zig-zag varint — number vs ticks
    public void TypeChangeWithinSharedWireBucket_OnStableTag_IsIncompatible(string from, string to)
    {
        // #5 (class D): these pairs share a framing WireType (and even a varint sub-encoding) but
        // decode to different values, so a reader on the new schema silently misreads (or throws on)
        // old bytes. The int→uint fix split zig-zag from plain varint; this generalises it so the
        // Fixed128, LengthPrefixed, and remaining plain-varint buckets no longer collapse.
        var existing = Schema([Field(1, "Value", from)]);
        var candidate = Schema([Field(1, "Value", to)]);

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Full).Should().BeFalse(
            $"{from}->{to} share a wire-type bucket but are not byte-interchangeable");
    }

    [Theory]
    [InlineData("sbyte", "long")]   // signed-int family — zig-zag varint widening
    [InlineData("byte", "ulong")]   // unsigned-int family — plain varint widening
    [InlineData("short", "int")]    // signed-int family
    [InlineData("guid", "guid")]    // identity — a type is always compatible with itself
    public void WideningWithinNumericFamily_OrIdentity_IsBackwardCompatible(string from, string to)
    {
        var existing = Schema([Field(1, "Value", from)]);
        var candidate = Schema([Field(1, "Value", to)]);

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeTrue(
            $"{from}->{to} is a widening within one varint numeric family (safe for the new, wider reader) or an identity");
    }

    [Fact]
    public void TagCollision_DifferentFieldSameTag_Incompatible()
    {
        var existing = Schema([Field(1, "Name", "string")]);
        var candidate = Schema([Field(1, "Score", "double")]); // tag 1 repurposed for a different field

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Full).Should().BeFalse();
    }

    [Fact]
    public void ReusingReservedTag_Incompatible()
    {
        // Existing schema retired tag 2; candidate brings it back as a live field.
        var existing = Schema([Field(1, "Name", "string")], reserved: 2);
        var candidate = Schema([Field(1, "Name", "string"), Field(2, "Reborn", "string", optional: true)]);

        _checker.IsCompatible(existing, candidate, CompatibilityMode.Backward).Should().BeFalse();
    }

    [Fact]
    public void None_AlwaysCompatible_EvenWithTypeMismatch()
    {
        var existing = Schema([Field(1, "Value", "int")]);
        var candidate = Schema([Field(1, "Value", "double")]); // genuine wire-type change

        _checker.IsCompatible(existing, candidate, CompatibilityMode.None).Should().BeTrue();
    }

    public static TheoryData<string, CompatibilityMode> EnumSignednessChanges()
    {
        var data = new TheoryData<string, CompatibilityMode>();
        foreach (var shape in new[] { "scalar", "nullable", "collection" })
        {
            foreach (var mode in new[] { CompatibilityMode.Backward, CompatibilityMode.Forward, CompatibilityMode.Full })
            {
                data.Add(shape, mode);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EnumSignednessChanges))]
    public void EnumUnderlyingSignednessChange_OnStableTag_IsIncompatibleInEveryMode(string shape, CompatibilityMode mode)
    {
        // int -> uint underneath an unchanged enum name moves the tag from zig-zag to plain varint:
        // the wire type stays Varint, so a reader decodes old bytes silently to different values
        // (1 -> 2, 2 -> 4). The same change spelled as a raw int -> uint field is rejected.
        var (existing, candidate) = shape switch
        {
            "scalar" => (TaggedWireSchema.FromType<EnumUnderlyingInt.Paint>(), TaggedWireSchema.FromType<EnumUnderlyingUInt.Paint>()),
            "nullable" => (TaggedWireSchema.FromType<EnumUnderlyingInt.MaybePaint>(), TaggedWireSchema.FromType<EnumUnderlyingUInt.MaybePaint>()),
            _ => (TaggedWireSchema.FromType<EnumUnderlyingInt.Palette>(), TaggedWireSchema.FromType<EnumUnderlyingUInt.Palette>()),
        };

        _checker.IsCompatible(existing, candidate, mode).Should().BeFalse(
            $"a {shape} enum whose underlying type changes signedness leaves its wire-compatibility class");
        _checker.IsCompatible(Schema([Field(1, "Color", "int")]), Schema([Field(1, "Color", "uint")]), mode)
            .Should().BeFalse("the raw-integer spelling of the same wire change is already rejected");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void EnumUnderlyingWidthChange_OnStableTag_FollowsTheRawIntegerWidthRule(CompatibilityMode mode)
    {
        // An enum's bytes are its underlying integer's bytes, so a width change underneath an
        // unchanged enum name must get exactly the verdict the same raw-integer change gets.
        var asInt = TaggedWireSchema.FromType<EnumUnderlyingInt.Paint>();
        var cases = new[]
        {
            (Candidate: TaggedWireSchema.FromType<EnumUnderlyingLong.Paint>(), From: "int", To: "long"),
            (Candidate: TaggedWireSchema.FromType<EnumUnderlyingSByte.Paint>(), From: "int", To: "sbyte"),
        };

        foreach (var (candidate, from, to) in cases)
        {
            var rawVerdict = _checker.IsCompatible(
                Schema([Field(1, "Color", from)]), Schema([Field(1, "Color", to)]), mode);
            _checker.IsCompatible(asInt, candidate, mode).Should().Be(
                rawVerdict, $"enum underlying {from} -> {to} under {mode} is the raw {from} -> {to} change on the wire");
        }
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void EnumCollectionUnderlyingWidthChange_IsIncompatible_LikeTheRawIntegerCollection(CompatibilityMode mode)
    {
        _checker.IsCompatible(
            TaggedWireSchema.FromType<EnumUnderlyingInt.Palette>(),
            TaggedWireSchema.FromType<EnumUnderlyingLong.Palette>(),
            mode).Should().Be(
                _checker.IsCompatible(
                    Schema([Field(1, "Colors", "System.Collections.Generic.IEnumerable<int>")]),
                    Schema([Field(1, "Colors", "System.Collections.Generic.IEnumerable<long>")]),
                    mode),
                "a collection of enums carries its elements as the underlying integer collection does");
    }

    [Theory]
    [InlineData(CompatibilityMode.Backward)]
    [InlineData(CompatibilityMode.Forward)]
    [InlineData(CompatibilityMode.Full)]
    public void EnumWithUnchangedUnderlyingType_OnStableTag_StaysCompatible(CompatibilityMode mode)
    {
        _checker.IsCompatible(
            TaggedWireSchema.FromType<EnumUnderlyingInt.Paint>(),
            TaggedWireSchema.FromType<EnumUnderlyingIntCopy.Paint>(),
            mode).Should().BeTrue("the same enum name over the same underlying type is the same wire identity");
        _checker.IsCompatible(
            TaggedWireSchema.FromType<EnumUnderlyingInt.Palette>(),
            TaggedWireSchema.FromType<EnumUnderlyingIntCopy.Palette>(),
            mode).Should().BeTrue("the same holds for a collection of that enum");
    }
}
