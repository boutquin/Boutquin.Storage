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
using System.Collections.Frozen;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Boutquin.Storage.Infrastructure.Schemas;

namespace Boutquin.Storage.Distributed.Sync;

/// <summary>A file-backed reference replica with a durable journal and receive checkpoints.</summary>
/// <remarks>Built-in JSON converters retain complete grow-only counter, grow-only set, observed-remove
/// set, and vector clock state, the CRDTs whether declared by type or family interface; a
/// <see cref="DistributedSystems.PNCounter"/> or <see cref="DistributedSystems.GossipProtocol{TNodeId, TState}"/>
/// payload is refused with <see cref="NotSupportedException"/> before the journal is rewritten, because its
/// state cannot be captured. Any payload that does not read back with the configured options as the
/// value appended (text with an unpaired surrogate, which JSON writes as U+FFFD; content a write-side option
/// such as a dictionary key policy or an ignore condition alters; a structured value declared as
/// <see cref="object"/> that would reload as a bare JSON document) is refused the same way before the
/// replace. A replica identifier JSON cannot carry exactly is refused by <see cref="CreateAsync"/> with
/// <see cref="ArgumentException"/>. The journal records the change type that wrote it, and a
/// journal written for another change type, or one without that record, is refused on load with
/// <see cref="InvalidDataException"/> before its content is read, instead of being reinterpreted and
/// rewritten. A journal of this change type whose persisted content these options cannot reproduce is
/// refused the same way; renderings that vary by process (the element order of a hash-ordered set, the
/// zone offset of a local time) are not content, so a journal this change type wrote that differs from
/// this process's serialization only in them is accepted.
/// Local append retries allocate new envelopes; peer retries deduplicate existing envelope identifiers,
/// and a known identifier carrying different content is rejected.
/// Coordinates same-file instances in one process, including spellings that differ only by symbolic
/// links or letter case and replicas of different change types. Atomic replacement does not fsync the containing
/// directory, so this reference store does not promise durability through directory-entry loss.</remarks>
public sealed class JsonFileSyncReplica<TChange> : ISyncReplica<TChange>
{
    // The renderings excused as process variance are known to be variance only for the type that wrote
    // them: a hash-ordered set reader cannot tell its own set from a foreign list, nor a local-time reader its
    // own zone offset from a foreign offset. The writer's identity is therefore checked before any excusal.
    private static readonly string s_changeType = ChangeTypeIdentity.Of(typeof(TChange));

    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _path;
    private readonly SemaphoreSlim _pathGate;

    private JsonFileSyncReplica(string path, string replicaId, JsonSerializerOptions jsonOptions)
    {
        _path = Path.GetFullPath(path);
        ReplicaId = replicaId;
        _jsonOptions = jsonOptions;
        _pathGate = SyncJournalGates.For(_path);
    }

    /// <inheritdoc />
    public string ReplicaId { get; }

    /// <summary>Creates a replica bound to the stable identity from <paramref name="idProvider"/>.</summary>
    /// <returns>A replica after validating any existing journal identity.</returns>
    public static async Task<JsonFileSyncReplica<TChange>> CreateAsync(
        string path,
        IDeviceReplicaIdProvider idProvider,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(idProvider);
        var replicaId = await idProvider.GetReplicaIdAsync(cancellationToken).ConfigureAwait(false);
        ArgumentException.ThrowIfNullOrWhiteSpace(replicaId);

        // The identifier is persisted in the journal and compared on every load; one JSON cannot carry
        // exactly (an unpaired surrogate becomes U+FFFD) would make the journal unreadable by its owner.
        JsonRoundTripIdentity.ThrowIfIllFormedText(replicaId, nameof(idProvider));
        var options = jsonOptions is null
            ? new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }
            : new JsonSerializerOptions(jsonOptions);
        options.Converters.Add(new SyncCrdtJsonConverterFactory());
        var replica = new JsonFileSyncReplica<TChange>(path, replicaId, options);
        var existing = await replica.WithStateAsync(static state => state, cancellationToken).ConfigureAwait(false);
        if (existing is not null && !string.Equals(existing.ReplicaId, replicaId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Replica journal belongs to '{existing.ReplicaId}', not '{replicaId}'.");
        }

        return replica;
    }

