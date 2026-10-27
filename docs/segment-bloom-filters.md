# Persisted segment Bloom filters

SSTables store a Bloom filter in their footer to avoid reading data blocks for absent keys.
The default uses the actual entry count and a 1% false-positive target. Columnar segments
use exact dataset/date indexes by default and can additionally enable a full-key Bloom filter.

`SegmentBloomFilterOptions<TKey>` configures each new physical segment, including compaction
outputs. It is an immutable record in `Boutquin.Storage.Domain.ValueObjects.SortedStringTable`.

```csharp
var options = new SegmentBloomFilterOptions<MyKey>(
    expectedItems: 131_072,
    falsePositiveProbability: 0.001);
var format = new SstSegmentFormat<MyKey, MyValue>(options);
```

Capacity is an expected population, not a write limit. Exceeding it increases false positives;
it must never cause false negatives. Omitting it sizes each filter from its segment's actual
entry count. Native filters are limited to just under 256 MiB and 64 hash functions; unsupported sizing
fails the write before publication. Invalid nonpositive capacities and probabilities outside
the finite open interval `(0, 1)` fail option construction.

Both formats have convenience constructors and full overloads with required arguments:

```csharp
new SstSegmentFormat<MyKey, MyValue>(options, codec, blockSizeBytes,
    restartInterval, codecRegistry, blockCache);
new ColumnarSegmentFormat<MyDatasetKey, MyRecord>(timeSeriesOptions, innerFormat, sortSpec);
```

For columnar segments, `timeSeriesOptions` has type
`SegmentBloomFilterOptions<TimeSeriesKey<MyDatasetKey>>`. Membership is for the complete
dataset/date key. Bloom negatives are used only for point reads. Range scans, projections,
tombstone discovery and snapshot visibility retain their existing index and sequence rules.

## Custom filters

A custom factory receives the resolved capacity and probability and returns a fresh, empty
`IBloomFilter<TKey>` per physical segment. Supply an `IBloomFilterStateCodec<TKey>` alongside it:

```csharp
var options = new SegmentBloomFilterOptions<MyKey>(
    factory: (capacity, probability) => CreateFilter(capacity, probability),
    stateCodec: myCodec);
```

The codec exposes `CodecId`, a positive `Version`, `Serialize(filter)` and `Deserialize(state)`.
Its identity must cover hashing, seeds, key representation and state layout. State must be
self-contained and at most 16 MiB. The codec must validate its payload rather than trust its
checksum as a structural guarantee. Changing semantics requires a different identity or version.

Before publication, Storage deserializes the encoded custom state and checks membership for
every data key. That validation instance is disposed immediately. This is a seal-time check;
reopening invokes only the decoder, with no factory calls or `Add` replay. Codec methods are
borrowed and must support concurrent calls. Filters are owned and disposed by Storage. A reused
factory or decoder instance is rejected without taking ownership away from its first segment.
Filter disposal must be idempotent and nonthrowing. Custom filters must preserve no-false-negative
semantics for serialized key identity; false positives remain permissible.

Missing or mismatched codecs throw `UnsupportedBloomFilterCodecException`, a configuration
exception distinct from corruption. `ExcludeAndSurface` must not hide valid data because the
application omitted its decoder. A malformed state fails closed. Columnar metadata is loaded
lazily on first use; engine recovery loads it before admitting reads and writes.

To stop writing filters while still reading existing custom segments:

```csharp
var options = new SegmentBloomFilterOptions<MyKey>(enabled: false, stateCodec: myCodec);
```

A codec without a factory is decoder-only; enabled writes then use the native filter. Disabling
affects new writes only. Existing segments always use their persisted descriptor. A native
segment needs no custom codec. A custom codec is never disposed by Storage.

## Footer representation

Native SST filters keep the existing bit-count, hash-count and bit-array fields. Disabled SST
filters store zero counts and no bits. A custom SST filter stores zero native counts and appends
a custom descriptor after the tombstone list, before the existing footer CRC. Columnar segments
append an enabled filter descriptor after their tombstone list, also within their footer CRC.
No separate sidecar or second durability transaction is introduced.

Descriptors start with ASCII `BLM1` and a one-byte kind. Integers below are signed 32-bit
little-endian values; lengths must be nonnegative and bounded by the remaining descriptor bytes.

| Kind | Body after magic and kind |
|---|---|
| 0 (native, columnar) | bit count, hash count, byte length, native bit array |
| 1 (custom) | codec-ID UTF-8 byte length, ID bytes, version, state byte length, state bytes |

The ID is at most 256 UTF-8 bytes. Trailing bytes, unknown kinds and mismatched codecs are
rejected. An absent extension selects ordinary native SST or unfiltered columnar behavior.
These are modes of the current format version 1. Older binaries that do not implement custom
descriptors must not read files written with custom filters. Default native SST bytes and
unfiltered columnar bytes retain their existing layout.
