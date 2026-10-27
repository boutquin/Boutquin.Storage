# Source Generator Type Reference — 2.0.0

The Boutquin.Storage source generator emits serialization and columnar access code for types annotated
with `[Key]`, `[StorageSerializable]`, and `[ColumnarStorable]`. This document enumerates every supported
CLR type, the wire format it produces on each emission path, the diagnostics that
govern its use, and the migration surface relative to 1.x.

Build-time requirement: a Roslyn 5.9 or newer compiler host. The repository and CI pin .NET SDK 10.0.401, and the packaged analyzer is tested through a fresh-cache consumer using that SDK. Older compiler hosts cannot load this generator version.

Install the analyzer package at the same version as the Domain and Infrastructure packages:

```sh
dotnet add package Boutquin.Storage.SourceGenerator --version 2.0.0
```

The package places both the generator and its BSSG010 code-fix assembly under
`analyzers/dotnet/cs`; there is no separate CodeFixes package.

Two wire formats, selected by annotation:

- **`[StorageSerializable]` → tagged wire** (DDIA 2e Ch. 5, Protobuf family). Every
  field carries an explicit tag number and wire-type hint; unknown tags are skipped
  at read time so old readers gracefully tolerate new-writer fields and absent
  tags fall to default at the reader. Schema evolution works by adding tags and
  retiring via `[ReservedTag]`.
- **`[Key]` → ordered wire**. Flat concatenation of big-endian, sign-bit-flipped
  bytes per field so `byte-lex(encode(a), encode(b)) == IComparable<T>.Compare(a, b)`.
  LSM SSTable binary search and range scan can operate on raw byte spans without
  deserialising each key on every step.

The key-value store APIs exclude keys and values equal to `default(T)` at the store
boundary. For a generated `[Key]` record struct, the all-default key is therefore
reserved: `OrderId(0)` for `record struct OrderId(long Value)` cannot be stored,
looked up, checked for presence, or removed through those APIs. Individual zero-valued
fields remain valid when the complete key differs from `default(TKey)`; a numeric
value of zero is also excluded when it is the complete stored value.

`[Key]` and `[StorageSerializable]` types must be `partial`; `record struct` is the
recommended shape and produces the complete generated equality surface. Every serialized
property must declare an explicit `[Tag(N)]`. The generated `Deserialize` rebuilds the value
by calling a constructor with every public instance property in declaration order, so the type
needs a constructor whose leading parameters have exactly those types in that order, each
named like its property ignoring case (a
positional record's primary constructor always qualifies; a record with body-declared
properties needs one written by hand). A `[StorageSerializable]` type must also be a record or
a struct, because its generated `ProjectTo` uses `with` expressions. `[ColumnarStorable]` requires a partial record
class or record struct because its generated `Rehydrate` and `ProjectTo` methods use record
`with` expressions. Tag 0 is reserved by the generator for the per-record schema-format
version marker.

The generators persist public instance **properties** only. A public instance field, declared on
the annotated type or inherited (including a `readonly` or `required` one), would never reach the
wire or a column and would read back as its default, so all three generators reject it with
BSSG024; static and `const` fields are type state and are allowed, as are non-public fields. A
`[ColumnarStorable]` type's `required` members must all be columns (public properties with a `set`
or `init` accessor); a `required` member with no column is rejected with BSSG025.

## Supported type set

### Tier 1 — numeric primitives

