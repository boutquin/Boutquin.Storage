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
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Boutquin.Storage.Domain.Helpers;
using Boutquin.Storage.Domain.Interfaces.Schemas;

namespace Boutquin.Storage.Infrastructure.Schemas;

/// <summary>
/// JSON-based versioned serializer that prepends a 4-byte little-endian schema version header
/// and a 4-byte little-endian payload length before the JSON payload.
///
/// <para><b>Wire format:</b> [4 bytes: version (LE int32)] [4 bytes: payload byte count (LE int32)]
/// [N bytes: UTF-8 JSON payload]</para>
///
/// <para><b>Why length framing?</b> The DDIA use case for a versioned serializer is version-tagged
/// records, plural, in a database. <c>JsonSerializer.DeserializeAsync</c> reads to end-of-stream,
/// so without per-record framing two concatenated records make even the first unreadable. The
/// length prefix bounds each record exactly, matching the concatenation-safe framing of the
/// sibling <see cref="TaggedWireVersionedSerializer{T}"/>.</para>
///
/// <para><b>Unknown field preservation:</b> deserializing to <typeparamref name="T"/> silently drops
/// JSON properties not mapped to <typeparamref name="T"/>'s members. A round-trip through
/// serialize → deserialize → serialize will lose unknown fields. Per DDIA Ch. 5 (W-8),
/// this can cause data loss when older code reads a record with new fields, modifies it,
/// and writes it back. To preserve unknown fields, annotate <typeparamref name="T"/> and every object
/// type reachable from it with <c>[JsonExtensionData]</c> on a <c>Dictionary&lt;string, JsonElement&gt;</c>
/// property: System.Text.Json keeps an unknown field only on the object that declares one.</para>
///
/// <para><b>Write-time identity:</b> <see cref="SerializeAsync"/> writes a body only when it reads back as
/// the value written. A value JSON cannot carry exactly (text with an unpaired surrogate, which would
/// persist as U+FFFD) or that a write-side option transform changes is refused with
/// <see cref="ArgumentException"/> before any byte reaches the stream.</para>
///
/// <para><b>Avro framing:</b> implements <see cref="IAvroStyleVersionedSerializer{T}"/>. The
/// <see cref="DeserializeAsync"/> overload accepts a registry and subject so callers can do
/// writer-schema-directed resolution; the JSON implementation ignores them because JSON is
/// self-describing and carries the writer version in the 4-byte header already.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026),
/// Ch. 5 — "Encoding and Evolution": version-tagged serialization.</para>
/// </summary>
/// <typeparam name="T">The type to serialize.</typeparam>
public sealed class JsonVersionedSerializer<T> : IAvroStyleVersionedSerializer<T>
{
    private readonly JsonSerializerOptions? _options;

    /// <summary>
    /// Initializes a new instance of <see cref="JsonVersionedSerializer{T}"/>.
    /// </summary>
    /// <param name="options">Optional JSON serializer options.</param>
    /// <param name="requireExtensionData">
    /// When <see langword="true"/> (the default — safe by default), the constructor walks the
    /// serializer's resolved contract for <typeparamref name="T"/> and every object type reachable from it
    /// (members, collection elements, dictionary values) and throws <see cref="InvalidOperationException"/>
    /// unless each has an instance <see cref="JsonExtensionDataAttribute"/> member the serializer populates
    /// (a static or ignored member does not count). <c>System.Text.Json</c> silently drops JSON properties not present on
    /// <typeparamref name="T"/>, so a read-modify-write through an old reader loses fields a new
    /// writer added — exactly the DDIA Ch. 5 forward-compatibility data-loss hazard. Requiring a
    /// <c>[JsonExtensionData]</c> member at construction makes that loss impossible by default;
    /// pass <see langword="false"/> to opt into lossy behaviour for a type that genuinely needs no
    /// unknown-field preservation.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="requireExtensionData"/> is <see langword="true"/> and
    /// <typeparamref name="T"/>, or an object type reachable from it, has no populated
    /// <c>[JsonExtensionData]</c> member.
    /// </exception>
    public JsonVersionedSerializer(JsonSerializerOptions? options = null, bool requireExtensionData = true)
    {
        _options = options;

        if (requireExtensionData && FindObjectTypeWithoutExtensionData(options) is { } unprotected)
        {
            throw new InvalidOperationException(
                $"Type '{unprotected.FullName}', reachable from '{typeof(T).FullName}', has no [JsonExtensionData] " +
                "member the serializer populates (an instance member that is not ignored). Add a " +
                "Dictionary<string, JsonElement> property with [JsonExtensionData] to every object type to preserve " +
                "unknown fields across round-trips, or set requireExtensionData: false to suppress this check.");
        }
    }

