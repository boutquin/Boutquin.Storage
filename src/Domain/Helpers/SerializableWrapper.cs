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
using Boutquin.Storage.Domain.Exceptions;

namespace Boutquin.Storage.Domain.Helpers;

/// <summary>
/// Generic wrapper class for serializing and deserializing built-in types.
/// </summary>
/// <typeparam name="T">The type to be serialized and deserialized.</typeparam>
/// <remarks>
/// <para>
/// <b>Why a wrapper class instead of direct serialization?</b> Built-in types (<c>int</c>,
/// <c>string</c>, etc.) don't implement <c>ISerializable</c>, but the storage engine interfaces
/// require <c>ISerializable</c> constraints on keys and values. <see cref="SerializableWrapper{T}"/>
/// bridges this gap by wrapping any built-in type with <c>ISerializable</c> + <c>IComparable</c>
/// implementations, making built-in types usable as storage engine keys/values without modifying
/// the interface constraints.
/// </para>
/// <para>
/// <b>Legacy test bridge — prefer annotated records for new code.</b> The wrapper predates the
/// Phase 2 source generator and exists for the in-memory test infrastructure that grew up
/// around <c>IStorageEngine&lt;TKey,TValue&gt;</c>. New consumers should declare a
/// <c>[Key]</c> or <c>[StorageSerializable]</c> record struct instead — the generator's
/// tagged-wire emitter (for values) and ordered-wire emitter (for keys) cover the full 22-kind
/// primitive set with tag-based schema evolution, whereas the wrapper's 8-type dispatch is
/// fixed. The wrapper stays in Domain because retrofitting the ~200+ Infrastructure test call
/// sites to annotated records would be invasive without raising any test's signal.
/// </para>
/// <para>
/// <b>Wire format.</b> <see cref="Serialize"/> emits the same tagged-wire frame the source
/// generator emits for <c>[StorageSerializable]</c> records: a ULEB128 length prefix followed
/// by a payload that opens with tag 0 + schema-format version 1 and then carries the wrapped
/// value under tag 1. Wire types are picked per <typeparamref name="T"/> using the same dispatch
/// table the generator uses, giving Domain one wire convention across the whole assembly.
/// Frames lacking the tag-0 schema-format header usually surface
/// <see cref="Exceptions.UnsupportedFormatVersionException"/> (or <see cref="System.IO.InvalidDataException"/> /
/// <see cref="System.IO.EndOfStreamException"/> when the length-prefix scope fails first) — typically a sign of a
/// corrupt file or a foreign byte stream rather than a wrapped value. The check is best-effort: a foreign
/// stream whose first bytes happen to read as a short length followed by tag 0 and version 1 is accepted,
/// so 1.x-persisted bytes must be identified out-of-band, not by this exception.
/// </para>
/// <para>
/// <b>Why implicit operators?</b> The implicit conversions between <typeparamref name="T"/> and
/// <see cref="SerializableWrapper{T}"/> allow transparent usage — callers can pass raw values
/// (e.g., <c>42</c>) where <c>SerializableWrapper&lt;int&gt;</c> is expected, eliminating
/// boilerplate <c>new SerializableWrapper&lt;int&gt;(42)</c> everywhere.
/// </para>
/// <para>
/// <b>Why a parameterless constructor?</b> Required by the <c>new()</c> constraint on
/// <c>ISerializable&lt;T&gt;</c>, which is needed for the static <see cref="Deserialize"/> method.
/// Deserialization creates an empty instance first, then populates it. Without a parameterless
/// constructor, the generic <c>new()</c> constraint would fail at compile time.
/// </para>
/// <para>
/// <b>Why type-checking if/else chains instead of generics?</b> The tagged-wire helpers have
/// kind-specific overloads (varint, zigzag, length-prefixed UTF-8, fixed-width float) that
/// cannot be called generically from <typeparamref name="T"/> alone. Runtime type dispatch
/// picks the correct helper. The generator-emitted code avoids this entirely because it knows
/// the declared type statically; this wrapper is the erased-T fallback.
/// </para>
/// <para>
/// <b>Why value-based equality (not reference)?</b> Two wrappers containing the same value should
/// be equal, regardless of which instance they are. This matches the semantics of the wrapped types
/// (two <c>int</c>s with value 42 are equal) and is critical for dictionary lookups and storage
/// engine key comparisons.
/// </para>
/// <para>
/// <b>One relation for order, equality and bytes.</b> Storage engines order and search keys by
/// <see cref="CompareTo"/> but filter point lookups by the serialized key bytes, so the three must
/// agree. Strings therefore compare by Unicode code point — the byte order of the UTF-8 the
/// wrapper writes — independent of the current culture, and <c>float</c>/<c>double</c> payloads
/// are written canonically (<c>-0</c> as <c>+0</c>, every NaN as the default NaN), the
/// representatives <see cref="Equals(SerializableWrapper{T})"/> already identifies them with.
/// A round trip of <c>-0.0</c> therefore reads back as <c>+0.0</c>, and a NaN payload is not
/// preserved.
/// </para>
/// <para>
/// <b>Mutation hazard.</b> <see cref="Value"/> has a public setter for deserialization support.
/// Mutating <see cref="Value"/> after placing an instance in a hash-based collection
/// (<c>Dictionary</c> key, <c>HashSet</c> member) causes undefined behavior because
/// <see cref="GetHashCode"/> is derived from <see cref="Value"/>. Treat instances as immutable
/// after construction.
/// </para>
/// </remarks>
public sealed class SerializableWrapper<T> : IStorageKey<SerializableWrapper<T>>
{
    // Current wrapper schema-format version. Incrementing this forces any 1.x-persisted bytes
    // (and earlier 2.x variants, if we ever add more) through the tag-0 check at read time.
    private const ulong SchemaFormatVersion = 1UL;