| CLR type | Tagged wire | Ordered wire |
|---|---|---|
| `sbyte` | zigzag varint | 1 byte, sign-bit-flipped |
| `byte` | varint | 1 byte |
| `short` | zigzag varint | 2 bytes BE, sign-bit-flipped |
| `ushort` | varint | 2 bytes BE |
| `int` | zigzag varint | 4 bytes BE, sign-bit-flipped |
| `uint` | varint | 4 bytes BE |
| `long` | zigzag varint | 8 bytes BE, sign-bit-flipped |
| `ulong` | varint | 8 bytes BE |
| `Int128` | 16-byte LE payload (Fixed128) | 16 bytes BE, high-byte XOR 0x80 (signed BE + sign-flip) |
| `UInt128` | 16-byte LE payload (Fixed128) | 16 bytes BE (unsigned, order-preserving) |
| `Half` | 2-byte LE payload (Fixed16) | 2 bytes BE, sign-flip for positives / bitwise-NOT for negatives. NaN rejected at encode time (`ArgumentException`) |
| `float` | 4-byte LE payload (Fixed32) | 4 bytes BE, sign-flip for positives / bitwise-NOT for negatives. NaN rejected at encode time (`ArgumentException`) |
| `double` | 8-byte LE payload (Fixed64) | 8 bytes BE, sign-flip for positives / bitwise-NOT for negatives. NaN rejected at encode time (`ArgumentException`) |

Signed integers are **zigzag-encoded** before varint so small negatives (−1, −2) compact
to one byte. `(n << 1) ^ (n >> 63)` for `long`.

### Tier 2 — non-numeric primitives

| CLR type | Tagged wire | Ordered wire |
|---|---|---|
| `bool` | varint (0 or 1) | 1 byte (0 or 1) |
| `char` | varint of UTF-16 code unit | 2 bytes BE |
| `string` | length-prefixed UTF-8 (LEN wire type) | UTF-8 with 0x00 byte escaped as `0x00 0xFF`, field terminator `0x00 0x00` |

Strings on the tagged path carry a varint byte-count prefix. On the ordered path
they self-terminate with `0x00 0x00` — the escape scheme preserves UTF-8 byte-lex
ordering while letting composite keys concatenate cleanly. In-memory `CompareTo`
on `[Key]` strings uses `OrderedByteHelpers.CompareStringByteLex`, which compares
in Unicode *code-point* order — the order of the encoded UTF-8 bytes. Neither
culture-sensitive `string.CompareTo` nor `String.CompareOrdinal` matches the
bytes: ordinal comparison orders UTF-16 *code units*, which inverts
supplementary-plane characters (U+10000 and above — every emoji) against
U+E000–U+FFFF. Ordered-wire key strings must be well-formed UTF-16: an unpaired
surrogate cannot be encoded in UTF-8 and is rejected with `ArgumentException` at
serialize time (replacement substitution would collide distinct keys). `[Key]`
string properties must be declared non-nullable (`string`, not `string?`) —
BSSG019 enforces this at compile time because the ordered wire has no null
discriminator for strings.

### Tier 3 — structured BCL primitives

| CLR type | Tagged wire | Ordered wire |
|---|---|---|
| `DateOnly` | varint of `DayNumber` | 4 bytes BE of `DayNumber` (always ≥ 0) |
| `TimeOnly` | varint of `Ticks` | 8 bytes BE of `Ticks` (always ≥ 0) |
| `TimeSpan` | zigzag varint of `Ticks` | 8 bytes BE of `Ticks`, sign-bit-flipped |
| `DateTimeOffset` | length-prefixed nested payload: `Ticks` varint + zigzag varint of `OffsetMinutes` | 10 bytes: 8 BE `UtcTicks` + 2 BE sign-flipped `OffsetMinutes`. **UTC-instant primary, offset tiebreaker** — two values at the same UTC instant share byte-identical first 8 bytes and sort adjacent with the lower offset first. In-memory `CompareTo` uses `OrderedByteHelpers.CompareDateTimeOffsetByteLex` (instant, then offset) — `DateTimeOffset.CompareTo` compares the instant only and would report same-instant/different-offset keys as equal while their encodings differ. Key equality and hashing follow the same rule (instant and offset, `DateTimeOffset.EqualsExact`), so two keys are equal exactly when `CompareTo` returns 0 |
| `Guid` | 16 bytes, non-canonical (Fixed128) | 16 bytes canonical big-endian |
| `decimal` | 16 bytes LE (4 × `int32` via `GetBits`) | 17 bytes: 1-byte sign discriminator (0x00 neg / 0x01 zero / 0x02 pos) + 16-byte magnitude envelope (12 BE mantissa bytes + `0xFF − scale` byte + 3 zero-pad bytes). `[Key]` decimals require `[DecimalScale(N)]` (BSSG017); every value is canonicalised to scale `N` **value-preservingly** — raw scales below `N` pad up (`1m` → `1.0000m`) and trailing-zero digits beyond `N` trim (`1.500000m` → `1.5000m`), so `1.0m`, `1.00m`, `1.000m` encode identically. Values not exactly representable at scale `N` throw `ArgumentException` at encode time: excess fractional precision (`1.00005m` at `N=4`) and magnitudes where `\|v\| × 10^N` exceeds the 96-bit mantissa are both rejected, because silent rounding or under-scaling would make byte-lex disagree with the full-precision in-memory `decimal.CompareTo`. Quantise at the ingestion boundary. Negatives complement the magnitude bytes to match `decimal.Compare` within the negative group |

