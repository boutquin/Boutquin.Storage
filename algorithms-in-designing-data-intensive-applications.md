# Algorithms in "Designing Data-Intensive Applications" (2nd Edition)

"Designing Data-Intensive Applications" (2nd edition) by [Martin Kleppmann](https://martin.kleppmann.com/) and [Chris Riccomini](https://cnr.sh/) is a comprehensive guide on building reliable, scalable, and maintainable systems. While the book doesn't provide an exhaustive list of algorithms in a traditional textbook sense, it discusses many key algorithms and concepts relevant to data-intensive applications. Here is an extensive list of the primary algorithms, techniques, and concepts covered in the book.

Algorithms implemented in this project are marked with **[Implemented]**.

## Chapter 3: Data Models and Query Languages
- B-trees **[Implemented]** — `IBTree` / `BTree`
- Log-structured merge-trees (LSM-trees) **[Implemented]** — `ILsmStorageEngine` / `LsmStorageEngine`
- Merkle trees **[Implemented]** — `IMerkleTree` / `MerkleTree`
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
- Schema compatibility checking **[Implemented]** — `ISchemaCompatibilityChecker` / `FieldLevelCompatibilityChecker`
- Versioned serialization **[Implemented]** — `IVersionedSerializer<T>` / `JsonVersionedSerializer<T>`
- Data serialization formats (e.g., JSON, XML, Protocol Buffers, Thrift, Avro)
- Binary serialization **[Implemented]** — `BinaryEntrySerializer`
- Text serialization **[Implemented]** — `CsvEntrySerializer`

## Chapter 6: Replication
- Single-leader replication **[Implemented]** — `ISingleLeaderReplication` / `SingleLeaderReplication`
- Multi-leader replication
- Leaderless replication
- Quorum-based replication **[Implemented]** — `IQuorumReplication` / `QuorumReplication`
- Conflict-free replicated data types (CRDTs) **[Implemented]** — `IGCounter` / `GCounter`, `IPNCounter` / `PNCounter`, `IGSet` / `GSet`, `IORSet` / `ORSet`
- Gossip protocols **[Implemented]** — `IGossipProtocol` / `GossipProtocol`
- Merkle trees (anti-entropy) **[Implemented]** — `IMerkleTree` / `MerkleTree`
- Replication log **[Implemented]** — `IReplicationLog` / `ReplicationLog`

## Chapter 7: Sharding
- Consistent hashing **[Implemented]** — `IConsistentHashRing` / `ConsistentHashRing`
- Rendezvous hashing **[Implemented]** — `IRendezvousHash` / `RendezvousHash`
- Range partitioning **[Implemented]** — `IRangePartitioner` / `RangePartitioner`
- Hash partitioning **[Implemented]** — `IPartitioner` / `HashPartitioner`
- Secondary indexes **[Implemented]** — `ISecondaryIndex` / `SecondaryIndex`

## Chapter 8: Transactions
- Two-phase commit protocol (2PC)
- Three-phase commit protocol (3PC)
- Paxos
- Raft **[Implemented]** — `IRaftNode` / `RaftNode`, `IRaftCluster` / `RaftCluster`
- Multi-version concurrency control (MVCC) **[Implemented]** — `IMvccStore` / `MvccStore`
- Serializable Snapshot Isolation (SSI) **[Implemented]** — `ISsiStore` / `SsiStore`

## Chapter 9: The Trouble with Distributed Systems
- Consensus algorithms (e.g., Paxos, Raft) **[Implemented]** — `IRaftNode` / `RaftNode`
- Clock synchronization algorithms (e.g., NTP, PTP)
- Vector clocks **[Implemented]** — `IVectorClock` / `VectorClock`
- Lamport timestamps **[Implemented]** — `ILamportTimestamp` / `LamportTimestamp`
- Gossip protocols **[Implemented]** — `IGossipProtocol` / `GossipProtocol`

## Chapter 10: Consistency and Consensus
- Paxos
- Raft **[Implemented]** — `IRaftNode` / `RaftNode`, `IRaftCluster` / `RaftCluster`
- Viewstamped Replication
- Zab (ZooKeeper Atomic Broadcast)

## Chapter 11: Batch Processing
- MapReduce
- Directed acyclic graphs (DAGs)
- Dataflow algorithms
- Two-phase commit (for distributed transactions)
- Object stores **[Implemented]** — `IObjectStore` / `FileSystemObjectStore`, `InMemoryObjectStore`

## Chapter 12: Stream Processing
- Stream processing algorithms (e.g., Apache Kafka, Apache Flink)
- Distributed snapshot algorithms
- Stream joins
- Time windowing algorithms
- Change data capture **[Implemented]** — `IChangeDataCaptureSource<TKey,TValue>` / `EventLogCdcSource<TKey,TValue>`, `ObjectStoreCdcSource<TKey>`
- Checkpointing **[Implemented]** — `ICheckpointStore` / `FileCheckpointStore`, `InMemoryCheckpointStore`
- Append-only event log **[Implemented]** — `IEventLog<TEvent>` / `AppendOnlyEventLog<TEvent>`

## Chapter 13: A Philosophy of Streaming Systems
- Exactly-once semantics
- Idempotency
- Microbatching vs. per-event processing
- Dataflow programming models

## General Algorithms Discussed
- Hash functions **[Implemented]** — `Fnv1aHash`, `Murmur3`, `XxHash32`
- Sorting algorithms
- Data partitioning algorithms
- Compression algorithms
- Sharding algorithms
- Load balancing algorithms
- Fault tolerance mechanisms

## Implementation Coverage Summary

This project implements **49 components** covering algorithms from Chapters 3–12:

| Component | Interface | Implementation | DDIA Chapter |
|-----------|-----------|----------------|--------------|
| LSM Storage Engine | `ILsmStorageEngine` | `LsmStorageEngine` | Ch. 4 |
| LSM Compaction | `ILsmStorageEngine` | `LsmStorageEngine.CompactAsync` | Ch. 4 |
| Compaction Strategies | `ICompactionStrategy` | `FullCompactionStrategy`, `SizeTieredCompactionStrategy`, `LeveledCompactionStrategy` | Ch. 4 |
| B-tree | `IBTree` | `BTree` | Ch. 4 |
| B+ tree | `IBPlusTree` | `BPlusTree` | Ch. 4 |
| SSTable | `ISortedStringTable` | `SortedStringTable` | Ch. 4 |
| Write-Ahead Log | `IWriteAheadLog` | `WriteAheadLog` | Ch. 4 |
| Merkle tree | `IMerkleTree` | `MerkleTree` | Ch. 6 |
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
| Single-leader replication | `ISingleLeaderReplication` | `SingleLeaderReplication` | Ch. 6 |
| Quorum replication | `IQuorumReplication` | `QuorumReplication` | Ch. 6 |
| Replication log | `IReplicationLog` | `ReplicationLog` | Ch. 6 |
| GCounter (CRDT) | `IGCounter` | `GCounter` | Ch. 6 |
| PNCounter (CRDT) | `IPNCounter` | `PNCounter` | Ch. 6 |
| GSet (CRDT) | `IGSet` | `GSet` | Ch. 6 |
| ORSet (CRDT) | `IORSet` | `ORSet` | Ch. 6 |
| Gossip protocol | `IGossipProtocol` | `GossipProtocol` | Ch. 6 |
| Consistent hash ring | `IConsistentHashRing` | `ConsistentHashRing` | Ch. 7 |
| Range partitioner | `IRangePartitioner` | `RangePartitioner` | Ch. 7 |
| Hash partitioner | `IPartitioner` | `HashPartitioner` | Ch. 7 |
| Rendezvous hash | `IRendezvousHash` | `RendezvousHash` | Ch. 7 |
| Secondary index | `ISecondaryIndex` | `SecondaryIndex` | Ch. 7 |
| MVCC store | `IMvccStore` | `MvccStore` | Ch. 8 |
| SSI store | `ISsiStore` | `SsiStore` | Ch. 8 |
| Vector clock | `IVectorClock` | `VectorClock` | Ch. 9 |
| Lamport timestamp | `ILamportTimestamp` | `LamportTimestamp` | Ch. 9 |
| Raft consensus | `IRaftNode`, `IRaftCluster` | `RaftNode`, `RaftCluster` | Ch. 10 |
| Object Store | `IObjectStore` | `FileSystemObjectStore`, `InMemoryObjectStore` | Ch. 11 |
| Checkpoint Store | `ICheckpointStore` | `FileCheckpointStore`, `InMemoryCheckpointStore` | Ch. 12 |
| Schema Registry | `ISchemaRegistry` | `InMemorySchemaRegistry` | Ch. 5 |
| Schema Compatibility | `ISchemaCompatibilityChecker` | `FieldLevelCompatibilityChecker` | Ch. 5 |
| Versioned Serializer | `IVersionedSerializer<T>` | `JsonVersionedSerializer<T>` | Ch. 5 |
| Event Log | `IEventLog<TEvent>` | `AppendOnlyEventLog<TEvent>`, `InMemoryEventLog<TEvent>` | Ch. 3, 12 |
| CDC Source | `IChangeDataCaptureSource` | `EventLogCdcSource`, `ObjectStoreCdcSource` | Ch. 12 |
| Concurrent KV Store | `IKeyValueStore` | `ConcurrentKeyValueStore` | Ch. 4 |