    // Tag number carrying the wrapped value. Tag 0 is reserved by the tagged-wire format for
    // the schema-format version marker (BSSG012 guards user code against writing tag 0 via
    // the normal WriteTag path; the wrapper emits tag 0 by writing the combined tag+wire-type
    // varint directly, matching the generator convention at TaggedWireEmitter.cs:54-57).
    private const int ValueTag = 1;

    // Tag number carrying the runtime-type discriminant. This closes the write/read dispatch
    // asymmetry: WriteValuePayload dispatches on the boxed runtime type (switch on Value),
    // while ReadValuePayload needs a symmetric signal. The wire type alone does not suffice
    // (int/long/bool/byte/char all use Varint). Tag 2 carries a compact numeric id for the
    // concrete CLR type so ReadValuePayload can match the same arm WriteValuePayload used —
    // even when T is an interface (e.g. IComparable wrapping an int).
    // TypeId values MUST remain stable; they are persisted in the wire format.
    private const int TypeIdTag = 2;

    // Compact type ids written under TypeIdTag. Must not be reused after being assigned.
    private const ulong TypeIdInt = 1UL;
    private const ulong TypeIdLong = 2UL;
    private const ulong TypeIdString = 3UL;
    private const ulong TypeIdFloat = 4UL;
    private const ulong TypeIdDouble = 5UL;
    private const ulong TypeIdBool = 6UL;
    private const ulong TypeIdByte = 7UL;
    private const ulong TypeIdChar = 8UL;

    /// <summary>
    /// Gets or sets the value of the wrapper.
    /// </summary>
    public T Value { get; set; } = default!;

    /// <summary>
    /// Initializes a new instance of the SerializableWrapper class.
    /// </summary>
    public SerializableWrapper() { }

    /// <summary>
    /// Initializes a new instance of the SerializableWrapper class with the specified value.
    /// </summary>
    /// <param name="value">The value to wrap.</param>
    public SerializableWrapper(T value)
    {
        Value = value;
    }

    /// <summary>
    /// Serializes the wrapped value to a stream using the 2.0.0 tagged-wire frame.
    /// </summary>
    /// <param name="stream">The stream to serialize to.</param>
    public void Serialize(Stream stream)
    {
        // PooledMemoryStream rents from ArrayPool<byte>.Shared; WriteTo flushes the rented
        // buffer directly into the destination without an intermediate ToArray() allocation.
        using var payload = new PooledMemoryStream();

        // Tag 0 schema-format version — combined (tag << 3 | wire) varint written directly
        // because VarintHelpers.WriteTag rejects tag 0 (that rejection is the user-facing
        // BSSG012 guard).
        VarintHelpers.WriteVarUInt64(payload, 0UL);
        VarintHelpers.WriteVarUInt64(payload, SchemaFormatVersion);

        WriteValuePayload(payload);

        VarintHelpers.WriteVarUInt64(stream, (ulong)payload.Length);
        payload.WriteTo(stream);
    }

