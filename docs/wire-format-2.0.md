# Boutquin.Storage tagged-wire format — v2.0

> **Audience.** Third-party implementors writing a decoder for
> `[StorageSerializable]`-annotated records in a language other than .NET. Every field on a
> Boutquin.Storage record-struct value is persisted through this wire; the goal of this
> document is that a Go/Rust/Python developer with no access to the C# emitter can produce a
> byte-exact round-trip implementation from this text alone.
>
> **Reference.** DDIA 2nd edition (Kleppmann), Chapter 5 "Encoding and Evolution",
> pp. 169–171, Figure 5-3 (tag-and-wire-type varint).

Spec covered by this document: **schema-format version byte `0x01`**. All Boutquin.Storage
2.0.x releases write `0x01`; a future major version may introduce `0x02` with additional
well-known types, but the framing (length prefix → tag-0 schema-format version byte → body
fields) is stable across versions by construction.

## 1. Per-record framing

Every persisted record has three layers:

```
+------------------+----------------------+------------------------------+
| length_prefix    | tag_0 schema_version | field_1, field_2, …, field_N |
|  (ULEB128)       |  (ULEB128 body)      |  (tag-and-wire-type varint + |
|                  |                      |   payload, one per field)    |
+------------------+----------------------+------------------------------+
                   `--- payload ---`
                    (length_prefix bytes)
```

- `length_prefix` — a ULEB128 unsigned integer, the byte count of the payload that follows.
- `tag_0 schema_version` — the first bytes of the payload are always the tag-0 header (a
  single `0x00` byte, since tag 0 combined with wire-type Varint = `0x00`) followed by a
  ULEB128 body carrying the schema-format version number (`0x01` for this document).
- Remaining payload bytes are zero or more field records, each `(tag-and-wire-type varint,
  payload bytes)`. Fields may appear in any order. Unknown tags are skipped by the reader
  using the wire-type hint (§4).

If the reader encounters a record whose first payload byte is not `0x00`, it raises
`UnsupportedFormatVersionException` (schema-format version not recognized). If the version
ULEB128 that follows the `0x00` is not a value this document describes, the same exception is
raised with the observed version number.

### Empty record — worked example

A `[StorageSerializable]` record-struct with zero tagged fields (or where every field was
omitted by a partial writer) encodes as:

```
hex  : 02 00 01
byte : length=2, tag0-header=0x00, schema-version=0x01
```

## 2. Tag-and-wire-type varint (DDIA Fig 5-3)

Every field header is a single ULEB128 varint whose low 3 bits encode the `wire_type` and
whose upper bits encode the `tag_number` (shifted left by 3).

```
combined_value = (tag_number << 3) | wire_type
varint_bytes   = ULEB128(combined_value)
```

Wire types are:

| Value | Name            | Payload                                         |
|-------|-----------------|-------------------------------------------------|
| 0     | Varint          | ULEB128 body (signed ints use zigzag, §3)       |
| 1     | Fixed16         | exactly 2 bytes, little-endian                  |
| 2     | Fixed32         | exactly 4 bytes, little-endian                  |
| 3     | Fixed64         | exactly 8 bytes, little-endian                  |
| 4     | LengthPrefixed  | ULEB128 byte-count, then that many body bytes   |
| 5     | Fixed128        | exactly 16 bytes, little-endian                 |

Tag 0 is reserved for the per-record schema-format version header (§1). User-written fields
must use tag numbers in `1 .. 2^28 − 1`; the compiled varint stays ≤ 5 bytes.

### Worked example: `int Volume` with `Volume = 42`, `[Tag(1)]`

The ZigZag of 42 is `(42 << 1) ^ (42 >> 63) = 84 = 0x54`.

```
hex  : 04 00 01 08 54
byte : length=4, tag0-header=0x00, schema-version=0x01,
       tag1-header=(1<<3)|0=0x08, zigzag(42)=0x54
```

## 3. Integer encodings

### ULEB128

Each byte carries 7 bits of payload in its low bits and one continuation flag in the high
bit (`0x80`). The encoding is little-endian at the 7-bit-group level: the first byte contains
bits 0–6 of the value, the second byte (when present) contains bits 7–13, and so on, up to
10 bytes for the full 64-bit range.

Canonical encodings of small integers:

| Value     | Bytes                       |
|-----------|-----------------------------|
| 0         | `00`                        |
| 127       | `7F`                        |
| 128       | `80 01`                     |
| 16 383    | `FF 7F`                     |
| 16 384    | `80 80 01`                  |
| 268 435 455 (2^28 − 1) | `FF FF FF 7F`  |

### ZigZag

Signed integers are ZigZag-encoded before being ULEB128'd so that small-magnitude negatives
stay compact:

```
zigzag_u64 = ((int64) << 1) ^ ((int64) >> 63)
```

| Signed value | ZigZag unsigned | ULEB128 bytes |
|--------------|-----------------|---------------|
| 0            | 0               | `00`          |
| -1           | 1               | `01`          |
| 1            | 2               | `02`          |
| -2           | 3               | `03`          |
| 2            | 4               | `04`          |

Without ZigZag, the two's-complement bits of `-1` would require the full 10-byte varint.

### Narrowing-read semantics

Every Tier-1 narrowing scalar read (any read whose target type is narrower than the
varint's effective domain — `sbyte` / `byte` / `short` / `ushort` / `int` / `uint` /
`char`) is wrapped in `checked(...)` at the emitter. A writer that emitted a wider
scalar than the reader's schema declares for the same tag (e.g., a writer with
`long Volume = 100_000` consumed by a reader whose schema says `short Volume`) surfaces
an `OverflowException` at the read site rather than silently truncating to a different
value.

DDIA p. 171 names the hazard ("there is a risk that values will get truncated"); this format
goes further than the book's description by making the reader detect it and fail fast. Previously
the same `checked()` was applied only on the enum read path; the current emitter generalises the
guard to every Tier-1 narrowing scalar. `Int64` / `UInt64` reads do not narrow and are
not wrapped. `String` / `Bool` reads have their own discrete contracts and are unchanged.

### Missing-tag defaults for non-nullable strings

When a reader's schema declares a non-nullable `string` field and the writer's encoded
record omits the tag for that field, the reader populates the field with
`string.Empty`, NOT `null`. The emitter previously initialised non-nullable string
fields to `default!` (i.e., `null` cast to `string`), which NRE'd a forward-compatible
reader walking a record whose writer omitted the tag. DDIA p. 171 prescribes `""` for
missing strings.

