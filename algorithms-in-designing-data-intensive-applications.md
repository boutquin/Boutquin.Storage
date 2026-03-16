# Algorithms in "Designing Data-Intensive Applications"

"Designing Data-Intensive Applications" by [Martin Kleppmann](https://martin.kleppmann.com/) is a comprehensive guide on building reliable, scalable, and maintainable systems. While the book doesn't provide an exhaustive list of algorithms in a traditional textbook sense, it discusses many key algorithms and concepts relevant to data-intensive applications. Here is an extensive list of the primary algorithms, techniques, and concepts covered in the book.

Algorithms implemented in this project are marked with **[Implemented]**.

## Chapter 2: Data Models and Query Languages
- B-trees **[Implemented]** — `IBTree` / `BTree`
- Log-structured merge-trees (LSM-trees) **[Implemented]** — `ILsmStorageEngine` / `LsmStorageEngine`
- Merkle trees **[Implemented]** — `IMerkleTree` / `MerkleTree`

## Chapter 3: Storage and Retrieval
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

## Chapter 4: Encoding and Evolution
- Schema evolution
- Data serialization formats (e.g., JSON, XML, Protocol Buffers, Thrift, Avro)
- Binary serialization **[Implemented]** — `BinaryEntrySerializer`
- Text serialization **[Implemented]** — `CsvEntrySerializer`

## Chapter 5: Replication
- Single-leader replication **[Implemented]** — `ISingleLeaderReplication` / `SingleLeaderReplication`
- Multi-leader replication
- Leaderless replication
- Quorum-based replication **[Implemented]** — `IQuorumReplication` / `QuorumReplication`
- Conflict-free replicated data types (CRDTs) **[Implemented]** — `IGCounter` / `GCounter`, `IPNCounter` / `PNCounter`, `IGSet` / `GSet`, `IORSet` / `ORSet`
- Gossip protocols **[Implemented]** — `IGossipProtocol` / `GossipProtocol`
- Merkle trees (anti-entropy) **[Implemented]** — `IMerkleTree` / `MerkleTree`
- Replication log **[Implemented]** — `IReplicationLog` / `ReplicationLog`

## Chapter 6: Partitioning
- Consistent hashing **[Implemented]** — `IConsistentHashRing` / `ConsistentHashRing`
- Rendezvous hashing **[Implemented]** — `IRendezvousHash` / `RendezvousHash`
- Range partitioning **[Implemented]** — `IRangePartitioner` / `RangePartitioner`
- Hash partitioning **[Implemented]** — `IPartitioner` / `HashPartitioner`
- Secondary indexes **[Implemented]** — `ISecondaryIndex` / `SecondaryIndex`

## Chapter 7: Transactions
- Two-phase commit protocol (2PC)
- Three-phase commit protocol (3PC)
- Paxos
- Raft **[Implemented]** — `IRaftNode` / `RaftNode`, `IRaftCluster` / `RaftCluster`
- Multi-version concurrency control (MVCC) **[Implemented]** — `IMvccStore` / `MvccStore`
- Serializable Snapshot Isolation (SSI) **[Implemented]** — `ISsiStore` / `SsiStore`

## Chapter 8: The Trouble with Distributed Systems
- Consensus algorithms (e.g., Paxos, Raft) **[Implemented]** — `IRaftNode` / `RaftNode`
- Clock synchronization algorithms (e.g., NTP, PTP)
- Vector clocks **[Implemented]** — `IVectorClock` / `VectorClock`
- Lamport timestamps **[Implemented]** — `ILamportTimestamp` / `LamportTimestamp`
- Gossip protocols **[Implemented]** — `IGossipProtocol` / `GossipProtocol`

## Chapter 9: Consistency and Consensus
- Paxos
- Raft **[Implemented]** — `IRaftNode` / `RaftNode`, `IRaftCluster` / `RaftCluster`
- Viewstamped Replication
- Zab (ZooKeeper Atomic Broadcast)

## Chapter 10: Batch Processing
- MapReduce
- Directed acyclic graphs (DAGs)
- Dataflow algorithms
- Two-phase commit (for distributed transactions)

## Chapter 11: Stream Processing
- Stream processing algorithms (e.g., Apache Kafka, Apache Flink)
- Distributed snapshot algorithms
- Stream joins
- Time windowing algorithms

## General Algorithms Discussed
- Hash functions **[Implemented]** — `Fnv1aHash`, `Murmur3`, `XxHash32`
- Sorting algorithms
- Data partitioning algorithms
- Compression algorithms
- Sharding algorithms
- Load balancing algorithms
- Fault tolerance mechanisms

## Implementation Coverage Summary

This project implements **38 components** covering algorithms from Chapters 2–9:

| Component | Interface | Implementation | DDIA Chapter |
|-----------|-----------|----------------|--------------|
| LSM Storage Engine | `ILsmStorageEngine` | `LsmStorageEngine` | Ch. 3 |
| LSM Compaction | `ILsmStorageEngine` | `LsmStorageEngine.CompactAsync` | Ch. 3 |
| Compaction Strategies | `ICompactionStrategy` | `FullCompactionStrategy`, `SizeTieredCompactionStrategy`, `LeveledCompactionStrategy` | Ch. 3 |
| B-tree | `IBTree` | `BTree` | Ch. 3 |
| B+ tree | `IBPlusTree` | `BPlusTree` | Ch. 3 |
| SSTable | `ISortedStringTable` | `SortedStringTable` | Ch. 3 |
| Write-Ahead Log | `IWriteAheadLog` | `WriteAheadLog` | Ch. 3 |
| Merkle tree | `IMerkleTree` | `MerkleTree` | Ch. 5 |
| Red-Black tree (MemTable) | `IRedBlackTree` | `RedBlackTree` | Ch. 3 |
| Skip list (MemTable) | `ISkipListMemTable` | `SkipListMemTable` | Ch. 3 |
| Bloom filter | `IBloomFilter` | `BloomFilter` | Ch. 3 |
| Counting Bloom filter | `ICountingBloomFilter` | `CountingBloomFilter` | Ch. 3 |
| FNV-1a hash | `IHashAlgorithm` | `Fnv1aHash` | Ch. 3 |
| Murmur3 hash | `IHashAlgorithm` | `Murmur3` | Ch. 3 |
| xxHash32 | `IHashAlgorithm` | `XxHash32` | Ch. 3 |
| Append-only storage | — | `AppendOnlyFileStorageEngine` | Ch. 3 |
| Indexed append-only storage | — | `AppendOnlyFileStorageEngineWithIndex` | Ch. 3 |
| Log-segmented storage | — | `LogSegmentedStorageEngine` | Ch. 3 |
| Binary serializer | `IEntrySerializer` | `BinaryEntrySerializer` | Ch. 4 |
| CSV serializer | `IEntrySerializer` | `CsvEntrySerializer` | Ch. 4 |
| Single-leader replication | `ISingleLeaderReplication` | `SingleLeaderReplication` | Ch. 5 |
| Quorum replication | `IQuorumReplication` | `QuorumReplication` | Ch. 5 |
| Replication log | `IReplicationLog` | `ReplicationLog` | Ch. 5 |
| GCounter (CRDT) | `IGCounter` | `GCounter` | Ch. 5 |
| PNCounter (CRDT) | `IPNCounter` | `PNCounter` | Ch. 5 |
| GSet (CRDT) | `IGSet` | `GSet` | Ch. 5 |
| ORSet (CRDT) | `IORSet` | `ORSet` | Ch. 5 |
| Gossip protocol | `IGossipProtocol` | `GossipProtocol` | Ch. 5 |
| Consistent hash ring | `IConsistentHashRing` | `ConsistentHashRing` | Ch. 6 |
| Range partitioner | `IRangePartitioner` | `RangePartitioner` | Ch. 6 |
| Hash partitioner | `IPartitioner` | `HashPartitioner` | Ch. 6 |
| Rendezvous hash | `IRendezvousHash` | `RendezvousHash` | Ch. 6 |
| Secondary index | `ISecondaryIndex` | `SecondaryIndex` | Ch. 6 |
| MVCC store | `IMvccStore` | `MvccStore` | Ch. 7 |
| SSI store | `ISsiStore` | `SsiStore` | Ch. 7 |
| Vector clock | `IVectorClock` | `VectorClock` | Ch. 8 |
| Lamport timestamp | `ILamportTimestamp` | `LamportTimestamp` | Ch. 8 |
| Raft consensus | `IRaftNode`, `IRaftCluster` | `RaftNode`, `RaftCluster` | Ch. 9 |