    /// <summary>
    /// Deserializes the wrapped value from a stream. The stream must carry the 2.0.0
    /// tagged-wire frame written by <see cref="Serialize"/>.
    /// </summary>
    /// <param name="stream">The stream to deserialize from.</param>
    /// <returns>The deserialized SerializableWrapper object.</returns>
    public static SerializableWrapper<T> Deserialize(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var declaredLength = VarintHelpers.ReadVarUInt64(stream);

        // The declared length is corruption-controlled data. A length above int.MaxValue cannot
        // back a CLR array — surface the typed corruption exception instead of the raw
        // OverflowException a `checked((int)...)` would throw (a caller catching the documented
        // InvalidDataException corruption contract would otherwise miss it). Mirrors the ReadString
        // sibling guard in VarintHelpers.
        if (declaredLength > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Declared payload length {declaredLength} exceeds int.MaxValue — corrupt or foreign length prefix.");
        }

        var payloadLength = (int)declaredLength;

        // Seekable streams: bound the declared length by the bytes actually remaining before
        // allocating, or a corrupt prefix declaring int.MaxValue rents ~2 GB before the read
        // observes the short stream.
        if (stream.CanSeek && payloadLength > stream.Length - stream.Position)
        {
            throw new InvalidDataException(
                $"Declared payload length {payloadLength} exceeds the {stream.Length - stream.Position} bytes remaining in the stream — corrupt length prefix.");
        }

        // Read the payload into a bounded, geometrically-growing buffer (via the shared
        // VarintHelpers primitive) so a forged length on a NON-seekable stream — where the
        // seekable bound above cannot fire — never pre-allocates the full declared size. A stream
        // that ends before the declared length is corruption and surfaces InvalidDataException.
        var payloadBuf = VarintHelpers.ReadLengthPrefixedPayloadBounded(stream, payloadLength);
        using var payload = new MemoryStream(payloadBuf, writable: false);

        if (!VarintHelpers.TryReadTag(payload, out var tag, out var wireType)
            || tag != 0 || wireType != WireType.Varint)
        {
            throw new UnsupportedFormatVersionException(
                0u,
                "SerializableWrapper<T> 2.0.0 cannot read data missing the tag-0 schema-format version byte. Re-ingest from source or follow docs/migration-1x-to-2x.md.");
        }

        var version = VarintHelpers.ReadVarUInt64(payload);
        if (version != SchemaFormatVersion)
        {
            throw new UnsupportedFormatVersionException(
                (uint)version,
                $"SerializableWrapper<T> 2.0.0 cannot read schema-format version {version}; supported: {SchemaFormatVersion}.");
        }

        var wrapper = new SerializableWrapper<T>();
        var valueSeen = false;
        ulong typeId = 0UL; // 0 = not yet seen

        while (VarintHelpers.TryReadTag(payload, out tag, out wireType))
        {
            if (tag == TypeIdTag)
            {
                EnsureWire(wireType, WireType.Varint);
                typeId = VarintHelpers.ReadVarUInt64(payload);
            }
            else if (tag == ValueTag)
            {
                wrapper.Value = ReadValuePayload(payload, wireType, typeId);
                valueSeen = true;
            }
            else
            {
                // Unknown tag — forward compatibility: a future writer could carry more fields.
                VarintHelpers.SkipField(payload, wireType);
            }
        }

        if (!valueSeen)
        {
            throw new InvalidDataException(
                $"SerializableWrapper<{typeof(T).Name}> payload missing tag {ValueTag}.");
        }

        return wrapper;
    }

