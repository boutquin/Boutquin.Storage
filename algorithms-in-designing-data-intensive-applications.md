# Algorithms in "Designing Data-Intensive Applications" (2nd Edition)

"Designing Data-Intensive Applications" (2nd edition) by [Martin Kleppmann](https://martin.kleppmann.com/) and [Chris Riccomini](https://cnr.sh/) is a comprehensive guide on building reliable, scalable, and maintainable systems. While the book doesn't provide an exhaustive list of algorithms in a traditional textbook sense, it discusses many key algorithms and concepts relevant to data-intensive applications. Here is an extensive list of the primary algorithms, techniques, and concepts covered in the book.

Algorithms implemented in this project are marked with **[Implemented]**.

Entries tagged **[Implemented (Reference)]** live in the
`Boutquin.Storage.Distributed` assembly — an in-repo, project-reference-only home for
the distributed-systems primitives (Raft, replication, MVCC / Snapshot Isolation, partitioning, CRDTs,
gossip, Merkle anti-entropy). The implementations are faithful to the DDIA chapter but
have module-specific production limits, such as missing network transports, replicated failover,
dynamic membership, predicate locks, and phi-accrual failure detection. See
[`src/Distributed/README.md`](src/Distributed/README.md) for the full scope statement and
pointers to production alternatives. Entries without the `(Reference)` suffix — LSM,
SSTable, WAL, columnar, Bloom filters, source generator, CDC, events, schemas,
checkpointing, object store — remain production-strength under `Boutquin.Storage.Domain`
and `Boutquin.Storage.Infrastructure`.

## Chapter 3: Data Models and Query Languages
- B-trees **[Implemented]** — `IBTree` / `BTree`
- Log-structured merge-trees (LSM-trees) **[Implemented]** — `ILsmStorageEngine` / `LsmStorageEngine`
- Merkle trees **[Implemented (Reference)]** — `IMerkleTree` / `MerkleTree` (`Boutquin.Storage.Distributed`)
- Event Sourcing **[Implemented]** — `IEventLog<TEvent>` / `AppendOnlyEventLog<TEvent>`, `InMemoryEventLog<TEvent>`

## Chapter 4: Storage and Retrieval
- B-trees **[Implemented]** — `IBTree` / `BTree`
- Log-structured merge-trees (LSM-trees) **[Implemented]** — `ILsmStorageEngine` / `LsmStorageEngine`
- Hash indexes **[Implemented]** — `IHashAlgorithm` / `Fnv1aHash`, `Murmur3`, `XxHash32`
- SSTables (Sorted String Tables) **[Implemented]** — `ISortedStringTable` / `SortedStringTable`
- Bloom filters **[Implemented]** — `IBloomFilter` / `BloomFilter`
- WriteAheadLog **[Implemented]** — `IWriteAheadLog` / `WriteAheadLog`
- Append-only file storage **[Implemented]** — `AppendOnlyFileStorageEngine`
- Log-segmented storage **[Implemented]** — `LogSegmentedStorageEngine`
- MemTables (in-memory balanced trees) **[Implemented]** — `IRedBlackTree` / `RedBlackTree`
- Compaction strategies **[Implemented]** — `ICompactionStrategy` / `FullCompactionStrategy`, `SizeTieredCompactionStrategy`
- Concurrent key-value store **[Implemented]** — `IKeyValueStore` / `ConcurrentKeyValueStore` (thread-safe caching)

## Chapter 5: Encoding and Evolution
- Schema evolution **[Implemented]** — `ISchema`, `ISchemaRegistry` / `InMemorySchemaRegistry`
- Schema compatibility checking **[Implemented]** — `ISchemaCompatibilityChecker` / `AvroFieldLevelCompatibilityChecker`, `TaggedWireCompatibilityChecker`, `ProtobufTagCompatibilityChecker`
- Versioned serialization **[Implemented]** — `IAvroStyleVersionedSerializer<T>` / `JsonVersionedSerializer<T>`, `TaggedWireVersionedSerializer<T>`; `IProtobufSerializer<T>` / `ProtoSerializer<T>`
- Data serialization formats (e.g., JSON, XML, Protocol Buffers, Thrift, Avro)
- Binary serialization **[Implemented]** — `BinaryEntrySerializer`
- Text serialization **[Implemented]** — `CsvEntrySerializer`

## Chapter 6: Replication
- Single-leader replication **[Implemented (Reference)]** — `ISingleLeaderReplication` / `SingleLeaderReplication` (`Boutquin.Storage.Distributed`)
- Multi-leader replication **[Implemented (Reference)]** — `IMultiLeaderReplication` / `MultiLeaderReplicaSet` (`Boutquin.Storage.Distributed`)
- Leaderless replication **[Implemented (Reference)]** — `LeaderlessReplicaSet` (`Boutquin.Storage.Distributed`)
- Quorum-based replication **[Implemented (Reference)]** — `IQuorumReplication` / `QuorumReplication` (`Boutquin.Storage.Distributed`)
- Conflict-free replicated data types (CRDTs) **[Implemented (Reference)]** — `IGCounter` / `GCounter`, `IPNCounter` / `PNCounter`, `IGSet` / `GSet`, `IORSet` / `ORSet` (`Boutquin.Storage.Distributed`)
- Gossip protocols **[Implemented (Reference)]** — `IGossipProtocol` / `GossipProtocol` (`Boutquin.Storage.Distributed`)
- Merkle trees (anti-entropy) **[Implemented (Reference)]** — `IMerkleTree` / `MerkleTree` (`Boutquin.Storage.Distributed`)
- Replication log **[Implemented (Reference)]** — `IReplicationLog` / `ReplicationLog` (`Boutquin.Storage.Distributed`)

## Chapter 7: Sharding
- Consistent hashing **[Implemented (Reference)]** — `IConsistentHashRing` / `ConsistentHashRing` (`Boutquin.Storage.Distributed`)
- Rendezvous hashing **[Implemented (Reference)]** — `IRendezvousHash` / `RendezvousHash` (`Boutquin.Storage.Distributed`)
- Range partitioning **[Implemented (Reference)]** — `IRangePartitioner` / `RangePartitioner` (`Boutquin.Storage.Distributed`)
- Hash partitioning **[Implemented (Reference)]** — `IPartitioner` / `HashPartitioner` (`Boutquin.Storage.Distributed`)
- Secondary indexes **[Implemented]** — `ISecondaryIndex` / `SecondaryIndex`

## Chapter 8: Transactions
- Two-phase commit protocol (2PC) **[Implemented (Reference)]** — `ITwoPhaseCommitCoordinator` / `TwoPhaseCommitCoordinator`, `DurableTransactionalResource` (`Boutquin.Storage.Distributed`)
- Three-phase commit protocol (3PC)
- Paxos
- Raft **[Implemented (Reference)]** — `IRaftNode` / `RaftNode`, `IRaftCluster` / `RaftCluster` (`Boutquin.Storage.Distributed`)
- Multi-version concurrency control (MVCC) **[Implemented (Reference)]** — `IMvccStore` / `MvccStore` (`Boutquin.Storage.Distributed`)
- Snapshot Isolation **[Implemented (Reference)]** — `ISnapshotIsolationStore` / `SnapshotIsolationStore` (`Boutquin.Storage.Distributed`); this is plain SI, not Serializable Snapshot Isolation

## Chapter 9: The Trouble with Distributed Systems
- Consensus algorithms (e.g., Paxos, Raft) **[Implemented (Reference)]** — `IRaftNode` / `RaftNode` (`Boutquin.Storage.Distributed`)
- Clock synchronization algorithms (e.g., NTP, PTP)
- Vector clocks **[Implemented (Reference)]** — `IVectorClock` / `VectorClock` (`Boutquin.Storage.Distributed`)
- Lamport timestamps **[Implemented (Reference)]** — `ILamportTimestamp` / `LamportTimestamp` (`Boutquin.Storage.Distributed`)
- Gossip protocols **[Implemented (Reference)]** — `IGossipProtocol` / `GossipProtocol` (`Boutquin.Storage.Distributed`)

## Chapter 10: Consistency and Consensus
- Paxos
- Raft **[Implemented (Reference)]** — `IRaftNode` / `RaftNode`, `IRaftCluster` / `RaftCluster` (`Boutquin.Storage.Distributed`)
- Viewstamped Replication
- Zab (ZooKeeper Atomic Broadcast)

## Chapter 11: Batch Processing
- External merge sorting **[Implemented]** — `IExternalSorter<T>` / `ExternalMergeSorter<T>`
- Hash shuffle partitioning **[Implemented]** — `IShufflePartitioner<T>` / `HashShufflePartitioner<T>`
- Directed acyclic graphs (DAGs) **[Implemented]** — `BatchJobGraph` / `BatchJobRunner`
- In-memory dataflow transformations **[Implemented]** — `MiniDataFrame<TRow>`
- Two-phase commit (for distributed transactions) **[Implemented (Reference)]** — `ITwoPhaseCommitCoordinator` / `TwoPhaseCommitCoordinator` (`Boutquin.Storage.Distributed`)
- Object stores **[Implemented]** — `IObjectStore` / `FileSystemObjectStore`, `InMemoryObjectStore`

## Chapter 12: Stream Processing
- Stream processing **[Implemented]** — `IStream<T>` / `EventLogStream<T>`, `IStreamProcessor<TIn,TOut>` / `AtLeastOnceProcessor`, `EffectivelyOnceProcessor`
- Distributed snapshot algorithms
- Stream joins **[Implemented]** — `IStreamJoin<TLeft,TRight,TOut>` / `StreamStreamJoin`, plus `StreamTableJoin` and `TableTableJoin`
- Time windowing algorithms **[Implemented]** — `IWindowAssigner` / `TumblingWindow`, `SlidingWindow`, `SessionWindow`; `WindowedAggregator`
- Change data capture **[Implemented]** — `IChangeDataCaptureSource<TKey,TValue>` / `EventLogCdcSource<TKey,TValue>`, `ObjectStoreCdcSource<TKey>`, `LsmCdcAdapter<TKey,TValue>`
- Checkpointing **[Implemented]** — `ICheckpointStore` / `FileCheckpointStore`, `InMemoryCheckpointStore`
- Append-only event log **[Implemented]** — `IEventLog<TEvent>` / `AppendOnlyEventLog<TEvent>`

## Chapter 13: A Philosophy of Streaming Systems
- Exactly-once semantics
- Idempotency
- Microbatching vs. per-event processing
- Dataflow programming models

## General Algorithms Discussed
- Hash functions **[Implemented]** — `Fnv1aHash`, `Murmur3`, `XxHash32`
- Sorting algorithms **[Implemented]** — `ExternalMergeSorter<T>`
- Data partitioning algorithms
- Compression algorithms **[Implemented]** — `ICompressionCodec` / `DefaultCompressionCodecRegistry` and the columnar codec set
- Sharding algorithms
- Load balancing algorithms
- Fault tolerance mechanisms

## Implementation Coverage Summary

The table below summarizes the major public components that implement concepts from Chapters 3–12. It is a capability map, not a count of public types; several rows intentionally group related interfaces and implementations. See the [DDIA coverage map](docs/DDIA-COVERAGE-MAP.md) for behavior boundaries and executable evidence.

| Component | Interface | Implementation | DDIA Chapter |
|-----------|-----------|----------------|--------------|
| LSM Storage Engine | `ILsmStorageEngine` | `LsmStorageEngine` | Ch. 4 |
| LSM Compaction | `ILsmStorageEngine` | `LsmStorageEngine.CompactAsync` | Ch. 4 |
| Compaction Strategies | `ICompactionStrategy` | `FullCompactionStrategy`, `SizeTieredCompactionStrategy`, `LeveledCompactionStrategy` | Ch. 4 |
| B-tree | `IBTree` | `BTree` | Ch. 4 |
| B+ tree | `IBPlusTree` | `BPlusTree` | Ch. 4 |
| SSTable | `ISortedStringTable` | `SortedStringTable` | Ch. 4 |
| Write-Ahead Log | `IWriteAheadLog` | `WriteAheadLog` | Ch. 4 |
| Merkle tree | `IMerkleTree` | `MerkleTree` *(Boutquin.Storage.Distributed — reference)* | Ch. 6 |
| Red-Black tree (MemTable) | `IRedBlackTree` | `RedBlackTree` | Ch. 4 |
| Skip list (MemTable) | `ISkipListMemTable` | `SkipListMemTable` | Ch. 4 |
| Bloom filter | `IBloomFilter` | `BloomFilter` | Ch. 4 |
| Counting Bloom filter | `ICountingBloomFilter` | `CountingBloomFilter` | Ch. 4 |
| FNV-1a hash | `IHashAlgorithm` | `Fnv1aHash` | Ch. 4 |
| Murmur3 hash | `IHashAlgorithm` | `Murmur3` | Ch. 4 |
| xxHash32 | `IHashAlgorithm` | `XxHash32` | Ch. 4 |
| Append-only storage | — | `AppendOnlyFileStorageEngine` | Ch. 4 |
| Indexed append-only storage | — | `AppendOnlyFileStorageEngineWithIndex` | Ch. 4 |
| Log-segmented storage | — | `LogSegmentedStorageEngine` | Ch. 4 |
| Binary serializer | `IEntrySerializer` | `BinaryEntrySerializer` | Ch. 5 |
| CSV serializer | `IEntrySerializer` | `CsvEntrySerializer` | Ch. 5 |
| Single-leader replication | `ISingleLeaderReplication` | `SingleLeaderReplication` *(Boutquin.Storage.Distributed — reference)* | Ch. 6 |
| Quorum replication | `IQuorumReplication` | `QuorumReplication` *(Boutquin.Storage.Distributed — reference)* | Ch. 6 |
| Replication log | `IReplicationLog` | `ReplicationLog` *(Boutquin.Storage.Distributed — reference)* | Ch. 6 |
| GCounter (CRDT) | `IGCounter` | `GCounter` *(Boutquin.Storage.Distributed — reference)* | Ch. 6 |
| PNCounter (CRDT) | `IPNCounter` | `PNCounter` *(Boutquin.Storage.Distributed — reference)* | Ch. 6 |
| GSet (CRDT) | `IGSet` | `GSet` *(Boutquin.Storage.Distributed — reference)* | Ch. 6 |
| ORSet (CRDT) | `IORSet` | `ORSet` *(Boutquin.Storage.Distributed — reference)* | Ch. 6 |
| Gossip protocol | `IGossipProtocol` | `GossipProtocol` *(Boutquin.Storage.Distributed — reference)* | Ch. 6 |
| Consistent hash ring | `IConsistentHashRing` | `ConsistentHashRing` *(Boutquin.Storage.Distributed — reference)* | Ch. 7 |
| Range partitioner | `IRangePartitioner` | `RangePartitioner` *(Boutquin.Storage.Distributed — reference)* | Ch. 7 |
| Hash partitioner | `IPartitioner` | `HashPartitioner` *(Boutquin.Storage.Distributed — reference)* | Ch. 7 |
| Rendezvous hash | `IRendezvousHash` | `RendezvousHash` *(Boutquin.Storage.Distributed — reference)* | Ch. 7 |
| Secondary index | `ISecondaryIndex` | `SecondaryIndex` | Ch. 7 |
| MVCC store | `IMvccStore` | `MvccStore` *(Boutquin.Storage.Distributed — reference)* | Ch. 8 |
| Snapshot Isolation store | `ISnapshotIsolationStore` | `SnapshotIsolationStore` *(Boutquin.Storage.Distributed — reference; plain SI, not SSI)* | Ch. 8 |
| Two-phase commit | `ITwoPhaseCommitCoordinator`, `ITransactionalResource` | `TwoPhaseCommitCoordinator`, `DurableTransactionalResource` *(Boutquin.Storage.Distributed — reference)* | Ch. 8, 11 |
| Vector clock | `IVectorClock` | `VectorClock` *(Boutquin.Storage.Distributed — reference)* | Ch. 9 |
| Lamport timestamp | `ILamportTimestamp` | `LamportTimestamp` *(Boutquin.Storage.Distributed — reference)* | Ch. 9 |
| Raft consensus | `IRaftNode`, `IRaftCluster` | `RaftNode`, `RaftCluster` *(Boutquin.Storage.Distributed — reference)* | Ch. 10 |
| Object Store | `IObjectStore` | `FileSystemObjectStore`, `InMemoryObjectStore` | Ch. 11 |
| Checkpoint Store | `ICheckpointStore` | `FileCheckpointStore`, `InMemoryCheckpointStore` | Ch. 12 |
| Schema Registry | `ISchemaRegistry` | `InMemorySchemaRegistry` | Ch. 5 |
| Schema Compatibility | `ISchemaCompatibilityChecker` | `AvroFieldLevelCompatibilityChecker`, `TaggedWireCompatibilityChecker`, `ProtobufTagCompatibilityChecker` | Ch. 5 |
| Versioned Serializer | `IAvroStyleVersionedSerializer<T>`, `IProtobufSerializer<T>` | `JsonVersionedSerializer<T>`, `TaggedWireVersionedSerializer<T>`, `ProtoSerializer<T>` | Ch. 5 |
| Event Log | `IEventLog<TEvent>` | `AppendOnlyEventLog<TEvent>`, `InMemoryEventLog<TEvent>` | Ch. 3, 12 |
| CDC Source | `IChangeDataCaptureSource<TKey,TValue>`, `ISnapshotCdcSource<TKey,TValue>` | `EventLogCdcSource`, `ObjectStoreCdcSource`, `LsmCdcAdapter` | Ch. 12 |
| Concurrent KV Store | `IKeyValueStore` | `ConcurrentKeyValueStore` | Ch. 4 |
| Multi-leader replication | `IMultiLeaderReplication` | `MultiLeaderReplicaSet` *(Boutquin.Storage.Distributed — reference)* | Ch. 6 |
| Leaderless replication | — | `LeaderlessReplicaSet` *(Boutquin.Storage.Distributed — reference)* | Ch. 6 |
| Columnar time-series storage | `ITimeSeriesStorageEngine`, `IColumnarSegmentFormat` | `LsmTimeSeriesStorageEngine`, `ColumnarSegmentFormat` | Ch. 3, 4 |
| Text and vector indexes | `IInvertedIndex`, `IVectorIndex` | `InvertedIndex`, `FlatVectorIndex`, `HnswVectorIndex`, `IvfVectorIndex` | Ch. 3, 7 |
| Vectorized analytics | `IVectorizedOperator`, `IDataCubeBuilder` | `FilterOperator`, `ProjectionOperator`, `AggregateOperator`, `DataCubeBuilder` | Ch. 3, 11 |
| Batch dataflow | `IExternalSorter`, `IShufflePartitioner`, `IBatchTask` | `ExternalMergeSorter`, `HashShufflePartitioner`, `BatchJobGraph`, `BatchJobRunner`, `MiniDataFrame` | Ch. 11 |
| Stream processing | `IStream`, `IStreamProcessor`, `IWindowAssigner` | `EventLogStream`, `AtLeastOnceProcessor`, `EffectivelyOnceProcessor`, `WindowedAggregator` | Ch. 12 |
| Stream joins | `IStreamJoin` | `StreamStreamJoin`, `StreamTableJoin`, `TableTableJoin` | Ch. 12 |
