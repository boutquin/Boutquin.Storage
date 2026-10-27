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
using Boutquin.Storage.Domain.Helpers;
using Boutquin.Storage.Domain.Interfaces.Schemas;

namespace Boutquin.Storage.Infrastructure.Schemas;

/// <summary>
/// An <see cref="IAvroStyleVersionedSerializer{T}"/> that frames a source-generator tagged-wire record
/// behind a registry <see cref="SchemaVersion"/>. The wire layout is
/// <c>[ULEB128 schema-version][tagged-wire-payload]</c>; the version prefix mandated by the interface
/// subsumes any separate "registry-aware" envelope. Delegates the body to the generated
/// <see cref="ISerializable{T}"/> implementation (<c>Serialize</c> / static <c>Deserialize</c>).
///
/// <para><b>Unknown-field handling — a documented non-goal.</b> The generated reader SKIPS and
/// DISCARDS unknown tags: a read-modify-write through an old reader drops fields a newer writer
/// added. This is the tagged-wire format's documented deliberate deviation from round-trip
/// preservation (see <c>docs/wire-format-2.0.md</c> §4 — LSM values are rewritten from
/// authoritative in-memory state, not read-modify-written). Note the asymmetry with the JSON
/// sibling, whose <c>requireExtensionData</c> default enforces preservation: choose the JSON
/// serializer when consumers genuinely read-modify-write versioned records.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026),
/// Ch. 5 — "Encoding and Evolution": version-tagged records resolved against a schema registry.</para>
/// </summary>
/// <typeparam name="T">A source-generator <c>[StorageSerializable]</c> record (an <see cref="ISerializable{T}"/>).</typeparam>
public sealed class TaggedWireVersionedSerializer<T> : IAvroStyleVersionedSerializer<T>
    where T : ISerializable<T>
{
    /// <inheritdoc />
    /// <remarks>
    /// <para><b>Synchronous-write contract.</b> <c>value.Serialize(stream)</c> — the generated
    /// <see cref="ISerializable{T}.Serialize"/> implementation — writes synchronously to
    /// <paramref name="stream"/>. The method returns a completed <see cref="ValueTask"/> immediately
    /// after the write; the <c>Async</c> suffix reflects the interface contract (callers may
    /// specialise with buffered or network streams that do flush asynchronously at a higher layer),
    /// not the write itself. Callers must supply a stream whose <c>Write</c> method is safe to call
    /// on the current thread (e.g., a <see cref="System.IO.MemoryStream"/>,
    /// <see cref="System.IO.FileStream"/>, or a buffered wrapper); any stream that blocks or
    /// faults on synchronous writes will surface that fault here, not at a later async flush.</para>
    /// <para><b>No output on refusal.</b> The body is encoded into a private buffer before the
    /// version header is written, so a value the generated writer refuses (an
    /// <see cref="ArgumentException"/> such as ill-formed UTF-16) leaves <paramref name="stream"/>
    /// untouched. Only a fault of <paramref name="stream"/> itself can leave a partial frame.</para>
    /// </remarks>
    public ValueTask SerializeAsync(Stream stream, T value, SchemaVersion schemaVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        // For reference-type T a null value would NRE at value.Serialize — reject at the
        // boundary with the typed guard (no-op for the common record-struct T).
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();

        // default(SchemaVersion) carries Value 0, a header DeserializeAsync rejects as corruption.
        // Refuse it before any byte reaches the stream, as the JSON and Protobuf siblings do.
        if (schemaVersion.Value < 1)
        {
            throw new ArgumentException(
                $"A positive registered schema version is required; got {schemaVersion.Value}.", nameof(schemaVersion));
        }

        // [ULEB128 schema-version][tagged-wire-payload]. The body is encoded into a private buffer
        // first: the generated writer refuses some values (ill-formed UTF-16, for example), and a
        // refusal after the header reached the caller's stream would leave a stray version byte
        // that the next record's read takes as its own header. Only an encoded body is framed.
        using var body = new MemoryStream();
        value.Serialize(body);
        VarintHelpers.WriteVarUInt64(stream, (ulong)schemaVersion.Value);
        stream.Write(body.GetBuffer(), 0, checked((int)body.Length));

        return default;
    }

    /// <inheritdoc />
    /// <exception cref="UnsupportedFormatVersionException">
    /// The embedded schema version is not registered under <paramref name="subject"/>, or the
    /// version header or body is truncated, malformed, or decodes to a value <typeparamref name="T"/>
    /// rejects. A fault of the stream itself propagates unwrapped.
    /// </exception>
    public async ValueTask<(SchemaVersion Version, T Value)> DeserializeAsync(
        Stream stream,
        ISchemaRegistry registry,
        string subject,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ct.ThrowIfCancellationRequested();

        // A version of 0 or one beyond int.MaxValue is corruption (or a foreign stream) —
        // surface it as the domain's fail-closed corruption exception, not as an
        // ArgumentOutOfRangeException escaping the SchemaVersion constructor or an
        // OverflowException escaping the checked cast.
        // AC-8.3 (F-14): a truncated stream causes ReadVarUInt64 to throw EndOfStreamException
        // or InvalidDataException (malformed varint). Both are low-level I/O exceptions that
        // must not escape the serializer boundary — wrap them as UnsupportedFormatVersionException.
        ulong rawVersion;
        try
        {
            rawVersion = VarintHelpers.ReadVarUInt64(stream);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
            throw new UnsupportedFormatVersionException(
                0,
                "Tagged-wire versioned record has a truncated or malformed version varint — corrupt or incomplete stream.",
                ex);
        }
        if (rawVersion is 0 or > int.MaxValue)
        {
            throw new UnsupportedFormatVersionException(
                unchecked((uint)rawVersion),
                $"Tagged-wire versioned record carries invalid schema version {rawVersion} (must be in [1, {int.MaxValue}]) — corrupt header or foreign stream.");
        }

        var observed = new SchemaVersion((int)rawVersion);

        // Resolve the writer-schema version against the registry. Tagged wire is self-describing, so
        // this is a validation step, not a decode dependency — an unknown version is a hard error.
        try
        {
            _ = await registry.GetAsync(subject, observed, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            throw new UnsupportedFormatVersionException(
                (uint)observed.Value,
                $"Schema version {observed.Value} is not registered under subject '{subject}'.");
        }

        // The body decode is the same corruption surface as the version varint: a torn or
        // malformed tagged-wire body causes the generated reader to throw EndOfStreamException
        // (truncated), InvalidDataException (forged/oversized length prefix, bad varint), or
        // OverflowException (a forged varint past the checked((intN)…) narrowing on lengths,
        // counts, and scalar reads). All are low-level decode artifacts that must not escape
        // the serializer boundary — wrap them as the domain's fail-closed corruption type,
        // matching the version-read wrap above and the JSON sibling's body wrap. The same holds
        // for a body that decodes but that T itself rejects (a validating constructor or init
        // accessor throwing ArgumentException): T's own Serialize only emits values T accepts, so
        // such bytes come from corruption or a foreign stream.
        // Excluded: an UnsupportedFormatVersionException the body raises itself (already typed),
        // cancellation, OOM, and faults of the stream itself (IOException other than end of
        // stream, a disposed or unreadable stream) — those are not evidence of corrupt bytes.
        T value;
        try
        {
            value = T.Deserialize(stream);
        }
        catch (Exception ex) when (IsBodyDecodeFailure(ex))
        {
            throw new UnsupportedFormatVersionException(
                (uint)observed.Value,
                $"Tagged-wire versioned record (schema version {observed.Value}) has a truncated or malformed body — corrupt or incomplete stream.",
                ex);
        }

        return (observed, value);
    }

    private static bool IsBodyDecodeFailure(Exception ex) => ex switch
    {
        UnsupportedFormatVersionException or OperationCanceledException or OutOfMemoryException => false,
        ObjectDisposedException or NotSupportedException => false,
        EndOfStreamException => true,
        IOException => false,
        _ => true,
    };
}