    // Runtime-typed write dispatch. Each arm picks the tagged-wire encoding that the
    // generator's TaggedWireEmitter.WireTypeFor dispatch would pick for the same CLR kind.
    // Tag 2 (TypeIdTag, Varint) is written BEFORE tag 1 (the value) so the read path can
    // dispatch on the concrete CLR type even when T is an interface. This closes the
    // asymmetry: WriteValuePayload switches on Value's runtime type; ReadValuePayload now
    // has the same runtime-type signal in the wire stream rather than relying on typeof(T).
    private void WriteValuePayload(Stream payload)
    {
        switch (Value)
        {
            case int intValue:
                VarintHelpers.WriteTag(payload, TypeIdTag, WireType.Varint);
                VarintHelpers.WriteVarUInt64(payload, TypeIdInt);
                VarintHelpers.WriteTag(payload, ValueTag, WireType.Varint);
                VarintHelpers.WriteZigZagInt64(payload, intValue);
                break;

            case long longValue:
                VarintHelpers.WriteTag(payload, TypeIdTag, WireType.Varint);
                VarintHelpers.WriteVarUInt64(payload, TypeIdLong);
                VarintHelpers.WriteTag(payload, ValueTag, WireType.Varint);
                VarintHelpers.WriteZigZagInt64(payload, longValue);
                break;

            case string stringValue:
                VarintHelpers.WriteTag(payload, TypeIdTag, WireType.Varint);
                VarintHelpers.WriteVarUInt64(payload, TypeIdString);
                VarintHelpers.WriteTag(payload, ValueTag, WireType.LengthPrefixed);
                VarintHelpers.WriteString(payload, stringValue);
                break;

            case float floatValue:
                VarintHelpers.WriteTag(payload, TypeIdTag, WireType.Varint);
                VarintHelpers.WriteVarUInt64(payload, TypeIdFloat);
                VarintHelpers.WriteTag(payload, ValueTag, WireType.Fixed32);
                Span<byte> fbuf = stackalloc byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(fbuf, CanonicalSingle(floatValue));
                payload.Write(fbuf);
                break;

            case double doubleValue:
                VarintHelpers.WriteTag(payload, TypeIdTag, WireType.Varint);
                VarintHelpers.WriteVarUInt64(payload, TypeIdDouble);
                VarintHelpers.WriteTag(payload, ValueTag, WireType.Fixed64);
                Span<byte> dbuf = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleLittleEndian(dbuf, CanonicalDouble(doubleValue));
                payload.Write(dbuf);
                break;

            case bool boolValue:
                VarintHelpers.WriteTag(payload, TypeIdTag, WireType.Varint);
                VarintHelpers.WriteVarUInt64(payload, TypeIdBool);
                VarintHelpers.WriteTag(payload, ValueTag, WireType.Varint);
                VarintHelpers.WriteVarUInt64(payload, boolValue ? 1UL : 0UL);
                break;

            case byte byteValue:
                VarintHelpers.WriteTag(payload, TypeIdTag, WireType.Varint);
                VarintHelpers.WriteVarUInt64(payload, TypeIdByte);
                VarintHelpers.WriteTag(payload, ValueTag, WireType.Varint);
                VarintHelpers.WriteVarUInt64(payload, byteValue);
                break;

            case char charValue:
                VarintHelpers.WriteTag(payload, TypeIdTag, WireType.Varint);
                VarintHelpers.WriteVarUInt64(payload, TypeIdChar);
                VarintHelpers.WriteTag(payload, ValueTag, WireType.Varint);
                VarintHelpers.WriteVarUInt64(payload, (ushort)charValue);
                break;

            default:
                throw new NotSupportedException($"Type {typeof(T)} is not supported for serialization.");
        }
    }

    // CompareTo/Equals/GetHashCode treat -0 and +0 as one value and every NaN payload as one
    // value, while the storage engines filter point lookups by serialized key bytes (the SSTable
    // bloom filter). Writing the raw IEEE bits would give equal keys different bytes — a bloom
    // false negative after flush — so the payload is canonicalised to the representative the
    // equality relation already identifies the value with (mirrors OrderedByteHelpers'
    // -0 → +0 normalisation for [Key] floats).
    private static float CanonicalSingle(float value) =>
        float.IsNaN(value) ? float.NaN : value == 0f ? 0f : value;

    private static double CanonicalDouble(double value) =>
        double.IsNaN(value) ? double.NaN : value == 0d ? 0d : value;