    /// <inheritdoc />
    public Task<SyncChange<TChange>> AppendLocalAsync(
        TChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return MutateAsync(state =>
        {
            if (state.NextOriginSequence == long.MaxValue)
            {
                throw new InvalidOperationException("The local origin sequence is exhausted.");
            }

            state.NextOriginSequence++;
            var entry = new SyncChange<TChange>(
                $"{ReplicaId}:{state.NextOriginSequence}",
                ReplicaId,
                state.NextOriginSequence,
                change);
            state.Entries.Add(entry);
            return entry;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ChangeSet<TChange>> ReadChangesAsync(
        SyncCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!string.Equals(checkpoint.ReplicaId, ReplicaId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Checkpoint must identify this source replica.", nameof(checkpoint));
        }

        return WithStateAsync(state =>
        {
            var current = state ?? NewState();
            if (checkpoint.Position < 0 || checkpoint.Position > current.Entries.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(checkpoint), checkpoint.Position, "Position is outside the journal.");
            }

            var changes = current.Entries.Skip(checked((int)checkpoint.Position)).ToArray();
            return new ChangeSet<TChange>(ReplicaId, checkpoint.Position, current.Entries.Count, changes);
        }, cancellationToken)!;
    }

    /// <inheritdoc />
    public Task<int> ApplyAsync(ChangeSet<TChange> changeSet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ValidateChangeSet(changeSet);
        return MutateAsync(state =>
        {
            var previous = state.Checkpoints.GetValueOrDefault(changeSet.SourceReplicaId);
            if (changeSet.FromPosition > previous)
            {
                throw new InvalidOperationException(
                    $"Delta gap for '{changeSet.SourceReplicaId}': expected position {previous}, received {changeSet.FromPosition}.");
            }

            var known = state.Entries.ToDictionary(entry => entry.ChangeId, StringComparer.Ordinal);
            var applied = 0;
            foreach (var change in changeSet.Changes)
            {
                ValidateChange(change);
                if (known.TryGetValue(change.ChangeId, out var existing))
                {
                    // Deduplication is by identifier, so an identifier must keep one meaning. A journal
                    // restored past a shipped append reissues the identifier with new content; keeping
                    // either version silently would diverge the replicas permanently.
                    if (!HasSameContent(existing, change))
                    {
                        throw new InvalidOperationException(
                            $"Change '{change.ChangeId}' was reissued with different content; its origin journal was "
                            + "rolled back or restored after the change was shipped.");
                    }

                    continue;
                }

                if (string.Equals(change.OriginReplicaId, ReplicaId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Peer delta contains unknown change '{change.ChangeId}' attributed to this replica.");
                }

                known.Add(change.ChangeId, change);
                state.Entries.Add(change);
                applied++;
            }

            state.Checkpoints[changeSet.SourceReplicaId] = Math.Max(previous, changeSet.ToPosition);
            return applied;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SyncCheckpoint> LoadCheckpointAsync(
        string peerReplicaId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerReplicaId);
        return WithStateAsync(
            state => new SyncCheckpoint(peerReplicaId, state?.Checkpoints.GetValueOrDefault(peerReplicaId) ?? 0),
            cancellationToken)!;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncChange<TChange>>> ReadAllAsync(CancellationToken cancellationToken = default) =>
        WithStateAsync<IReadOnlyList<SyncChange<TChange>>>(
            state => state?.Entries.ToArray() ?? [],
            cancellationToken)!;

    private async Task<TResult> MutateAsync<TResult>(
        Func<ReplicaState, TResult> mutation,
        CancellationToken cancellationToken)
    {
        await _pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false) ?? NewState();
            var result = mutation(state);
            await SaveUnsafeAsync(state, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _pathGate.Release();
        }
    }

    private async Task<TResult?> WithStateAsync<TResult>(
        Func<ReplicaState?, TResult> projection,
        CancellationToken cancellationToken)
    {
        await _pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return projection(await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _pathGate.Release();
        }
    }

    private async Task<ReplicaState?> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var content = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        EnsureWrittenForThisChangeType(content);
        var state = JsonSerializer.Deserialize<ReplicaState>(content, _jsonOptions)
            ?? throw new InvalidDataException($"Sync journal '{_path}' is empty.");
        if (!string.Equals(state.ReplicaId, ReplicaId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Sync journal '{_path}' has a different replica identity.");
        }

        ValidateLoadedState(state);
        EnsureFaithful(content, state);

        return state;
    }

    private async Task SaveUnsafeAsync(ReplicaState state, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(state, _jsonOptions);
        EnsureReadable(content, state);

        // rename(2) replaces a symbolic link rather than writing through it; replacing the link's
        // target keeps every spelling the gate coordinates on one physical journal.
        var physicalPath = FileCoordinationKey.ResolvePhysicalPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
        var temporaryPath = $"{physicalPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, physicalPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    // The replace installs the only copy of the journal, so a payload the serializer can write but
    // not read back as written (an interface or a constructor parameter without a property, or content
    // the write alters) must be refused here, while the previous file is still authoritative, rather
    // than discovered by the next load.
    private void EnsureReadable(byte[] content, ReplicaState written)
    {
        try
        {
            var reloaded = JsonSerializer.Deserialize<ReplicaState>(content, _jsonOptions)
                ?? throw new InvalidDataException("The serialized sync journal is empty.");
            ValidateLoadedState(reloaded);
            EnsureFaithful(content, reloaded);
            EnsureReloadsAsWritten(written, reloaded);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException
            or InvalidDataException or ArgumentException)
        {
            throw new NotSupportedException(
                "The sync payload does not round-trip through the configured JsonSerializerOptions, so the journal "
                + "was left unchanged.", error);
        }
    }

    // Reading back without an error, and re-serializing to the same file, prove only that the reload is a
    // fixed point: a lone surrogate (U+FFFD), a write-side key policy or ignore condition, and an object
    // member that reloads as a bare JSON document (a CRDT's merge semantics, a private counter) all pass
    // both. The reload must therefore be the state that was written, compared value by value.
    private void EnsureReloadsAsWritten(ReplicaState written, ReplicaState reloaded)
    {
        var difference = JsonRoundTripIdentity.FindDifference(written, reloaded, _jsonOptions);
        if (difference is not null)
        {
            throw new NotSupportedException($"The sync journal does not reload as written: {difference}.");
        }
    }

    // Read from the raw document, before the content is deserialized as this change type: a foreign journal
    // must be refused as foreign, not surface as whatever its content does when read as the wrong type.
    private void EnsureWrittenForThisChangeType(byte[] content)
    {
        using var persisted = JsonDocument.Parse(content, DocumentOptions());
        const string member = nameof(ReplicaState.ChangeType);
        var name = _jsonOptions.PropertyNamingPolicy?.ConvertName(member) ?? member;
        string? written = null;
        if (persisted.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in persisted.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && (string.Equals(property.Name, name, StringComparison.Ordinal)
                        || (_jsonOptions.PropertyNameCaseInsensitive
                            && string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))))
                {
                    written = property.Value.GetString();
                }
            }
        }

        if (!string.Equals(written, s_changeType, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Sync journal '{_path}' was written for change type '{written ?? "(unrecorded)"}', not '{s_changeType}'; "
                + "reading it as another type would reinterpret and rewrite acknowledged changes.");
        }
    }

    private JsonDocumentOptions DocumentOptions() => new()
    {
        AllowTrailingCommas = _jsonOptions.AllowTrailingCommas,
        CommentHandling = _jsonOptions.ReadCommentHandling,
        MaxDepth = _jsonOptions.MaxDepth,
    };

    // The next mutation rewrites the whole journal from the loaded state, so content these options cannot
    // represent (a journal of this change type written with other options: unmapped members are skipped
    // and missing ones defaulted) would be erased by the rewrite. Re-serializing the loaded state must
    // therefore reproduce every persisted member and value; it may add members the file omits, and it may
    // differ in renderings that vary by process rather than by content (see IsRetained).
    private void EnsureFaithful(byte[] content, ReplicaState state)
    {
        var documentOptions = DocumentOptions();
        using var persisted = JsonDocument.Parse(content, documentOptions);
        using var representable = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(state, _jsonOptions), documentOptions);
        if (!IsRetained(persisted.RootElement, representable.RootElement, typeof(ReplicaState)))
        {
            throw new InvalidDataException(
                $"Sync journal '{_path}' holds content that {typeof(TChange).Name} cannot represent with these options; "
                + "it was written with other options, and rewriting it would discard acknowledged changes.");
        }
    }

    // Whether an array's order is content depends on the type the reader declares for it, not on the
    // array: a list keeps its order through deserialization, so a reader that reorders a persisted list
    // would rewrite it in its own order. Only this change type's own journals reach here (the writer's
    // identity is checked first), so the declared type is also the writer's. Order is
    // therefore excused only below a declared collection with no defined order (a hash-ordered set). A
    // member whose declared type is unknown here (a custom converter, such as the built-in CRDT state,
    // whose reload rebuilds insertion-ordered collections in persisted order, or a polymorphic value) is
    // compared in order.
    private bool IsRetained(JsonElement persisted, JsonElement representable, Type? declared)
    {
        var shape = Describe(declared);
        switch (persisted.ValueKind)
        {
            case JsonValueKind.Object:
                if (representable.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                foreach (var member in persisted.EnumerateObject())
                {
                    if (representable.TryGetProperty(member.Name, out var counterpart)
                        ? !IsRetained(member.Value, counterpart, MemberType(shape, member.Name))
                        : member.Value.ValueKind != JsonValueKind.Null)
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Array:
                if (representable.ValueKind != JsonValueKind.Array
                    || representable.GetArrayLength() != persisted.GetArrayLength())
                {
                    return false;
                }

                var elementType = shape?.Kind == JsonTypeInfoKind.Enumerable ? shape.ElementType : null;
                return IsRetainedInOrder(persisted, representable, elementType)
                    || (HasNoDefinedOrder(declared) && IsRetainedInAnyOrder(persisted, representable, elementType));
            case JsonValueKind.String:
                return JsonElement.DeepEquals(persisted, representable) || IsSameInstantInLocalZone(persisted, representable);
            default:
                return JsonElement.DeepEquals(persisted, representable);
        }
    }

    private JsonTypeInfo? Describe(Type? declared)
    {
        if (declared is null)
        {
            return null;
        }

        var info = _jsonOptions.GetTypeInfo(Nullable.GetUnderlyingType(declared) ?? declared);
        return info.Kind == JsonTypeInfoKind.None ? null : info;
    }

    private Type? MemberType(JsonTypeInfo? shape, string name)
    {
        switch (shape?.Kind)
        {
            case JsonTypeInfoKind.Dictionary:
                return shape.ElementType;
            case JsonTypeInfoKind.Object:
                var property = shape.Properties.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
                    ?? (_jsonOptions.PropertyNameCaseInsensitive
                        ? shape.Properties.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                        : null);
                return property is null || property.CustomConverter is not null ? null : property.PropertyType;
            default:
                return null;
        }
    }

    private static bool HasNoDefinedOrder(Type? declared)
    {
        if (declared is null || !declared.IsGenericType)
        {
            return false;
        }

        var definition = declared.GetGenericTypeDefinition();
        return definition == typeof(HashSet<>)
            || definition == typeof(ISet<>)
            || definition == typeof(IReadOnlySet<>)
            || definition == typeof(System.Collections.Immutable.ImmutableHashSet<>)
            || definition == typeof(System.Collections.Immutable.IImmutableSet<>);
    }

    private bool IsRetainedInOrder(JsonElement persisted, JsonElement representable, Type? elementType)
    {
        using var left = persisted.EnumerateArray();
        using var right = representable.EnumerateArray();
        while (left.MoveNext() && right.MoveNext())
        {
            if (!IsRetained(left.Current, right.Current, elementType))
            {
                return false;
            }
        }

        return true;
    }

    // A hash-ordered collection (ImmutableHashSet<string>) enumerates in an order that string-hash
    // randomization makes different in every process, so the journal another process wrote lists the
    // same elements in another order. Nothing is lost when every persisted element still has its own
    // counterpart. Only reached for a declared collection with no defined order (see IsRetained).
    // Identical spellings are paired through a lookup first, so a large reordered set costs linear time;
    // only elements whose spelling changed are paired by the semantic comparison.
    private bool IsRetainedInAnyOrder(JsonElement persisted, JsonElement representable, Type? elementType)
    {
        var candidates = representable.EnumerateArray().ToArray();
        var used = new bool[candidates.Length];
        var bySpelling = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Length; i++)
        {
            var spelling = candidates[i].GetRawText();
            if (!bySpelling.TryGetValue(spelling, out var slots))
            {
                bySpelling.Add(spelling, slots = new Queue<int>());
            }

            slots.Enqueue(i);
        }

        var unmatched = new List<JsonElement>();
        foreach (var element in persisted.EnumerateArray())
        {
            if (bySpelling.TryGetValue(element.GetRawText(), out var slots) && slots.TryDequeue(out var slot))
            {
                used[slot] = true;
            }
            else
            {
                unmatched.Add(element);
            }
        }

        foreach (var element in unmatched)
        {
            var match = -1;
            for (var i = 0; i < candidates.Length && match < 0; i++)
            {
                if (!used[i] && IsRetained(element, candidates[i], elementType))
                {
                    match = i;
                }
            }

            if (match < 0)
            {
                return false;
            }

            used[match] = true;
        }

        return true;
    }

    // A local-kind DateTime persists with the writing process's zone offset and re-serializes with this
    // process's, so a device that changed zone spells the same instant differently. Only that rendering
    // is excused: the instant must be identical, so a value the reload would move is still refused.
    private static bool IsSameInstantInLocalZone(JsonElement persisted, JsonElement representable) =>
        persisted.TryGetDateTimeOffset(out var written)
        && representable.TryGetDateTimeOffset(out var reloaded)
        && written.UtcTicks == reloaded.UtcTicks
        && reloaded.Offset == TimeZoneInfo.Local.GetUtcOffset(reloaded.UtcDateTime);

    // Compared through the journal's own serialization: it is the representation both replicas persist
    // and reload, and it is defined for every payload type the store accepts.
    private bool HasSameContent(SyncChange<TChange> existing, SyncChange<TChange> incoming) =>
        JsonSerializer.SerializeToUtf8Bytes(existing.Value, _jsonOptions).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(incoming.Value, _jsonOptions));

    private ReplicaState NewState() => new() { ReplicaId = ReplicaId, ChangeType = s_changeType };

    private static void ValidateChangeSet(ChangeSet<TChange> changeSet)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changeSet.SourceReplicaId);
        ArgumentOutOfRangeException.ThrowIfNegative(changeSet.FromPosition);
        if (changeSet.ToPosition < changeSet.FromPosition)
        {
            throw new ArgumentException("Delta end position cannot precede its start.", nameof(changeSet));
        }

        ArgumentNullException.ThrowIfNull(changeSet.Changes);

        // The checkpoint advances to ToPosition, so a delta that carries fewer changes than its range
        // would record history it never delivered, and no later session would resend it.
        if (changeSet.ToPosition - changeSet.FromPosition != changeSet.Changes.Count)
        {
            throw new ArgumentException(
                $"Delta for positions {changeSet.FromPosition}..{changeSet.ToPosition} carries {changeSet.Changes.Count} changes.",
                nameof(changeSet));
        }

        // A matching count is not one change per position: a repeated identifier is deduplicated on
        // apply, so the positions it occupies would be recorded as delivered while their changes never were.
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changeSet.Changes)
        {
            ValidateChange(change);
            if (!identifiers.Add(change.ChangeId))
            {
                throw new ArgumentException(
                    $"Delta for positions {changeSet.FromPosition}..{changeSet.ToPosition} carries change '{change.ChangeId}' more than once.",
                    nameof(changeSet));
            }
        }
    }

    private static void ValidateChange(SyncChange<TChange> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.ChangeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.OriginReplicaId);
        ArgumentOutOfRangeException.ThrowIfLessThan(change.OriginSequence, 1);
        ArgumentNullException.ThrowIfNull(change.Value);
        var expectedId = change.OriginReplicaId + ":" + change.OriginSequence.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(change.ChangeId, expectedId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Change identifier must match its origin and sequence.", nameof(change));
        }
    }

    private void ValidateLoadedState(ReplicaState state)
    {
        if (state.Entries is null || state.Checkpoints is null)
        {
            throw new InvalidDataException("Sync journal entries and checkpoints must not be null.");
        }

        foreach (var (peer, position) in state.Checkpoints)
        {
            if (string.IsNullOrWhiteSpace(peer) || position < 0)
            {
                throw new InvalidDataException("Sync journal contains an invalid peer checkpoint.");
            }
        }

        if (state.NextOriginSequence < 0)
        {
            throw new InvalidDataException("The persisted local origin sequence is negative.");
        }

        var known = new HashSet<string>(StringComparer.Ordinal);
        long greatestOwnSequence = 0;
        foreach (var change in state.Entries)
        {
            try
            {
                ValidateChange(change);
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException("Sync journal contains an invalid change envelope.", error);
            }
            if (!known.Add(change.ChangeId))
            {
                throw new InvalidDataException($"Sync journal contains duplicate change '{change.ChangeId}'.");
            }

            if (string.Equals(change.OriginReplicaId, ReplicaId, StringComparison.Ordinal))
            {
                greatestOwnSequence = Math.Max(greatestOwnSequence, change.OriginSequence);
            }
        }

        if (state.NextOriginSequence < greatestOwnSequence)
        {
            throw new InvalidDataException(
                $"Persisted origin sequence {state.NextOriginSequence} precedes retained local change {greatestOwnSequence}.");
        }
    }

    private sealed class ReplicaState
    {
        public string ReplicaId { get; set; } = string.Empty;

        public string ChangeType { get; set; } = string.Empty;

        public long NextOriginSequence { get; set; }

        public List<SyncChange<TChange>> Entries { get; set; } = [];

        [JsonConverter(typeof(VerbatimCounterMapConverter))]
        public Dictionary<string, long> Checkpoints { get; set; } = new(StringComparer.Ordinal);
    }
}

