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

namespace Boutquin.Storage.Infrastructure.Schemas;

/// <summary>
/// Shared mapping from a tagged-wire field's declared type name to the registry's
/// <see cref="SchemaFieldType"/>, and the normalization used to compare two declared type names for
/// wire-compatibility. Used by both <see cref="TaggedWireSchema"/> (lossy registry projection) and
/// <see cref="TaggedWireCompatibilityChecker"/> (stable-tag type-change detection).
/// </summary>
internal static class TaggedWireTypeMapping
{
    /// <summary>
    /// The wire-compatibility class of a type: two fields on a stable tag are wire-compatible iff
    /// their classes are equal. Comparing the framing <see cref="WireType"/> alone is not enough —
    /// several distinct, non-interchangeable types share one framing and decode to different values:
    /// <list type="bullet">
    ///   <item><description><see cref="WireType.Fixed128"/>: <c>int128</c>, <c>uint128</c>,
    ///   <c>guid</c>, <c>decimal</c> all occupy 16 bytes but with unrelated layouts (a <c>guid</c>
    ///   read as a <c>decimal</c> throws on most inputs; <c>int128</c> read as <c>uint128</c>
    ///   mis-signs).</description></item>
    ///   <item><description><see cref="WireType.LengthPrefixed"/>: <c>string</c> (UTF-8) vs
    ///   <c>datetimeoffset</c> (a ticks+offset block).</description></item>
    ///   <item><description>plain varint: <c>bool</c>/<c>char</c>/<c>dateonly</c>/<c>timeonly</c>/
    ///   <c>timestamp</c> share the framing of the unsigned-int family but reinterpret the value.</description></item>
    ///   <item><description>zig-zag varint: <c>timespan</c> (ticks) vs the signed-int family.</description></item>
    /// </list>
    /// The ONLY blessed cross-type compatibility is width change within one varint numeric
    /// family (Protobuf's <c>int32↔int64</c>, <c>uint32↔uint64</c> rule), so the signed-int and
    /// unsigned-int families each form a shared class; every other type is its own class. This
    /// generalises the earlier zig-zag/plain split (finding #5), which closed only <c>int→uint</c>
    /// and left the Fixed128, LengthPrefixed, and other-plain-varint buckets collapsed.
    /// Class equality is NECESSARY but not SUFFICIENT: within a family the change's direction
    /// matters — the mode-aware width rule lives in
    /// <see cref="TaggedWireCompatibilityChecker"/>, fed by <see cref="NumericWidthBits"/>.
    /// </summary>
    public static string CompatibilityClassOf(string typeName) => Normalize(typeName) switch
    {
        // Signed integers: zig-zag varint, mutually widening (int32 ↔ int64).
        "sbyte" or "short" or "int16" or "int" or "int32" or "long" or "int64" or "nint"
            => "varint-signed",
        // Unsigned integers: plain varint, mutually widening (uint32 ↔ uint64).
        "byte" or "ushort" or "uint16" or "uint" or "uint32" or "ulong" or "uint64" or "nuint"
            => "varint-unsigned",
        // Everything else decodes uniquely on the wire even when it shares a framing WireType with a
        // sibling — each is its own class, so a stable-tag change between byte-aliased siblings is
        // correctly rejected (and a type vs itself is trivially compatible).
        var other => "type:" + other,
    };

    /// <summary>
    /// Decoded-value bit width for the two varint numeric families; 0 for every type outside
    /// them. Width is what makes a same-class change direction-sensitive: the generated readers
    /// decode through <c>checked((intN)…)</c>, so a reader narrower than the writer throws
    /// <see cref="OverflowException"/> at read time on any out-of-range stored value (DDIA 2e
    /// Ch. 5's truncation hazard, made loud). <c>nint</c>/<c>nuint</c> count as 64-bit — the
    /// emitter transports them as their widest form.
    /// </summary>
    public static int NumericWidthBits(string typeName) => Normalize(typeName) switch
    {
        "sbyte" or "byte" => 8,
        "short" or "int16" or "ushort" or "uint16" => 16,
        "int" or "int32" or "uint" or "uint32" => 32,
        "long" or "int64" or "ulong" or "uint64" or "nint" or "nuint" => 64,
        _ => 0,
    };

    /// <summary>
    /// Maps a declared type name to the nearest <see cref="SchemaFieldType"/>. Unmapped types
    /// (Guid, byte[], char, nested records, …) fall back to <see cref="SchemaFieldType.Binary"/>.
    /// </summary>
    public static SchemaFieldType Map(string typeName) => Normalize(typeName) switch
    {
        "string" => SchemaFieldType.String,
        "bool" or "boolean" => SchemaFieldType.Boolean,
        "sbyte" or "byte" or "short" or "int16" or "ushort" or "uint16" or "int" or "int32"
            => SchemaFieldType.Int32,
        "long" or "int64" or "uint" or "uint32" or "ulong" or "uint64"
            or "int128" or "uint128" or "nint" or "nuint" => SchemaFieldType.Int64,
        "float" or "single" or "double" or "half" or "decimal" => SchemaFieldType.Float64,
        "dateonly" or "timeonly" or "datetimeoffset" or "timespan" or "timestamp"
            => SchemaFieldType.Timestamp,
        _ => SchemaFieldType.Binary,
    };

    /// <summary>
    /// Strips namespace qualification PER IDENTIFIER SEGMENT, canonicalises BCL spellings to
    /// their keyword forms, drops a trailing nullable <c>?</c> and whitespace, and lower-cases —
    /// so <c>System.Int64</c>, <c>long</c>, <c>long?</c>, <c>List&lt;long&gt;</c>, and
    /// <c>List&lt;System.Int64&gt;</c> all normalize consistently. A whole-string
    /// <c>LastIndexOf('.')</c> would cut into a generic's type-argument list
    /// (<c>"IEnumerable&lt;System.Int64&gt;"</c> → <c>"int64&gt;"</c>), making the same
    /// collection type spelled two ways false-incompatible AND two different generic types
    /// whose final type-argument simple names match false-compatible. The nullable annotation is
    /// not part of wire identity — optionality is tracked separately on <see cref="TaggedWireField"/>.
    /// </summary>
    public static string Normalize(string typeName)
    {
        var bare = typeName.TrimEnd('?');
        var builder = new System.Text.StringBuilder(bare.Length);
        var segmentStart = 0;
        for (var i = 0; i <= bare.Length; i++)
        {
            if (i < bare.Length && bare[i] is not ('<' or '>' or ',' or ' '))
            {
                continue;
            }

            if (i > segmentStart)
            {
                var segment = bare[segmentStart..i];
                var lastDot = segment.LastIndexOf('.');
                if (lastDot >= 0)
                {
                    segment = segment[(lastDot + 1)..];
                }

                builder.Append(CanonicalSegment(segment.ToLowerInvariant()));
            }

            if (i < bare.Length && bare[i] != ' ')
            {
                builder.Append(bare[i]);
            }

            segmentStart = i + 1;
        }

        return builder.ToString();
    }

    // BCL struct names → C# keyword forms, so "List<long>" and "List<System.Int64>" produce one
    // canonical spelling. Top-level scalars don't strictly need this (the switches list both
    // spellings), but generic type arguments are compared as opaque class strings.
    private static string CanonicalSegment(string segment) => segment switch
    {
        "int16" => "short",
        "int32" => "int",
        "int64" => "long",
        "uint16" => "ushort",
        "uint32" => "uint",
        "uint64" => "ulong",
        "single" => "float",
        "boolean" => "bool",
        _ => segment,
    };
}