    // Runtime-typed read dispatch. When typeId > 0 (written by the current schema-format),
    // dispatch on the wire-encoded CLR type id — matching WriteValuePayload's runtime switch
    // on Value. This makes write and read symmetric: both paths decide by the concrete runtime
    // type, whether T is a concrete type (int, string) or an interface (IComparable, object).
    // When typeId == 0 (absent — forward-compat unknown tag skipped), fall back to typeof(T)
    // for the 8 known concrete types so callers with a concrete T still get a readable result.
    private static T ReadValuePayload(MemoryStream payload, WireType wireType, ulong typeId)
    {
        // Prefer the wire-encoded runtime type id. Falls back to typeof(T) when typeId is
        // absent so the method remains usable from hand-crafted test frames that pre-date
        // the TypeIdTag addition (e.g., Deserialize_UnknownTagSkipsForwardCompatibility).
        var effectiveTypeId = typeId != 0UL ? typeId : StaticTypeId();

        // The writer emits only in-range values for each type id (see WriteValuePayload), so a
        // decoded value outside the target type's domain is corrupt input and is rejected rather
        // than narrowed to a different value.
        switch (effectiveTypeId)
        {
            case TypeIdInt:
                {
                    EnsureWire(wireType, WireType.Varint);
                    var raw = VarintHelpers.ReadZigZagInt64(payload);
                    EnsureInRange(raw is >= int.MinValue and <= int.MaxValue, raw, "int");
                    return AsT((int)raw, effectiveTypeId);
                }

            case TypeIdLong:
                EnsureWire(wireType, WireType.Varint);
                return AsT(VarintHelpers.ReadZigZagInt64(payload), effectiveTypeId);

            case TypeIdString:
                EnsureWire(wireType, WireType.LengthPrefixed);
                return AsT(VarintHelpers.ReadString(payload), effectiveTypeId);

            case TypeIdFloat:
                EnsureWire(wireType, WireType.Fixed32);
                Span<byte> fbuf = stackalloc byte[4];
                VarintHelpers.ReadExact(payload, fbuf);
                return AsT(BinaryPrimitives.ReadSingleLittleEndian(fbuf), effectiveTypeId);

            case TypeIdDouble:
                EnsureWire(wireType, WireType.Fixed64);
                Span<byte> dbuf = stackalloc byte[8];
                VarintHelpers.ReadExact(payload, dbuf);
                return AsT(BinaryPrimitives.ReadDoubleLittleEndian(dbuf), effectiveTypeId);

            case TypeIdBool:
                {
                    EnsureWire(wireType, WireType.Varint);
                    var raw = VarintHelpers.ReadVarUInt64(payload);
                    EnsureInRange(raw <= 1UL, raw, "bool");
                    return AsT(raw == 1UL, effectiveTypeId);
                }

            case TypeIdByte:
                {
                    EnsureWire(wireType, WireType.Varint);
                    var raw = VarintHelpers.ReadVarUInt64(payload);
                    EnsureInRange(raw <= byte.MaxValue, raw, "byte");
                    return AsT((byte)raw, effectiveTypeId);
                }

            case TypeIdChar:
                {
                    EnsureWire(wireType, WireType.Varint);
                    var raw = VarintHelpers.ReadVarUInt64(payload);
                    EnsureInRange(raw <= char.MaxValue, raw, "char");
                    return AsT((char)raw, effectiveTypeId);
                }

            default:
                // At schema-format version 1 the writer emits only ids 1..8 and always writes the
                // type-id tag, so an unknown id — or an absent one that T cannot supply — is a
                // corrupt or foreign frame: the same typed contract as the sibling arms.
                throw new InvalidDataException(
                    $"SerializableWrapper<{typeof(T).Name}> payload carries type id {effectiveTypeId}, which no schema-format version {SchemaFormatVersion} writer emits — corrupt or foreign payload.");
        }
    }