### Tier 4 — enums

Any `enum` type dispatches to its underlying integer kind. On the tagged path
this means the emitted bytes are the underlying kind's tagged encoding (varint
or zigzag varint depending on signedness); on the ordered path the enum value
round-trips through BE + sign-flip for signed underlyings.

Supported underlyings: `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`,
`ulong`. The `BarGranularity` enum used by MarketData's `PriceBarDatasetKey`
exercises this path (`int` underlying).

Because the bytes are the underlying kind's, the underlying type is part of an
enum field's wire identity. The generated `TaggedWireSchemaFields` descriptor
records it as `TaggedWireField.WireTypeName` (`int`, `uint?` for a nullable
enum, `IEnumerable<long>` for a collection of enums), and
`TaggedWireCompatibilityChecker` applies the integer rules to it: changing an
enum's underlying signedness on a stable tag is incompatible in every mode, and
a width change gets the same direction-sensitive verdict as the raw integer
change.

### Tier 5 — user recursive types and collections

| Shape | Tagged wire (`[StorageSerializable]`) | Ordered wire (`[Key]`) |
|---|---|---|
| Nested `[StorageSerializable]` | length-prefixed child payload | **BSSG014** — not allowed |
| Nested `[StorageSerializable]` that hand-implements `ISerializable<T>` | parent-framed: padded LEN + the hand-written bytes; unread bytes inside the frame are rejected as corrupt | **BSSG014** — not allowed |
| Nested `[Key]` | parent-framed: padded LEN + the child's ordered-wire bytes; unread bytes inside the frame are rejected as corrupt | **BSSG014** — not allowed |
| `IEnumerable<Tier-1 numeric or bool>` | **packed by default (F2)**: LEN-framed, concatenated element bodies, no count. `[Unpacked]` opts into one tag+body per element (Protobuf `repeated`). | **BSSG015** — not allowed |
| `IEnumerable<string/Guid/decimal/Tier-3 date-time>` | LEN-framed: count varint + element bodies. `[Packed]`/`[Unpacked]` are no-ops here (element wire type is itself LengthPrefixed or Fixed128). | **BSSG015** — not allowed |
| `IEnumerable<[StorageSerializable]>` | LEN-framed: count varint + per-element `Serialize` | **BSSG015** — not allowed |
| `IEnumerable<enum>` | LEN-framed: count varint + per-element underlying encoding | **BSSG015** — not allowed |

A collection property must be declared as `IEnumerable<T>` itself: the generated
`Deserialize` decodes into an `IEnumerable<T>` and passes it to the constructor, so
`List<T>`, `IReadOnlyList<T>`, `HashSet<T>` and other concrete or derived collection
types raise **BSSG001**. The collection property itself must be non-nullable:
`IEnumerable<T>?` raises **BSSG001**, because an absent collection tag reads as an empty
collection and cannot preserve null. Elements cannot be nullable — nullable value types
(`IEnumerable<int?>`), strings (`IEnumerable<string?>`), and nullable-annotated records
(`IEnumerable<Child?>`) are rejected — because the
collection encodings carry no per-element null discriminator (**BSSG001**).