/// <summary>Names a change type the same way in every process and assembly version.</summary>
/// <remarks>Built from namespace-qualified type names, recursively over generic arguments and array
/// elements, without assembly versions, so upgrading the application does not orphan its journals.</remarks>
internal static class ChangeTypeIdentity
{
    public static string Of(Type type)
    {
        if (type.IsArray)
        {
            return Of(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var definition = type.GetGenericTypeDefinition();
        return (definition.FullName ?? definition.Name) + "[" + string.Join(",", type.GetGenericArguments().Select(Of)) + "]";
    }
}

/// <summary>Holds one in-process gate per physical sync journal.</summary>
/// <remarks>Non-generic on purpose: a static field of <see cref="JsonFileSyncReplica{TChange}"/> exists
/// once per closed type, so replicas with different change types over one journal would get different
/// gates and their load-mutate-replace cycles would discard each other's acknowledged appends.</remarks>
internal static class SyncJournalGates
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_gates = new(StringComparer.Ordinal);

    public static SemaphoreSlim For(string fullPath) =>
        s_gates.GetOrAdd(FileCoordinationKey.For(fullPath), static _ => new SemaphoreSlim(1, 1));
}

/// <summary>Derives the in-process coordination key for a file from its physical location.</summary>
/// <remarks><see cref="Path.GetFullPath(string)"/> is lexical: it neither follows symbolic-link
/// ancestors (<c>/tmp</c> to <c>/private/tmp</c>) nor folds case, so two spellings of one file would get
/// different gates and their load-mutate-replace cycles would discard each other's acknowledged writes.
/// The key resolves every existing link, then folds Unicode normalization and case on every volume:
/// over-coordinating two distinct files on a case-sensitive volume only serializes them, whereas
/// under-coordinating one file loses data.</remarks>
internal static class FileCoordinationKey
{
    private const int MaxLinkDepth = 40;