    // Maps typeof(T) → TypeId constant for the fallback (typeId == 0) code path.
    // Returns 0 when T is an interface or unknown concrete type.
    private static ulong StaticTypeId()
    {
        if (typeof(T) == typeof(int)) { return TypeIdInt; }
        if (typeof(T) == typeof(long)) { return TypeIdLong; }
        if (typeof(T) == typeof(string)) { return TypeIdString; }
        if (typeof(T) == typeof(float)) { return TypeIdFloat; }
        if (typeof(T) == typeof(double)) { return TypeIdDouble; }
        if (typeof(T) == typeof(bool)) { return TypeIdBool; }
        if (typeof(T) == typeof(byte)) { return TypeIdByte; }
        if (typeof(T) == typeof(char)) { return TypeIdChar; }
        return 0UL; // interface or unsupported concrete type — callers must rely on typeId
    }

    private static void EnsureInRange<TRaw>(bool inRange, TRaw raw, string targetType)
    {
        if (!inRange)
        {
            throw new InvalidDataException(
                $"SerializableWrapper<{typeof(T).Name}> decoded value {raw} is outside the {targetType} range — corrupt or foreign payload.");
        }
    }

    // The persisted type id names the CLR type the writer held. When it is not a T (for example a
    // string frame read as SerializableWrapper<int>), the frame is corrupt or foreign for this
    // reader; surface the typed corruption contract instead of a raw InvalidCastException.
    private static T AsT(object value, ulong typeId)
    {
        if (value is T typed)
        {
            return typed;
        }

        throw new InvalidDataException(
            $"SerializableWrapper<{typeof(T).Name}> payload carries type id {typeId} ({value.GetType().Name}), which is not assignable to {typeof(T).Name} — corrupt or foreign payload.");
    }

    private static void EnsureWire(WireType actual, WireType expected)
    {
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"SerializableWrapper<{typeof(T).Name}> expected wire type {expected} for tag {ValueTag}, observed {actual}.");
        }
    }

    /// <summary>
    /// Compares the current object with another object of the same type.
    /// </summary>
    /// <param name="other">An object to compare with this object.</param>
    /// <returns>A value that indicates the relative order of the objects being compared.</returns>
    public int CompareTo(SerializableWrapper<T>? other)
    {
        if (other is null)
        {
            return 1;
        }

        // Strings compare by Unicode code point (== the UTF-8 byte order Serialize writes), never
        // through Comparer<T>.Default: for strings that comparer is culture-sensitive, so it
        // reports ordinally distinct keys (U+00C5 vs "A" + U+030A) as equal while Equals and the
        // serialized bytes keep them apart, and it makes persisted SSTable order depend on the
        // writing process's culture. Matched on the runtime type so an interface T is covered too.
        if (Value is string left && other.Value is string right)
        {
            return OrderedByteHelpers.CompareStringByteLex(left, right);
        }

        return Comparer<T>.Default.Compare(Value, other.Value);
    }

    /// <summary>
    /// Determines whether the specified wrapper is equal to the current wrapper.
    /// </summary>
    /// <param name="other">The wrapper to compare with the current wrapper.</param>
    /// <returns>true if the wrapped values are equal; otherwise, false.</returns>
    public bool Equals(SerializableWrapper<T>? other)
    {
        if (other is null)
        {
            return false;
        }

        return EqualityComparer<T>.Default.Equals(Value, other.Value);
    }

    /// <summary>
    /// Determines whether the specified object is equal to the current object.
    /// </summary>
    /// <param name="obj">The object to compare with the current object.</param>
    /// <returns>true if the specified object is equal to the current object; otherwise, false.</returns>
    public override bool Equals(object? obj) => obj is SerializableWrapper<T> other && Equals(other);

    /// <summary>
    /// Serves as the default hash function.
    /// </summary>
    /// <returns>A hash code for the current object.</returns>
    public override int GetHashCode()
    {
        return EqualityComparer<T>.Default.GetHashCode(Value!);
    }

    /// <summary>
    /// Returns a string that represents the current object.
    /// </summary>
    /// <returns>A string that represents the current object.</returns>
    public override string ToString()
    {
        return Value?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Implicit conversion from T to SerializableWrapper&lt;T&gt;.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    public static implicit operator SerializableWrapper<T>(T value) => new(value);

    /// <summary>
    /// Implicit conversion from SerializableWrapper&lt;T&gt; to T.
    /// </summary>
    /// <param name="wrapper">The wrapper to convert.</param>
    public static implicit operator T(SerializableWrapper<T> wrapper) => wrapper.Value;
}