A type may nest itself, directly (`record Node(int V, Node? Next)`,
`record struct Tree(IEnumerable<Tree> Kids)`) or through other types (`Ping` → `Pong` →
`Ping`). The bytes, not the schema, then decide how deep a value nests, so the generated
code of a type on such a cycle bounds it: a value may contain at most 100 levels of the same
type along any path. `Deserialize` throws `InvalidDataException` past that depth, so forged
input cannot overflow the stack, and `Serialize` throws `InvalidOperationException` for a
value that deep, so nothing is written that the reader would refuse. Types not on a cycle
nest only as deeply as their schema and carry no bound.

Collection elements inside a `[StorageSerializable]` skip the per-element tag
header — the enclosing LEN already scopes the block (packed and count-prefixed
encodings). In the unpacked encoding, each element carries its own tag-and-wire-type
varint whose wire type matches the element body. See
[`docs/wire-format-2.0.md`](wire-format-2.0.md) §6 for byte-level worked examples and
the cross-format reader-tolerance rule. **BSSG018** fires if `[Packed]` and `[Unpacked]`
are applied to the same property.

### Tier 6 — nullable value types

| Shape | Tagged wire | Ordered wire |
|---|---|---|
| `T?` for Tier 1–4 leaf | absent tag ⇒ null; when present, emit tag + payload | 1-byte discriminator (0x00 null, 0x01 present) + payload if present; nulls sort first |
| `AnnotatedRecord?` (nullable-annotated record class) | absent tag ⇒ null; when present, LEN-framed nested payload | **BSSG016** — nullable-of-annotated is not supported in key position |
| `string?` (nullable-annotated string) | absent tag ⇒ null; when present, emit tag + length-prefixed UTF-8 | **BSSG019** — the ordered wire has no null discriminator for strings |

## Explicitly excluded

The following CLR types cannot be used on an annotated property. The generator
emits **BSSG008** with a targeted remediation message:

| Type | Why | Replacement |
|---|---|---|
| `System.DateTime` | `DateTimeKind` is ambiguous on round-trip | `DateTimeOffset` for instant timestamps, `DateOnly` for calendar dates |
| `nint` | architecture-dependent wire width | `int` or `long` |
| `nuint` | architecture-dependent wire width | `uint` or `ulong` |

Reference types other than `string` produce the generic **BSSG001**. Wrap the
data in a `[StorageSerializable]` record to serialise it.

## Tag numbering (`[Tag(N)]`, `[ReservedTag(N)]`)

Every property on a `[Key]` or `[StorageSerializable]` record MUST declare a
`[Tag(N)]` on its positional-constructor parameter or the emitted property:

```csharp
[StorageSerializable]
public partial record struct Bar(
    [property: Tag(1)] DateOnly Date,
    [property: Tag(2)] decimal Open,
    [property: Tag(3)] long Volume);
```

Rules:

- **Tag numbers are positive integers ≤ 268_435_455 (2^28 − 1).** The upper bound
  keeps the combined tag+wire-type varint inside 5 bytes.
- **Tag 0 is reserved by the generator** for the implicit per-record schema-format
  version marker. `BSSG012` rejects `[Tag(0)]` so users can never clash with it.
- **Tag numbers are unique within a type.** Two properties sharing a tag would
  be indistinguishable on the wire.
- **Tag numbers are stable across versions.** Reassigning a tag number to a
  different property silently corrupts data written under the previous schema.
- **Retired tags stay retired.** When a property is removed, mark its old tag
  with `[ReservedTag(N)]` on the type. A future `[Tag(N)]` that collides raises
  **BSSG013** (error).