    // APFS and HFS+ compare names by full Unicode case folding of the decomposed form, so "ß", "ẞ" and
    // "ss" are one entry, and "İ" matches "i" + U+0307 only after decomposition. Lower-then-upper
    // casing of the decomposed form reaches the same class as the simple foldings; these are the
    // folds it cannot reach because they expand one character into several (CaseFolding.txt status F
    // entries that survive decomposition).
    private static readonly FrozenDictionary<int, string> s_expandingFolds = new Dictionary<int, string>
    {
        [0x00DF] = "ss",
        [0x1E9E] = "ss",
        [0x0149] = "\u02BCn",
        [0x0587] = "\u0565\u0582",
        [0x1E9A] = "a\u02BE",
        [0xFB00] = "ff",
        [0xFB01] = "fi",
        [0xFB02] = "fl",
        [0xFB03] = "ffi",
        [0xFB04] = "ffl",
        [0xFB05] = "st",
        [0xFB06] = "st",
        [0xFB13] = "\u0574\u0576",
        [0xFB14] = "\u0574\u0565",
        [0xFB15] = "\u0574\u056B",
        [0xFB16] = "\u057E\u0576",
        [0xFB17] = "\u0574\u056D",
    }.ToFrozenDictionary();

    public static string For(string fullPath) => Fold(ResolveLinks(fullPath));

