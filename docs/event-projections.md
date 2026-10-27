# Event projections and replay

`ProjectionRunner<TEvent,TState>` folds a positional event log into an immutable state and saves that state together with the last processed position. Reopening a runner with the same processor ID and checkpoint store resumes at the next event. `ProjectionRebuilder<TEvent,TState>` folds from the initial state without changing the live checkpoint, so a rebuilt model can be checked before switching readers.

Run the executable counter and searchable read-model examples:

```sh
dotnet run --project src/Samples/Boutquin.Storage.Samples.csproj -- projections
```

The counter processes a prefix, reconstructs the runner against a file checkpoint, consumes the tail, and compares its result with a full rebuild. The document projection replaces documents by ID and searches its latest state. This small model uses a linear substring scan; indexed retrieval is a separate consumer choice.

Implement `IProjection<TEvent,TState>` with an `InitialState` and a pure `Apply` function. Use immutable state: mutating the old state makes failure recovery and replay comparisons unreliable. The runner borrows the event log, checkpoint store, codec delegates, and optional external projector; it does not dispose them. Give each projection/log/codec combination its own stable processor ID, and keep one active owner per ID.

For each input event, the runner computes and encodes the next state, calls an optional `IProjector<TEvent>`, and saves the checkpoint. A checkpoint save failure propagates. The next call reloads the previously acknowledged state; it never advances from an uncommitted in-memory result. Invalid offsets and gaps in the input log fail closed.

An external projector must atomically record its effect and deduplication position. A crash after the external effect but before checkpoint save causes the handler to receive the same position again. The runner provides at-least-once delivery to that handler; exactly-once external effects depend on the handler's transactional or idempotent contract. Checkpoint persistence does not create a transaction with an unrelated database or service.

`InMemoryReadModelStore<TKey,TValue>` demonstrates per-key deduplication. It atomically applies only newer positions and keeps deletion watermarks so a replay cannot resurrect an older row. It is volatile: reconstruct it by replay after restart, and do not reuse an old durable checkpoint with an empty volatile model. Its snapshot copies the dictionary but shares values, which should be immutable.

Saving after every event makes the recovery boundary easy to understand and test, at the cost of an fsync per event with `FileCheckpointStore`. Each encoded checkpoint contains the full state, so this approach suits small aggregate/read models. Large indexes should use a transactional external projector and a compact fold state. Catch-up inherits the input log's snapshot versus live enumeration semantics; the supplied in-memory log snapshots its available tail at enumeration time.

Validation covers file-checkpoint restart, replay equality, empty streams, cancellation, corrupt offsets, gapped input, failure between output and checkpoint, stale updates, and delete/replay behavior.