Example schema evolution:

```csharp
[StorageSerializable]
[ReservedTag(3)]  // previously held a deleted 'Exchange' field; never re-use 3
public partial record struct Bar(
    [property: Tag(1)] DateOnly Date,
    [property: Tag(2)] decimal Close,
    [property: Tag(4)] long Volume);
```

See `src/Samples/ReservedTagEvolution.cs` for the full V1 → V2 evolution
pattern, including the prose walk-through of why tag 3 stays reserved and how
forward / backward compatibility read a V2 record as V1 and vice versa.

## Schema-format version — tag 0

Every tagged-wire record starts with tag 0 carrying the schema-format version
varint. The current writer emits version 1. On read:

1. The reader reads the length prefix and scopes to the record payload. A
   declared length larger than the bytes remaining throws `EndOfStreamException`
   (or `InvalidDataException` for a malformed length varint).
2. The first tag MUST be 0 with wire type `Varint`. A tag that decodes to an
   invalid wire type throws `InvalidDataException`; any other first tag throws
   `UnsupportedFormatVersionException`.
3. The varint value following tag 0 MUST equal 1. A newer version (written by
   a future writer) also throws `UnsupportedFormatVersionException` so the old
   reader cannot silently misinterpret newer payload bytes.

1.x-written data carries no format marker, so it is rejected by whichever of
these checks its bytes happen to trip first. Treat `UnsupportedFormatVersionException`,
`InvalidDataException` and `EndOfStreamException` together as the 1.x signal
(see the migration guide). The signal is best-effort: a 1.x record whose leading
bytes read as a length of 2 followed by `00 01` passes all three checks and
deserializes to defaulted fields without any exception, so identify 1.x data
through an out-of-band marker (a directory name or version file) and treat the
exceptions as a second line of defence.

`[Key]` types use ordered wire, not tagged wire, and therefore carry no tag 0
marker. Keys are versioned at the consumer level (key-shape changes force a
data-directory rebuild regardless).

## Diagnostic quick reference