    /// <summary>Returns the path with every existing symbolic link resolved, the file it physically names.</summary>
    public static string ResolvePhysicalPath(string fullPath) => ResolveLinks(fullPath);

    private static string Fold(string path)
    {
        var decomposed = path.Normalize(NormalizationForm.FormD);
        var expanded = new StringBuilder(decomposed.Length);
        foreach (var rune in decomposed.EnumerateRunes())
        {
            if (s_expandingFolds.TryGetValue(rune.Value, out var expansion))
            {
                expanded.Append(expansion);
            }
            else
            {
                expanded.Append(rune.ToString());
            }
        }

        return expanded.ToString().ToLowerInvariant().ToUpperInvariant().Normalize(NormalizationForm.FormD);
    }

    // Resolves the way the kernel does (realpath): components are walked left to right, a link's target
    // text is spliced in place of the link and walked the same way, and ".." steps to the parent of the
    // physical directory reached so far. A lexical collapse (Path.GetFullPath) of a target such as
    // "dirlink/../x" would instead drop the linked directory and name a file the kernel never opens.
    private static string ResolveLinks(string fullPath)
    {
        var current = Path.GetPathRoot(fullPath) ?? string.Empty;
        var pending = new List<string>(SplitComponents(fullPath[current.Length..]));
        pending.Reverse();
        var links = 0;
        while (pending.Count > 0)
        {
            var component = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            if (component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var next = Path.Combine(current, component);
            var target = new FileInfo(next).LinkTarget;
            if (target is null)
            {
                current = next;
                continue;
            }

            if (++links > MaxLinkDepth)
            {
                throw new IOException($"Too many levels of symbolic links while resolving '{fullPath}'.");
            }

            // A relative target is walked from the link's own (already physical) directory.
            var targetRoot = Path.GetPathRoot(target) ?? string.Empty;
            if (targetRoot.Length > 0)
            {
                current = Path.IsPathFullyQualified(target) ? targetRoot : Path.GetPathRoot(current) ?? targetRoot;
            }

            var spliced = SplitComponents(target[targetRoot.Length..]);
            for (var i = spliced.Length - 1; i >= 0; i--)
            {
                pending.Add(spliced[i]);
            }
        }

        return current;
    }

    private static string[] SplitComponents(string relativePath) => relativePath.Split(
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
        StringSplitOptions.RemoveEmptyEntries);
}