    /// <inheritdoc />
    public async ValueTask SerializeAsync(Stream stream, T value, SchemaVersion schemaVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        // A null value serializes to the JSON literal "null" — a record the read path can only
        // fail on ("Deserialization returned null."). Reject the write-succeeds/read-fails
        // round trip at the write boundary.
        ArgumentNullException.ThrowIfNull(value);

        // default(SchemaVersion) carries Value 0, a header DeserializeAsync rejects as corruption.
        // Refuse it before any byte reaches the stream, as ProtoSerializer does.
        if (schemaVersion.Value < 1)
        {
            throw new ArgumentException(
                $"A positive registered schema version is required; got {schemaVersion.Value}.", nameof(schemaVersion));
        }

        // Serialize the payload to a buffer first — the length prefix must precede the payload
        // bytes, and the target stream may be non-seekable (no backpatching).
        using var payload = new MemoryStream();
        await JsonSerializer.SerializeAsync(payload, value, _options, ct).ConfigureAwait(false);

        // Write 4-byte LE version header + 4-byte LE payload length.
        // AC-8.3 (F-15): guard against payload.Length > int.MaxValue before the checked cast.
        // A payload that large is operationally impossible (MemoryStream allocation would fail
        // first) but the cast is a silent overflow hazard if ever reached.
        if (payload.Length > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"JSON serialized payload is {payload.Length} bytes, which exceeds the 4-byte framed length field limit ({int.MaxValue}).");
        }