| ID | Severity | Fires on |
|---|---|---|
| BSSG001 | Error | Unsupported property type — not in Tier 1–6, no `[Key]`/`[StorageSerializable]` annotation, and no `IEnumerable<supported>` shape. On a `[StorageSerializable]` type a nullable collection (`IEnumerable<T>?`), a collection declared as anything other than `IEnumerable<T>` (`List<T>`, `IReadOnlyList<T>`, `HashSet<T>`, …), or a collection with nullable elements (including `string?`), is also unsupported |
| BSSG002 | Error | Annotated type not declared `partial` |
| BSSG003 | Warning | Annotated type is not `record struct` (advisory — still generates) |
| BSSG004 | Error | Multiple `[assembly: StorageDefaults]` attributes |
| BSSG005 | Error | Type carries both `[Key]` and `[StorageSerializable]` |
| BSSG006 | Warning | Type already explicitly implements `ISerializable<T>` / `IComparable<T>` / `IEquatable<T>` — generator skips that interface |
| BSSG007 | Error | Incompatible `[ColumnEncoding]` override on `[ColumnarStorable]` property |
| BSSG008 | Error | Property type is `DateTime`, `nint`, or `nuint` (with targeted remediation message) |
| BSSG010 | Error | Property missing `[Tag(N)]`. Message suggests the lowest unused positive tag |
| BSSG011 | Error | Duplicate `[Tag(N)]` within a type |
| BSSG012 | Error | Tag number out of range (≤ 0 or > 2^28 − 1) |
| BSSG013 | Error | Tag number collides with a `[ReservedTag]` on the same type |
| BSSG014 | Error | `[Key]` type contains a nested `[StorageSerializable]` record (ordered wire is flat) |
| BSSG015 | Error | `[Key]` type contains an `IEnumerable<T>` property |
| BSSG016 | Error | `[Key]` type contains a nullable-of-annotated-record |
| BSSG017 | Error | `[Key]` decimal property missing `[DecimalScale(N)]` with `0 ≤ N ≤ 28` |
| BSSG018 | Error | Property declares both `[Packed]` and `[Unpacked]` — apply at most one |
| BSSG019 | Error | `[Key]` string property declared `string?` — ordered wire has no null discriminator for strings |
| BSSG020 | Error | `[ColumnarStorable]` settable property type has no columnar encoding. Supported column types: `DateOnly`, `int`, `long`, `decimal`, `double`, `float`, `bool`, `string`, and enums (stored as the underlying value widened to int64 on the DeltaVlq stream). Get-only properties are excluded automatically (derived state — `Rehydrate` cannot assign them and their getters recompute from stored columns) |
| BSSG021 | Warning | Array, `stackalloc`, `ArrayPool.Rent`, or `Array.CreateInstance` allocation sized from a decoded length without a preceding bound against the remaining payload or stream bytes |
| BSSG022 | Error | `[ColumnarStorable]` target is a plain class or struct rather than a record class or record struct |
| BSSG023 | Error | Annotated type (`[Key]`, `[StorageSerializable]` or `[ColumnarStorable]`) is generic. A generic containing type is supported; the annotated type itself must not declare type parameters |
| BSSG024 | Error | Annotated type carries public instance state the generator does not plan: properties inherited from a base type (settable ones, for `[ColumnarStorable]`), or public instance fields, declared or inherited. Only directly-declared public properties are planned, so that state would be dropped; declare every persisted value as a property on the annotated type |
| BSSG025 | Error | The generated code cannot construct the annotated type: it is abstract (`Deserialize`, and `Rehydrate` on a `[ColumnarStorable]` record), no constructor takes the public instance properties positionally, in declaration order with parameter names matching the property names (`Deserialize`), that constructor leaves `required` members unset because it lacks `[SetsRequiredMembers]` (`Deserialize`), or a `[StorageSerializable]` type is a class that is not a record, or has a public property with no `set`/`init` accessor (`ProjectTo` assigns every property with `with`), or a `[ColumnarStorable]` type has a `required` member that is not a column (a non-public property or field, which `Rehydrate` could only set to default). Declare a positional record or a matching constructor, and give every property an `init` accessor (or implement `IProjectable<T>` by hand) |
| BSSG026 | Error | An annotated type is nested in a containing type that is not declared `partial`. The generated code re-opens every containing type, so each one must be `partial` |

BSSG009 is reserved-unshipped; the numbering gap is intentional to keep
BSSG010–BSSG013 contiguous.

## Runtime helpers

The generator emits calls into two helper classes in `Boutquin.Storage.Domain`:

- **`Boutquin.Storage.Domain.Helpers.VarintHelpers`** — ULEB128 + zigzag +
  combined-tag-and-wire-type varint + UTF-8 length-prefixed string + unknown-tag
  skip. Used by the tagged wire emitter.
- **`Boutquin.Storage.Domain.Helpers.OrderedByteHelpers`** — BE + sign-bit-flip
  for signed integers, plain BE for unsigned, UTF-8 terminator-escape for
  strings, 1-byte null discriminator, canonical BE `Guid`. Used by the ordered
  wire emitter.

Both helper classes are public API. Hand-rolled `ISerializable<T>` implementations
can call them directly when generator coverage doesn't apply (e.g., generic
wrappers).

## Design intent

The tagged-wire format tracks DDIA 2e Ch. 5's prescription for "any data that
outlives the process that created it" — explicit field identifiers plus
variable-length integer representation. The ordered wire format is the
FoundationDB / CockroachDB / Spanner composite-key encoding, chosen because
LSM SSTable binary search amortises one decode per binary-search step down to
zero when byte-lex matches numeric order. The two wire formats address
fundamentally different concerns (schema evolution vs byte-comparable
persistence) and cannot be unified — the generator selects one per annotation.
