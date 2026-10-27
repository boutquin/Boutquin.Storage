# Local-first sync reference core

Each device owns a durable append-only journal. A `SyncCheckpoint` records how far one device has
consumed a peer journal. `SyncEngine` reads both deltas before applying either. Each
`JsonFileSyncReplica` then atomically persists unseen change identifiers and the peer checkpoint in
one flushed JSON replacement. Before replacing, the replica reads the new content back with the
configured options and refuses, with `NotSupportedException`, any payload that serializes but does
not read back as the value appended: one that cannot be deserialized, text holding an unpaired UTF-16
surrogate (JSON writes it as U+FFFD), and content a write-side option alters (a `DictionaryKeyPolicy`
renaming payload keys, a `DefaultIgnoreCondition` dropping a value the reader then defaults,
`IgnoreReadOnlyProperties` dropping get-only state). The reload is compared with the state being
written, member by member, so a failure before the atomic replace leaves the old file and checkpoint
authoritative. A collection member declared by an interface (`IReadOnlyList<T>`,
`IReadOnlyDictionary<TKey,TValue>`) may reload as another implementation of it when its contents read
back intact. A replica identifier that is not well-formed UTF-16 is refused with `ArgumentException`
by `CreateAsync` and `FixedDeviceReplicaIdProvider`, because it would persist as different text and no
later load could match it. If the caller cannot tell whether replacement completed (for example, process loss at
the rename boundary), it must reopen the replica before retrying; the reopened journal supplies the
committed local sequence and change identifiers, so a new append cannot overwrite committed state.
Local append is at-least-once: retrying `AppendLocalAsync` after a lost acknowledgement creates a new
envelope and may duplicate the logical operation. Reopening does not supply request deduplication.
This API provides no exactly-once local-append guarantee. Applications needing that guarantee require
a store that atomically couples a request identity with the append. Peer exchange retries are different: they resend the same envelope
identifiers, which the receiving replica deduplicates. Deduplication requires an identifier to keep
one meaning: a known identifier that arrives with different content is rejected with
`InvalidOperationException`. That is how a journal restored from a backup, or reverted by
directory-entry loss, is detected after it reissues an identifier a peer already holds; `SyncEngine`
therefore still delivers echoes of the destination's own changes for this comparison, without
counting them as sent.
Peer deltas may replay or overlap an acknowledged range; deduplication makes that safe. A delta that
starts after the durable peer checkpoint is rejected before mutation because accepting it would skip
missing history. Loaded journals also validate unique change identifiers and ensure the next local
sequence is at least the greatest retained locally originated sequence; sequence exhaustion fails
instead of wrapping and reusing an identifier. Envelopes must use the canonical identifier
`<originReplicaId>:<positiveOriginSequence>`; mismatches are rejected before checkpoint changes.
Persisted checkpoint names must be nonblank and their positions nonnegative.
Each journal records the change type that wrote it. Reopening it as another change type, or opening a
journal without that record, fails with `InvalidDataException` before any content is read, so a
journal is never reinterpreted and rewritten by a foreign type. A journal of the same change type is
still accepted when it differs from this process's serialization only in renderings that vary by
process: the element order of a hash-ordered set, or the zone offset of a local `DateTime`.

`IConflictResolver<TChange,TState>` is separate from transport. The built-in key/value resolver
orders by logical timestamp, replica identifier, and origin sequence, so arrival order cannot change
the result. Deletions remain in the durable change history and suppress older values during materialization;
the reference store does not garbage-collect acknowledged history. Applications can inject another
deterministic resolver. `GCounterSyncAdapter` and `ORSetSyncAdapter<T>` merge the existing CRDT states.
The JSON replica registers converters for those CRDT payloads, preserving per-node counters,
grow-only set (`GSet<T>`) elements and, for observed-remove sets, add tags, tombstones, and
tag-allocation counters in a versioned payload. Payloads declared by their family interface
(`IGCounter`, `IGSet<T>`, `IORSet<T>`) are persisted the same way and reload as the built-in type.
Cold reopen therefore retains merge semantics; persisting only the public `Value` would lose data.
A `PNCounter` payload, declared as `PNCounter` or `IPNCounter`, alone or nested in a change, is
refused with `NotSupportedException` before the journal is rewritten, because its positive and
negative component counters are not capturable; carry it as two `GCounter` values. A payload
declared as `ICrdt<T>` is refused the same way, because the declared type does not identify which
family to reload.
A payload declared as `object`, alone or as a member of a change (including an element of
a list or a hash-ordered set), is refused the same way
when it would reload as a bare JSON document rather than the type that was appended, because its
type or private state (a CRDT's merge semantics, a counter no property exposes) would be lost.
Object-declared strings, numbers and booleans are also refused: they reload as `JsonElement`,
changing their runtime types. An explicit `JsonElement` payload retains its type and is accepted.
Caller-supplied converters take precedence and must preserve the same complete causal state.
Set elements must round-trip through the configured serializer with stable equality and hash codes.

The JSON store coordinates instances in one process, keyed on the physical journal: spellings that
differ only by symbolic-link ancestors or letter case, and replicas opened with different change
types, share one gate; the first change type to write the journal owns it and the others are refused. `FileDeviceReplicaIdProvider` writes its identifier under a temporary name
and publishes it with an exclusive, non-overwriting move, so concurrent first-run providers in any
process all return the one identifier that is persisted, and a cancelled first run leaves no file. Production systems need an embedded
transactional database or metadata service for cross-process writers and durable directory-entry
replacement. Payloads must be serializable with the configured `JsonSerializerOptions`.

Run the restart/convergence demo with `dotnet run --project src/LocalFirstSyncDemo -c Release`.
Run the resolver benchmark with `dotnet run --project benchmarks/Sync -c Release`.