        // The body has the same hazard: a value whose JSON this serializer cannot decode (for
        // example one embedding default(SchemaVersion)) would be written and then fail every read.
        // Run DeserializeAsync's decode on the exact bytes before anything reaches the stream.
        EnsureReadable(value, payload.GetBuffer().AsSpan(0, (int)payload.Length), nameof(value));

        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), schemaVersion.Value);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), (int)payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload.GetBuffer().AsMemory(0, (int)payload.Length), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<(SchemaVersion Version, T Value)> DeserializeAsync(
        Stream stream,
        ISchemaRegistry registry,
        string subject,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        // registry and subject accepted per the Avro-style interface contract; JSON is self-describing
        // so writer-schema lookup is not needed for this implementation.

        // Read 4-byte LE version header + 4-byte LE payload length. A non-positive version or
        // length is corruption (or a foreign stream) — surface it as the domain's fail-closed
        // corruption exception, not as an ArgumentOutOfRangeException escaping from the
        // SchemaVersion constructor.
        // AC-8.3 (F-15): a truncated header (< 8 bytes) causes ReadExactlyAsync to throw
        // EndOfStreamException. Wrap it as UnsupportedFormatVersionException so the low-level
        // I/O exception does not escape the serializer boundary.
        var header = new byte[8];
        try
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new UnsupportedFormatVersionException(
                0,
                "JSON versioned record has a truncated header (fewer than 8 bytes) — corrupt or incomplete stream.",
                ex);
        }
        var rawVersion = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        if (rawVersion < 1)
        {
            throw new UnsupportedFormatVersionException(
                unchecked((uint)rawVersion),
                $"JSON versioned record carries invalid schema version {rawVersion} (must be ≥ 1) — corrupt header or foreign stream.");
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (payloadLength < 0)
        {
            throw new UnsupportedFormatVersionException(
                (uint)rawVersion,
                $"JSON versioned record carries negative payload length {payloadLength} — corrupt header or foreign stream.");
        }

        var version = new SchemaVersion(rawVersion);

        // Read exactly the framed payload — never past it, so concatenated records on one
        // stream each deserialize from their own bounded window. The declared length is
        // untrusted: a corrupt header claiming ~2 GiB must NOT drive an up-front
        // `new byte[payloadLength]` allocation (class-C "declared length trusted before
        // allocation"). The seekable remaining-bytes pre-check alone is insufficient because
        // it is gated behind CanSeek — a forged length on a non-seekable stream would bypass
        // it and OOM. Delegate to the reference-correct bounded reader, which grows allocation
        // against the bytes the stream actually supplies regardless of seekability and throws
        // InvalidDataException when the stream ends before the declared length (the
        // length prefix over-declared). A truncated body is the same corruption class as a
        // bad header: surface the domain's fail-closed type.
        byte[] payload;
        try
        {
            payload = VarintHelpers.ReadLengthPrefixedPayloadBounded(stream, payloadLength);
        }
        catch (InvalidDataException ide)
        {
            throw new UnsupportedFormatVersionException(
                (uint)rawVersion,
                $"JSON versioned record declares payload length {payloadLength} but the stream " +
                "supplied fewer bytes — truncated, corrupt, or forged length prefix.", ide);
        }

        // Body corruption is the same fail-closed class as header corruption and truncation:
        // a declared-length payload full of garbage bytes must not leak a raw JsonException
        // across the serializer boundary (the caller's corruption handling would otherwise be
        // format-specific). That includes well-formed JSON that T itself rejects: System.Text.Json
        // does not wrap an exception thrown by a validating constructor or init accessor (for
        // example SchemaVersion's), and SerializeAsync's EnsureReadable refuses to write any body
        // that fails this decode — so on read such a body can only be corrupt or foreign. The
        // payload is already in memory, so no stream fault can surface here.
        T? deserialized;
        try
        {
            deserialized = JsonSerializer.Deserialize<T>(payload, _options);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            throw new UnsupportedFormatVersionException(
                (uint)rawVersion,
                $"JSON versioned record (schema version {version.Value}) has a malformed body — corrupt payload bytes.",
                ex);
        }

        // SerializeAsync rejects null values, so a well-framed body holding the JSON literal null
        // can only come from corruption or a foreign stream — the same fail-closed class as a
        // malformed body.
        var value = deserialized
            ?? throw new UnsupportedFormatVersionException(
                (uint)rawVersion,
                $"JSON versioned record (schema version {version.Value}) has a null body — corrupt or foreign stream.");

        return (version, value);
    }

    private void EnsureReadable(T original, ReadOnlySpan<byte> payload, string paramName)
    {
        T? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<T>(payload, _options);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ArgumentException(
                $"Value of type {typeof(T)} cannot be read back from its serialized form ({ex.GetType().Name}: {ex.Message}).",
                paramName,
                ex);
        }

        if (decoded is null)
        {
            throw new ArgumentException(
                $"Value of type {typeof(T)} serializes to a form that reads back as null.", paramName);
        }

        // Decoding is not enough: a lone surrogate decodes as U+FFFD, a write-side option transform as
        // a different value. Only a body that reads back as the value written may reach the stream.
        var difference = JsonRoundTripIdentity.FindDifference(original, decoded, _options);
        if (difference is not null)
        {
            throw new ArgumentException(
                $"Value of type {typeof(T)} does not read back as the value written ({difference}).", paramName);
        }
    }

    // Decided from the serializer's own resolved contract, not from attribute presence: a static or
    // [JsonIgnore] member carries the attribute but is never populated. Every object type reachable from
    // T needs its own extension member, because System.Text.Json keeps an unknown field only on the object
    // that declares one; a nested type without it erases a newer writer's nested field on rewrite.
    private static Type? FindObjectTypeWithoutExtensionData(JsonSerializerOptions? options)
    {
        var resolver = options is null ? JsonSerializerOptions.Default : new JsonSerializerOptions(options);
        var visited = new HashSet<Type>();
        var pending = new Queue<Type>();
        pending.Enqueue(typeof(T));
        while (pending.TryDequeue(out var type))
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(object) || !visited.Add(type))
            {
                continue;
            }

            var info = resolver.GetTypeInfo(type);
            switch (info.Kind)
            {
                case JsonTypeInfoKind.Object:
                    // An ignored member stays in the contract as extension data but with neither
                    // accessor, so nothing ever reaches it.
                    if (!info.Properties.Any(property => property.IsExtensionData && (property.Set is not null || property.Get is not null)))
                    {
                        return type;
                    }

                    foreach (var property in info.Properties.Where(property => !property.IsExtensionData))
                    {
                        pending.Enqueue(property.PropertyType);
                    }

                    // A derived contract can introduce nested objects absent from the base's members.
                    // Those objects need the same protection when the discriminator selects them.
                    if (info.PolymorphismOptions is { } polymorphism)
                    {
                        foreach (var derived in polymorphism.DerivedTypes)
                        {
                            pending.Enqueue(derived.DerivedType);
                        }
                    }

                    break;
                case JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary when info.ElementType is not null:
                    pending.Enqueue(info.ElementType);
                    break;
                default:
                    break;
            }
        }

        return null;
    }
}

