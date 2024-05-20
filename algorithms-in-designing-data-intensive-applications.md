# Algorithms in "Designing Data-Intensive Applications"

"Designing Data-Intensive Applications" by [Martin Kleppmann](https://martin.kleppmann.com/) is a comprehensive guide on building reliable, scalable, and maintainable systems. While the book doesn't provide an exhaustive list of algorithms in a traditional textbook sense, it discusses many key algorithms and concepts relevant to data-intensive applications. Here is an extensive list of the primary algorithms, techniques, and concepts covered in the book:
## Chapter 2: Data Models and Query Languages
- B-trees
- Log-structured merge-trees (LSM-trees)
- Merkle trees

## Chapter 3: Storage and Retrieval
- B-trees
- Log-structured merge-trees (LSM-trees)
- Hash indexes
- SSTables (Sorted String Tables)
- Bloom filters

## Chapter 4: Encoding and Evolution
- Schema evolution
- Data serialization formats (e.g., JSON, XML, Protocol Buffers, Thrift, Avro)

## Chapter 5: Replication
- Single-leader replication
- Multi-leader replication
- Leaderless replication
- Quorum-based replication
- Conflict-free replicated data types (CRDTs)
- Gossip protocols

## Chapter 6: Partitioning
- Consistent hashing
- Rendezvous hashing
- Range partitioning
- Hash partitioning
- Secondary indexes

## Chapter 7: Transactions
- Two-phase commit protocol (2PC)
- Three-phase commit protocol (3PC)
- Paxos
- Raft
- Multi-version concurrency control (MVCC)
- Serializable Snapshot Isolation (SSI)

## Chapter 8: The Trouble with Distributed Systems
- Consensus algorithms (e.g., Paxos, Raft)
- Clock synchronization algorithms (e.g., NTP, PTP)
- Vector clocks
- Lamport timestamps
- Gossip protocols

## Chapter 9: Consistency and Consensus
- Paxos
- Raft
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
- Hash functions
- Sorting algorithms
- Data partitioning algorithms
- Compression algorithms
- Sharding algorithms
- Load balancing algorithms
- Fault tolerance mechanisms