The detection looks at the property's nullability annotation: a non-nullable `string`
gets `string.Empty` as its absent-tag default; a nullable `string?` continues to
default to `null` (the reader's declared nullability says `null` is acceptable).
The generated writer mirrors that default: a `string?` property writes no tag when the value is
null, the same absent-tag-means-null contract as a nullable value type (`T?`) or a nullable
nested record. A `string?` property that old data leaves at `null` therefore writes back
unchanged, as the same bytes the old writer produced. A non-nullable `string` property that
holds `null` (only reachable by suppressing the nullable warnings) still makes the writer throw
`InvalidOperationException`.

## 4. Wire-type → payload dispatch

The reader reads one tag-and-wire-type varint, looks up the wire type, and skips or decodes:

- **Varint**: read ULEB128 (signed fields also apply ZigZag decode). Used for `sbyte`,
  `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `bool`, `char`, `DateOnly`,
  `TimeOnly`, `TimeSpan`, and the underlying body of any enum.
- **Fixed16**: read exactly 2 little-endian bytes. Used for `System.Half` (`HalfToUInt16Bits`
  little-endian).
- **Fixed32**: read exactly 4 little-endian bytes. Used for `float` / `System.Single` (IEEE
  754 bit pattern, little-endian).
- **Fixed64**: read exactly 8 little-endian bytes. Used for `double`.
- **Fixed128**: read exactly 16 little-endian bytes. Used for `Int128`, `UInt128`, `Guid`
  (Microsoft mixed-endian layout; see §5), and `decimal` (four little-endian int32 words:
  low, mid, hi, flags — matching `decimal.GetBits`).
- **LengthPrefixed**: read one ULEB128 length, then that many payload bytes. Used for
  `string` (UTF-8 bytes), `DateTimeOffset` (LEN-framed sub-block: `ticks` ULEB128 + offset-
  minutes ZigZag), all primitive collections (§6), all nested `[StorageSerializable]` records
  (§7), and future wire-type extensions.

An unknown tag with a recognized wire type is consumed-and-discarded (`SkipField`). A tag
with an unrecognized wire type value (anything outside `0..5`) aborts the read.

**Unknown fields are NOT retained.** Discarding (rather than buffering) unknown tags means a
read-modify-rewrite cycle through an *older* record type silently drops every field a newer
writer added — the round-trip data-loss hazard DDIA 2e Ch. 5 illustrates with its Figure 5-1
database example (proto3 itself only gained unknown-field retention in 3.5). This is a
deliberate v2.0 trade-off: generated record structs carry no hidden buffer field, and
inline-value and columnar compaction decode and re-serialize records through the current
`TValue` schema. Value-log pointers are copied without rewriting their payloads, but that
does not protect inline records. The constraint applies to application writes **and engine
compaction**: use a schema that knows every stored field, or unknown fields can be lost. If round-trip retention becomes a
requirement, it needs an explicit opt-in record shape (a retained-unknowns side buffer) —
not silently changed semantics.

## 5. Tier-3 well-known-type encodings

### `DateOnly`

Body: ULEB128 of `value.DayNumber` (days since `0001-01-01`). The CLR's `DateOnly.MaxValue`
is `9999-12-31` (day number 3,652,058), so the value fits in 4 ULEB128 bytes; dates before
day number 2,097,152 (2^21, around year 5742) fit in 3. Decoders must size for the full
4-byte range, and readers reject day numbers beyond `Int32` as corrupt.

### `TimeOnly`

Body: ULEB128 of `value.Ticks`. Wire type: Varint. A **tick** — here and everywhere below —
is 100 nanoseconds (10,000,000 ticks per second, the CLR tick unit); `TimeOnly` ticks count
from midnight (`00:00:00.0000000`), so the value ranges `0 .. 863,999,999,999`.

### `TimeSpan`

Body: ZigZag-encoded ULEB128 of `value.Ticks` (can be negative). Wire type: Varint. Ticks are
100 ns units of the span's duration (a signed count; no epoch).

### `DateTimeOffset`

Wire type: LengthPrefixed. The length-prefixed block contains two integers:

1. ULEB128 of `value.DateTime.Ticks` (always non-negative for reachable DateTimes). Ticks are
   100 ns units counted from the epoch `0001-01-01T00:00:00.0000000` of the LOCAL clock-face
   time (the offset is carried separately in field 2).
2. ZigZag ULEB128 of `value.Offset.TotalMinutes` (can be negative; range roughly `-840 ..
   +840`).

### `Guid`

16 Guid bytes in the Microsoft mixed-endian struct layout. Wire type: Fixed128. Produced via
`Guid.TryWriteBytes(span, bigEndian: false, out _)` on .NET; non-.NET implementors: relative
to the RFC 4122 textual form `aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee`, bytes 0–3 are the `a`
group as a little-endian uint32, bytes 4–5 the `b` group as a little-endian uint16, bytes 6–7
the `c` group as a little-endian uint16, and bytes 8–15 are the `d` + `e` groups verbatim in
textual order. (This is deliberately NOT the ordered `[Key]` wire, which uses the RFC 4122
big-endian byte order.)

### `decimal`

16 bytes: four little-endian int32 words matching `decimal.GetBits(value)` — `lo`, `mid`,
`hi`, `flags`. The `flags` word carries the scale (bits 16–23) and sign (bit 31). Wire type:
Fixed128. Non-.NET decoders must reconstruct the decimal value as
`sign × (hi × 2^64 + mid × 2^32 + lo) / 10^scale`.

> **Not canonical — value-equality does not imply byte-equality for `decimal`.** The tagged-wire
> (`[StorageSerializable]`) encoding writes `decimal.GetBits` verbatim, preserving the value's
> scale (trailing zeros). Two logically-equal decimals with different scale — `1.0m` and `1.00m` —
> therefore produce **different** 16 bytes (scale 1 vs scale 2 in the `flags` word). This is
> deliberate: it preserves the producer's trailing-zero fidelity on round-trip. The consequence is
> that **a consumer relying on byte-equality for deduplication, CRC, or content addressing must not
> treat decimal-bearing records as canonical** — equal values can hash differently. The
> *ordered-key* path (`[Key]`, `OrderedByteHelpers.WriteDecimal`) does the opposite: it canonicalizes to
> the declared scale without changing the value, rejecting values that cannot be represented
> exactly at that scale, so byte-lexicographic order matches `decimal.Compare`. Use the ordered path
> when canonical decimal bytes are required.

### Worked example: `string Name` with `Name = "hi"`, `[Tag(1)]`

UTF-8 bytes of `"hi"` are `0x68 0x69`. Length prefix for that body is `0x02`.

```
hex  : 06 00 01 0C 02 68 69
byte : length=6, tag0-header=0x00, schema-version=0x01,
       tag1-header=(1<<3)|4=0x0C, utf8-length=0x02, 'h'=0x68, 'i'=0x69
```

## 6. Collections

A property typed `IEnumerable<T>` on a `[StorageSerializable]` record uses one of three
wire shapes, selected from the property attributes and the element type:

| Element type                                                                           | Default shape            | `[Packed]` | `[Unpacked]` |
|----------------------------------------------------------------------------------------|--------------------------|------------|--------------|
| Tier-1 numeric (`sbyte`..`ulong`, `Int128`, `UInt128`, `Half`, `Single`, `Double`)     | packed                   | packed     | unpacked     |
| `bool`                                                                                 | packed                   | packed     | unpacked     |
| `string`, `Guid`, `decimal`, `Char`, `DateOnly`, `TimeOnly`, `TimeSpan`, `DateTimeOffset`, enum collections, nested `[StorageSerializable]` records | count-prefixed           | count-prefixed (no-op) | count-prefixed (no-op) |

### 6.1 Packed (Tier-1 + bool default)

```
+---------------+----------+-------------------+
| tag header    | LEN      | element bodies... |
|  (wire=LEN)   | (ULEB128)|  (no per-element  |
|               |          |   tag, no count)  |
+---------------+----------+-------------------+
```

- The tag-header's wire type is `LengthPrefixed` (4).
- `LEN` is the total byte count of the concatenated element bodies.
- Element bodies are written back-to-back using the scalar body encoding for the element
  type (zigzag varint for signed, plain varint for unsigned/bool, Fixed16 / Fixed32 / Fixed64
  / Fixed128 for the fixed-width primitives).

#### Worked example: `IEnumerable<int> Values` with `[1, 2, 3]`, `[Tag(1)]`

ZigZag encodings: `1 → 2 → 0x02`, `2 → 4 → 0x04`, `3 → 6 → 0x06`. Block of 3 bytes.

```
hex  : 07 00 01 0C 03 02 04 06
byte : length=7, tag0-header=0x00, schema-version=0x01,
       tag1-header=(1<<3)|4=0x0C, block-LEN=0x03,
       zigzag(1)=0x02, zigzag(2)=0x04, zigzag(3)=0x06
```

### 6.2 Unpacked (`[Unpacked]` opt-out)

```
+--------------+------+  +--------------+------+  +--------------+------+
| tag header   | body |  | tag header   | body |  | tag header   | body |   ...
|  (wire=type  |      |  |  (wire=type  |      |  |  (wire=type  |      |
|   of element)|      |  |   of element)|      |  |   of element)|      |
+--------------+------+  +--------------+------+  +--------------+------+
    element 1                element 2                element N
```

Each element writes its own tag-and-wire-type varint whose wire type is the element's
scalar wire type (not `LengthPrefixed`). The reader accumulates every occurrence of the
same tag into the list.

#### Worked example: `IEnumerable<long> Values` with `[1, 2]`, `[Tag(1), Unpacked]`

Per-element tag-header: `(1 << 3) | 0 = 0x08` (wire Varint). Bodies:
`zigzag(1) = 0x02`, `zigzag(2) = 0x04`.

```
hex  : 06 00 01 08 02 08 04
byte : length=6, tag0-header=0x00, schema-version=0x01,
       tag1-header=0x08, zigzag(1)=0x02,
       tag1-header=0x08, zigzag(2)=0x04
```

### 6.3 Count-prefixed (non-packable element types)

The count-prefixed shape used for `string`, `Guid`, `decimal`, Tier-3 date-time types, enums,
and nested `[StorageSerializable]` records:

```
+---------------+----------+---------+-------------------+
| tag header    | LEN      | count   | element bodies... |
|  (wire=LEN)   | (ULEB128)|(ULEB128)|                   |
+---------------+----------+---------+-------------------+
```

`count` is the element-count ULEB128 at the head of the length-prefixed block; bodies
follow. Each element body is a length-prefixed UTF-8 string, a 16-byte Guid, a 16-byte
decimal, and so on — identical to the scalar encoding for that type.

#### Padded LEN/count prefixes (write side)

The Boutquin.Storage 2.0 emitter writes the **LEN** and **count** prefixes of every
count-prefixed (and annotated) collection block as a **fixed-width 10-byte ULEB128**
(`MaxVarInt64Bytes`), regardless of the value's natural canonical width. A 10-byte slot
is reserved on the parent stream up front, the body streams in directly, and the slot is
backpatched once `__elemCount` and the body length are known. This is the
"backpatch-LEN trick" of Protobuf C++ — it eliminates the per-collection scratch
`PooledMemoryStream` rentals (and the buffer-to-buffer copy) the pre-2.0-GA emitter used to
defer LEN/count computation.

```
+---------------+----------+----------+----------+-------------------+
| tag header    | LEN      | (padding | count    | element bodies... |
|  (wire=LEN)   | (10-byte | bytes    | (10-byte |                   |
|               |  ULEB128)| 0x80…)   |  ULEB128)|                   |
+---------------+----------+----------+----------+-------------------+
```

The encoding remains a valid ULEB128 sequence: each padding byte sets the continuation
bit and zero-extends the payload, so a canonical reader walking left to right stops at
the first byte whose continuation bit is clear (the 10th byte) and recovers the original
value. Wire bytes grow by up to **18 per count-prefixed/annotated collection field**
versus a hypothetical canonical encoding (9 padding bytes × 2 prefixes).

#### Reader tolerance (read side)

A compliant reader **MUST** accept any valid up-to-10-byte ULEB128 for both prefixes —
canonical or padded. The reference reader at `VarintHelpers.ReadVarUInt64` reads up to 10
bytes, only checks for overflow on the 10th byte (`(b & 0xFE) != 0`), and accepts every
intermediate width. Third-party implementors writing compact (canonical-only) prefixes
remain wire-compatible with Boutquin.Storage 2.0 readers; readers that reject padded
prefixes are non-compliant.

#### Worked example: `IEnumerable<string> Tags` with `["a", "b"]`, `[Tag(1)]`

LEN of the count + body block = 10 (count varint) + 1 (`"a"` length 0x01) + 1 (`'a'`) + 1
(`"b"` length 0x01) + 1 (`'b'`) = **14**. Padded as 10 bytes:
`0x8E 0x80 0x80 0x80 0x80 0x80 0x80 0x80 0x80 0x00`. Count = 2 padded as 10 bytes:
`0x82 0x80 0x80 0x80 0x80 0x80 0x80 0x80 0x80 0x00`. Outer record length = 2 (tag-0 +
version) + 1 (tag-1 header) + 10 (LEN slot) + 10 (count slot) + 4 (bodies) = **27**
bytes (`0x1B`).

```
hex  : 1B 00 01 0C 8E 80 80 80 80 80 80 80 80 00 82 80 80 80 80 80 80 80 80 00 01 61 01 62
byte : length=27 (0x1B), tag0-header=0x00, schema-version=0x01,
       tag1-header=(1<<3)|4=0x0C,
       LEN=14 padded to 10 bytes,
       count=2 padded to 10 bytes,
       string("a")=0x01 0x61, string("b")=0x01 0x62
```

### 6.4 Reader tolerance (cross-format)

A reader whose schema declares `[Packed]` MUST also accept unpacked bytes for the same tag
(and vice versa), because the writer and reader may be built from different versions of the
schema. The dispatch rule is: if the first tag-and-wire-type varint for a collection tag has
wire type `LengthPrefixed`, take the packed decode path; otherwise take the unpacked decode
path (appending one element per tag occurrence). `[Unpacked]` is a writer-dispatch hint only.

## 7. Nested `[StorageSerializable]` records

A property whose type is itself `[StorageSerializable]` serializes as a LengthPrefixed
block. Parent writes `(tag_header, LEN, child_payload_bytes)`; child writes its own
length-prefixed record (from §1). The two length prefixes coincide — the child's own
length prefix *is* the parent's LEN. Reader recursion is therefore symmetric: read the
tag header, then call the child's `Deserialize`.

When a record type can contain itself (a recursive schema, directly or through other
types), the nesting depth comes from the bytes. Generated readers and writers for such a
type allow at most 100 nested levels of that type along any path: the reader rejects a
deeper value as corrupt (`InvalidDataException`) and the writer refuses to write one. The
bound is a codec limit, not a wire-format field; no bytes record it.

### 7.1 Parent-framed children

Two kinds of nested child do not write their own length prefix, so the parent writes it:

- a `[Key]` record, whose bytes are the flat ordered wire described in
  [`source-generator-type-reference.md`](source-generator-type-reference.md) (big-endian,
  sign-flipped fields, no tags, no length);
- a `[StorageSerializable]` record that hand-implements `ISerializable<T>`, whose body makes
  no framing promise.

The parent writes `(tag_header, LEN, child_bytes)` with the LEN as the same padded 10-byte
ULEB128 slot described in §6.3, backpatched once the child's length is known. The field is
therefore skippable by a reader that does not know the tag, exactly like every other
`LengthPrefixed` field (§8). A reader that knows the tag decodes the child from exactly the
LEN bytes; bytes the child leaves unread mean the frame and the child disagree, and the
reader throws `InvalidDataException`.

#### Worked example: `[Key] Pt At` with `At = Pt(5, 7)`, `[Tag(1)]`

`Pt` is `[Key] record struct Pt([property: Tag(1)] int X, [property: Tag(2)] int Y)`. Its
ordered bytes are `80 00 00 05 80 00 00 07` (each `int` big-endian with the sign bit
flipped), so LEN = **8**, padded to 10 bytes. Outer record length = 2 (tag-0 + version) + 1
(tag-1 header) + 10 (LEN slot) + 8 (child) = **21** bytes (`0x15`).

```
hex  : 15 00 01 0C 88 80 80 80 80 80 80 80 80 00 80 00 00 05 80 00 00 07
byte : length=21 (0x15), tag0-header=0x00, schema-version=0x01,
       tag1-header=(1<<3)|4=0x0C,
       LEN=8 padded to 10 bytes,
       Pt.X=5 → 80 00 00 05, Pt.Y=7 → 80 00 00 07
```

## 8. Schema evolution rules

- **Unknown tag.** Skip using the wire-type hint. Varint → consume a ULEB128 and discard.
  Fixed16/32/64/128 → advance 2/4/8/16 bytes. LengthPrefixed → read LEN, advance LEN bytes.
- **Missing tag.** When a tag the reader's schema expects is absent from the payload, the
  reader leaves the corresponding field at its default value — `0` for numerics, `false`
  for `bool`, `""`/`null` for `string`, empty list for `IEnumerable<T>`, `default(T)` for
  record types. The writer closes the rule from the other side for nullable fields and nested
  records: a `null` nullable field writes no tag, and a non-nullable nested record field
  holding `default(T)` — `null` for a record class, the zero-initialised value for a record
  struct — also writes no tag (such a child could not be serialised: its own non-nullable
  members would be null). These defaults do not promise byte-identical read/write cycles: unknown fields are
  discarded, field order can change, and known fields may be emitted explicitly. A child that is not `default(T)` is always written,
  even when its members are empty.
- **`[ReservedTag(N)]`.** A retired tag MUST NOT be reassigned. The generator enforces this
  via `BSSG013`. Third-party implementors who need equivalent discipline should track
  retired tags in their schema metadata.
- **Nested `[Key]` child (§7.1).** None of the rules above apply *inside* a `[Key]` child:
  its bytes are the positional, fixed-width ordered wire, with no tags to skip and no type
  information a reader could check. Changing a field's type (even a widening such as
  `int` → `long`, or `int` → `uint`), adding, removing or reordering fields, toggling a
  field's nullability, or changing a `[DecimalScale]` makes bytes written under the old
  layout decode to different values without any error. Such a child's layout must therefore
  stay identical across schema versions; only renaming its fields is safe. The generator
  emits the layout as a static `OrderedWireLayout` string on every `[Key]` type, and
  `TaggedWireCompatibilityChecker` rejects any change to it in every compatibility mode.

## 9. Document versioning

This document describes schema-format version byte **`0x01`**.

A future document describing version `0x02` may add well-known types (e.g., `BigInteger`,
`BigDecimal`, rational-number primitives) but MUST preserve the tag-0 header semantics and
the tag-and-wire-type varint framing. Readers encountering an unknown schema-format version
number throw `UnsupportedFormatVersionException` (.NET) or the equivalent — implementors in
other languages should define a parallel error type and surface the observed version byte
in the error message so users can diagnose version skew.

## 10. Compliance test fixture

A repository-level test,
[`tests/SourceGenerator/WireFormatDocFixtureTests.cs`](../tests/SourceGenerator/WireFormatDocFixtureTests.cs),
asserts that each worked-example byte sequence in §1 through §7 is byte-exact against the
generator-produced output for the corresponding schema. When this document changes, that
test changes in the same commit — or the build fails.