/// <summary>
/// The write-time identity check shared by every JSON-persisting path: a value may be acknowledged only
/// when the value its persisted bytes decode to is the value that was written.
/// </summary>
/// <remarks>
/// Decoding without an exception is not enough, and neither is a reload that re-serializes to the same
/// bytes: System.Text.Json writes an unpaired UTF-16 surrogate as U+FFFD, and write-side option transforms
/// (a dictionary key policy, an ignore condition, ignored read-only members) change content on write while
/// the reload stays a fixed point. The comparison is therefore made between the ORIGINAL and the decoded
/// value, walking the serializer's own contract with every write-side transform switched off: object
/// members (including extension data), dictionary entries by key, sequence elements in order (or as a
/// multiset for sets, whose order is not content), and leaves by value. A member declared as an
/// abstraction may reload as another implementation of it, but a structured value that reloads as a bare
/// JSON document (including a scalar JsonElement), or as a different object type, has lost its type.
/// Text must be well-formed UTF-16.
/// </remarks>
internal static class JsonRoundTripIdentity
{
    private const int MaxDepth = 64;
    private const int MaxRendered = 120;

    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> s_canonical = new();
    private static readonly JsonSerializerOptions s_defaultCanonical = CreateCanonical(JsonSerializerOptions.Default);

    /// <summary>Returns <see langword="null"/> when <paramref name="decoded"/> is <paramref name="original"/>;
    /// otherwise a description of the first difference.</summary>
    internal static string? FindDifference(object? original, object? decoded, JsonSerializerOptions? options)
    {
        var canonical = options is null ? s_defaultCanonical : s_canonical.GetValue(options, CreateCanonical);
        return Compare(original, decoded, canonical, "$", 0);
    }

    /// <summary>Whether <paramref name="text"/> is well-formed UTF-16 (no unpaired surrogate), the only
    /// text JSON carries exactly.</summary>
    internal static bool IsWellFormedUtf16(ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }

                return false;
            }

            if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Throws <see cref="ArgumentException"/> when <paramref name="text"/> is not well-formed UTF-16.</summary>
    internal static void ThrowIfIllFormedText(string text, string paramName)
    {
        if (!IsWellFormedUtf16(text))
        {
            throw new ArgumentException(
                "The text is not well-formed UTF-16 (it holds an unpaired surrogate), which JSON cannot carry: it "
                + "would persist as U+FFFD and read back as different text.",
                paramName);
        }
    }

    private static JsonSerializerOptions CreateCanonical(JsonSerializerOptions options)
    {
        var canonical = new JsonSerializerOptions(options)
        {
            IgnoreReadOnlyProperties = false,
            IgnoreReadOnlyFields = false,
        };
#pragma warning disable SYSLIB0020 // IgnoreNullValues excludes DefaultIgnoreCondition; clear it before setting the latter.
        canonical.IgnoreNullValues = false;
#pragma warning restore SYSLIB0020
        canonical.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
        canonical.Converters.Insert(0, new StrictStringConverter());
        canonical.Converters.Insert(1, new StrictCharConverter());
        canonical.MakeReadOnly(populateMissingResolver: true);
        return canonical;
    }

    private static string? Compare(object? original, object? decoded, JsonSerializerOptions options, string path, int depth)
    {
        if (original is null || decoded is null)
        {
            return original is null && decoded is null
                ? null
                : $"{path}: wrote {Describe(original)} but it reads back as {Describe(decoded)}";
        }

        var writtenType = original.GetType();
        var readType = decoded.GetType();
        if (depth > MaxDepth)
        {
            return CompareSerialized(writtenType, original, readType, decoded, options, path);
        }

        var written = options.GetTypeInfo(writtenType);
        if (writtenType != readType)
        {
            if (decoded is JsonElement && original is not JsonElement)
            {
                return $"{path}: a value of type '{writtenType.FullName}' reads back as a bare JSON document, losing its type or state";
            }

            var read = options.GetTypeInfo(readType);
            if (written.Kind != read.Kind || written.Kind is not (JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary))
            {
                return $"{path}: a value of type '{writtenType.FullName}' reads back as '{readType.FullName}', losing its type or state";
            }
        }

        switch (written.Kind)
        {
            case JsonTypeInfoKind.Object:
                foreach (var property in written.Properties)
                {
                    if (property.Get is not { } get)
                    {
                        continue;
                    }

                    var difference = Compare(get(original), get(decoded), options, path + "." + property.Name, depth + 1);
                    if (difference is not null)
                    {
                        return difference;
                    }
                }

                return null;
            case JsonTypeInfoKind.Dictionary:
                return original is IDictionary before && decoded is IDictionary after
                    ? CompareDictionary(before, after, options, path, depth)
                    : CompareSerialized(writtenType, original, readType, decoded, options, path);
            case JsonTypeInfoKind.Enumerable:
                return CompareSequence((IEnumerable)original, (IEnumerable)decoded,
                    IsSet(writtenType) || IsSet(readType), options, path, depth);
            default:
                return CompareLeaf(writtenType, original, readType, decoded, options, path);
        }
    }

    private static string? CompareDictionary(IDictionary before, IDictionary after, JsonSerializerOptions options, string path, int depth)
    {
        if (before.Count != after.Count)
        {
            return $"{path}: wrote {before.Count} entries but {after.Count} read back";
        }

        foreach (DictionaryEntry entry in before)
        {
            if (entry.Key is string key && !IsWellFormedUtf16(key))
            {
                return $"{path}: a key is not well-formed UTF-16 (an unpaired surrogate), which JSON cannot carry";
            }

            if (!after.Contains(entry.Key))
            {
                return $"{path}: key '{entry.Key}' does not read back under the same key";
            }

            var difference = Compare(entry.Value, after[entry.Key], options, $"{path}[{entry.Key}]", depth + 1);
            if (difference is not null)
            {
                return difference;
            }
        }

        return null;
    }

    private static string? CompareSequence(IEnumerable before, IEnumerable after, bool unordered,
        JsonSerializerOptions options, string path, int depth)
    {
        var left = before.Cast<object?>().ToList();
        var right = after.Cast<object?>().ToList();
        if (left.Count != right.Count)
        {
            return $"{path}: wrote {left.Count} elements but {right.Count} read back";
        }

        string? inOrder = null;
        for (var i = 0; i < left.Count && inOrder is null; i++)
        {
            inOrder = Compare(left[i], right[i], options, $"{path}[{i}]", depth + 1);
        }

        if (inOrder is null || !unordered)
        {
            return inOrder;
        }

        // A set's enumeration order is not content: pair every written element with an unused
        // decoded element it is identical to.
        var used = new bool[right.Count];
        for (var i = 0; i < left.Count; i++)
        {
            var match = -1;
            string? first = null;
            for (var j = 0; j < right.Count && match < 0; j++)
            {
                if (used[j])
                {
                    continue;
                }

                var difference = Compare(left[i], right[j], options, $"{path}[{i}]", depth + 1);
                if (difference is null)
                {
                    match = j;
                }
                else
                {
                    first ??= difference;
                }
            }

            if (match < 0)
            {
                return first ?? $"{path}[{i}]: element has no counterpart after reload";
            }

            used[match] = true;
        }

        return null;
    }

    private static string? CompareLeaf(Type writtenType, object original, Type readType, object decoded, JsonSerializerOptions options, string path)
    {
        if (writtenType == readType)
        {
            switch (original)
            {
                case string text:
                    return !IsWellFormedUtf16(text)
                        ? $"{path}: text is not well-formed UTF-16 (an unpaired surrogate), which JSON cannot carry; it would read back as U+FFFD"
                        : string.Equals(text, (string)decoded, StringComparison.Ordinal)
                            ? null
                            : $"{path}: wrote {Describe(original)} but it reads back as {Describe(decoded)}";
                case char single:
                    return char.IsSurrogate(single)
                        ? $"{path}: a lone surrogate character cannot be carried by JSON"
                        : single == (char)decoded ? null : $"{path}: wrote {Describe(original)} but it reads back as {Describe(decoded)}";
                case DateTimeOffset instant:
                    return instant.EqualsExact((DateTimeOffset)decoded) ? null : $"{path}: wrote {instant:O} but it reads back as {(DateTimeOffset)decoded:O}";
                case DateTime time:
                    var other = (DateTime)decoded;
                    return time.Ticks == other.Ticks && time.Kind == other.Kind ? null : $"{path}: wrote {time:O} ({time.Kind}) but it reads back as {other:O} ({other.Kind})";
                case bool or byte or sbyte or short or ushort or int or uint or long or ulong or Int128 or UInt128 or Guid or Enum:
                    return original.Equals(decoded) ? null : $"{path}: wrote {Describe(original)} but it reads back as {Describe(decoded)}";
                default:
                    break;
            }
        }

        return CompareSerialized(writtenType, original, readType, decoded, options, path);
    }

    // Leaves without an in-memory identity the walk can read (custom converters, floating point, decimal
    // scale, JSON documents) are compared through their canonical serialization; the strict text
    // converters make an unpaired surrogate anywhere in it a refusal rather than a silent U+FFFD.
    private static string? CompareSerialized(Type writtenType, object original, Type readType, object decoded, JsonSerializerOptions options, string path)
    {
        byte[] before;
        byte[] after;
        try
        {
            before = JsonSerializer.SerializeToUtf8Bytes(original, writtenType, options);
            after = JsonSerializer.SerializeToUtf8Bytes(decoded, readType, options);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return $"{path}: the value cannot be compared with its reload ({ex.GetType().Name}: {ex.Message})";
        }

        return before.AsSpan().SequenceEqual(after)
            ? null
            : $"{path}: wrote {Render(before)} but it reads back as {Render(after)}";
    }

    private static bool IsSet(Type type) =>
        type.GetInterfaces().Append(type).Any(candidate => candidate.IsGenericType
            && (candidate.GetGenericTypeDefinition() == typeof(ISet<>)
                || candidate.GetGenericTypeDefinition() == typeof(IReadOnlySet<>)
                || candidate.GetGenericTypeDefinition() == typeof(System.Collections.Immutable.IImmutableSet<>)));

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => "\"" + Escape(text) + "\"",
        _ => $"{value} ({value.GetType().Name})",
    };

    private static string Escape(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MaxRendered));
        foreach (var c in text.Length > MaxRendered ? text[..MaxRendered] : text)
        {
            builder.Append(char.IsSurrogate(c) || char.IsControl(c) ? $"\\u{(int)c:X4}" : c.ToString());
        }

        return builder.ToString();
    }

    private static string Render(byte[] json)
    {
        var text = Encoding.UTF8.GetString(json);
        return text.Length > MaxRendered ? text[..MaxRendered] + "…" : text;
    }

    private sealed class StrictStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            ThrowIfIllFormedText(value, nameof(value));
            writer.WriteStringValue(value);
        }

        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString()!;

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            ThrowIfIllFormedText(value, nameof(value));
            writer.WritePropertyName(value);
        }
    }

    private sealed class StrictCharConverter : JsonConverter<char>
    {
        public override char Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() is [var single] ? single : throw new JsonException("Expected a one-character string.");

        public override void Write(Utf8JsonWriter writer, char value, JsonSerializerOptions options)
        {
            if (char.IsSurrogate(value))
            {
                throw new ArgumentException("A lone surrogate character cannot be carried by JSON.", nameof(value));
            }

            writer.WriteStringValue(value.ToString());
        }
    }
}
