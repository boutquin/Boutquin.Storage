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
using System.Text.Json.Serialization;
using Boutquin.Storage.Distributed.DistributedSystems;

namespace Boutquin.Storage.Distributed.Sync;

// Public CRDT Value properties omit causal state, and the default serializer cannot rebuild a CRDT
// from a get-only Value. These versioned payloads preserve components, grow-only elements, observed
// add tags, tombstones, and allocation counters inside the existing atomic journal record. Every
// built-in CRDT is covered: a family member whose state cannot be captured is refused, never
// silently persisted as an empty instance. A VectorClock keeps its components in a private map the
// default serializer writes as "{}", so its components are captured the same way; GossipProtocol keeps
// its cluster state the same way and is refused. System.Text.Json picks converters by declared type, so a
// family interface (IGCounter, IGSet<T>, IORSet<T>) is claimed too and dispatches on the runtime
// instance; IPNCounter and the family-ambiguous ICrdt<T> are refused, because a value declared that
// way would otherwise persist as its public Value only and leave the journal unreadable.
internal sealed class SyncCrdtJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(GCounter)
        || typeToConvert == typeof(VectorClock)
        || typeToConvert == typeof(PNCounter)
        || typeToConvert == typeof(IGCounter)
        || typeToConvert == typeof(IPNCounter)
        || (typeToConvert.IsGenericType && IsCoveredGeneric(typeToConvert.GetGenericTypeDefinition()));

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert == typeof(GCounter))
        {
            return new CounterConverter();
        }

        if (typeToConvert == typeof(VectorClock))
        {
            return new VectorClockConverter();
        }

        if (typeToConvert == typeof(IGCounter))
        {
            return new InterfaceConverter<IGCounter, GCounter>(new CounterConverter());
        }

        if (typeToConvert == typeof(PNCounter) || typeToConvert == typeof(IPNCounter)
            || typeToConvert.GetGenericTypeDefinition() == typeof(ICrdt<>)
            || typeToConvert.GetGenericTypeDefinition() == typeof(GossipProtocol<,>))
        {
            return (JsonConverter)Activator.CreateInstance(typeof(UnsupportedCrdtConverter<>).MakeGenericType(typeToConvert))!;
        }

        var definition = typeToConvert.GetGenericTypeDefinition();
        var element = typeToConvert.GetGenericArguments();
        var concrete = definition == typeof(GSet<>) || definition == typeof(IGSet<>)
            ? typeof(GrowOnlySetConverter<>).MakeGenericType(element)
            : typeof(SetConverter<>).MakeGenericType(element);
        var converter = (JsonConverter)Activator.CreateInstance(concrete)!;
        if (definition == typeof(IGSet<>))
        {
            return (JsonConverter)Activator.CreateInstance(
                typeof(InterfaceConverter<,>).MakeGenericType(typeToConvert, typeof(GSet<>).MakeGenericType(element)), converter)!;
        }

        if (definition == typeof(IORSet<>))
        {
            return (JsonConverter)Activator.CreateInstance(
                typeof(InterfaceConverter<,>).MakeGenericType(typeToConvert, typeof(ORSet<>).MakeGenericType(element)), converter)!;
        }

        return converter;
    }

    private static bool IsCoveredGeneric(Type definition) => definition == typeof(ORSet<>)
        || definition == typeof(GSet<>)
        || definition == typeof(IORSet<>)
        || definition == typeof(IGSet<>)
        || definition == typeof(ICrdt<>)
        || definition == typeof(GossipProtocol<,>);

    private static void ValidateCounters(Dictionary<string, long>? counters)
    {
        if (counters is null || counters.Any(entry => string.IsNullOrWhiteSpace(entry.Key) || entry.Value < 0))
        {
            throw new JsonException("CRDT component counters are missing or invalid.");
        }
    }

    private sealed record CounterState(
        int Version,
        [property: JsonConverter(typeof(VerbatimCounterMapConverter))] Dictionary<string, long> Counters);

    private sealed class CounterConverter : JsonConverter<GCounter>
    {
        public override GCounter Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var state = JsonSerializer.Deserialize<CounterState>(ref reader, options);
            if (state is null || state.Version != 1)
            {
                throw new JsonException("Unsupported or missing counter state format.");
            }
            ValidateCounters(state.Counters);
            return new GCounter(state.Counters);
        }

        public override void Write(Utf8JsonWriter writer, GCounter value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new CounterState(1, value.CaptureSyncState()), options);
    }

    private sealed record VectorClockState(
        int Version,
        [property: JsonConverter(typeof(VerbatimCounterMapConverter))] Dictionary<string, long> Components);

    private sealed class VectorClockConverter : JsonConverter<VectorClock>
    {
        public override VectorClock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var state = JsonSerializer.Deserialize<VectorClockState>(ref reader, options);
            if (state is null || state.Version != 1)
            {
                throw new JsonException("Unsupported or missing vector clock state format.");
            }
            ValidateCounters(state.Components);
            return new VectorClock(state.Components);
        }

        public override void Write(Utf8JsonWriter writer, VectorClock value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new VectorClockState(1, new Dictionary<string, long>(value.GetClock(), StringComparer.Ordinal)), options);
    }

    private sealed record GrowOnlySetState<T>(int Version, T[] Elements) where T : notnull;

    private sealed class GrowOnlySetConverter<T> : JsonConverter<GSet<T>> where T : notnull
    {
        public override GSet<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var state = JsonSerializer.Deserialize<GrowOnlySetState<T>>(ref reader, options);
            if (state is null || state.Version != 1 || state.Elements is null)
            {
                throw new JsonException("Unsupported or missing grow-only set state format.");
            }

            var elements = new HashSet<T>();
            foreach (var element in state.Elements)
            {
                if (element is null || !elements.Add(element))
                {
                    throw new JsonException("Grow-only set contains a null or duplicate element.");
                }
            }

            return new GSet<T>(elements);
        }

        public override void Write(Utf8JsonWriter writer, GSet<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new GrowOnlySetState<T>(1, [.. value.Value]), options);
    }

    // PNCounter exposes only P - N; its two component counters are private, so persisting it would
    // keep Value alone and every reload would return an empty counter. Refusing keeps the journal intact.
    private sealed class UnsupportedCrdtConverter<TCrdt> : JsonConverter<TCrdt>
    {
        public override TCrdt Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw Refusal();

        public override void Write(Utf8JsonWriter writer, TCrdt value, JsonSerializerOptions options) =>
            throw Refusal();

        private static NotSupportedException Refusal() => new(
            $"{typeof(TCrdt).Name} sync payloads cannot be persisted without losing their component state; "
            + "carry its state in a supported CRDT payload instead.");
    }

    // Persists an interface-declared payload through its built-in implementation's converter and
    // reloads it as that implementation. Any other implementation has no capturable state here.
    private sealed class InterfaceConverter<TInterface, TConcrete> : JsonConverter<TInterface>
        where TConcrete : TInterface
    {
        private readonly JsonConverter<TConcrete> _inner;

        public InterfaceConverter(JsonConverter<TConcrete> inner) => _inner = inner;

        public override TInterface Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            _inner.Read(ref reader, typeof(TConcrete), options)!;

        public override void Write(Utf8JsonWriter writer, TInterface value, JsonSerializerOptions options)
        {
            if (value is not TConcrete concrete)
            {
                throw new NotSupportedException(
                    $"{value?.GetType().Name} sync payloads declared as {typeof(TInterface).Name} cannot be persisted; "
                    + $"only {typeof(TConcrete).Name} state can be captured.");
            }

            _inner.Write(writer, concrete, options);
        }
    }

    private sealed record SetElement<T>(T Item, string[] Tags, string[] RemovedTags) where T : notnull;
    private sealed record SetState<T>(
        int Version,
        SetElement<T>[] Elements,
        [property: JsonConverter(typeof(VerbatimCounterMapConverter))] Dictionary<string, long> Counters) where T : notnull;

    private sealed class SetConverter<T> : JsonConverter<ORSet<T>> where T : notnull
    {
        public override ORSet<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var state = JsonSerializer.Deserialize<SetState<T>>(ref reader, options);
            if (state is null || state.Version != 1 || state.Elements is null)
            {
                throw new JsonException("Unsupported or missing observed-remove set state format.");
            }
            ValidateCounters(state.Counters);
            var elements = new Dictionary<T, HashSet<string>>();
            var removed = new Dictionary<T, HashSet<string>>();
            foreach (var entry in state.Elements)
            {
                if (entry is null || entry.Item is null || entry.Tags is null || entry.RemovedTags is null
                    || entry.Tags.Any(string.IsNullOrWhiteSpace) || entry.RemovedTags.Any(string.IsNullOrWhiteSpace))
                {
                    throw new JsonException("Observed-remove set contains an invalid element or tag.");
                }
                var tags = entry.Tags.ToHashSet(StringComparer.Ordinal);
                var tombstones = entry.RemovedTags.ToHashSet(StringComparer.Ordinal);
                if (!tombstones.IsSubsetOf(tags) || !elements.TryAdd(entry.Item, tags))
                {
                    throw new JsonException("Observed-remove set contains duplicate elements or unknown tombstones.");
                }
                foreach (var tag in tags)
                {
                    var separator = tag.LastIndexOf(':');
                    if (separator <= 0 || !long.TryParse(tag.AsSpan(separator + 1), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var sequence) || sequence <= 0
                        || !state.Counters.TryGetValue(tag[..separator], out var counter) || counter < sequence)
                    {
                        throw new JsonException("Observed-remove set tag exceeds its component counter.");
                    }
                }
                removed.Add(entry.Item, tombstones);
            }
            return new ORSet<T>(elements, removed, state.Counters);
        }

        public override void Write(Utf8JsonWriter writer, ORSet<T> value, JsonSerializerOptions options)
        {
            var (elements, removed, counters) = value.CaptureSyncState();
            var entries = elements.Select(entry => new SetElement<T>(entry.Key, entry.Value.ToArray(),
                removed.TryGetValue(entry.Key, out var tombstones) ? tombstones.ToArray() : [])).ToArray();
            JsonSerializer.Serialize(writer, new SetState<T>(1, entries, counters), options);
        }
    }
}

// Map keys here are replica and node identifiers, which are data. A caller's DictionaryKeyPolicy is
// applied on write only, so "Phone" would persist as "phone" and reload under that name: a reopened
// counter would stop merging idempotently and a peer checkpoint would be lost. Keys are therefore
// written and read verbatim, and a duplicate key is corruption rather than a last-wins overwrite.
internal sealed class VerbatimCounterMapConverter : JsonConverter<Dictionary<string, long>>
{
    public override Dictionary<string, long> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a JSON object of identifier counters.");
        }

        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString()!;
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value))
            {
                throw new JsonException($"Counter for '{key}' is not a 64-bit integer.");
            }

            if (!map.TryAdd(key, value))
            {
                throw new JsonException($"Identifier '{key}' appears more than once.");
            }
        }

        return map;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, long> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, count) in value)
        {
            writer.WritePropertyName(key);
            writer.WriteNumberValue(count);
        }

        writer.WriteEndObject();
    }
}
