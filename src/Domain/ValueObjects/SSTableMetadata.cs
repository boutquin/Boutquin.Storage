// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
namespace Boutquin.Storage.Domain.ValueObjects;

/// <summary>
/// Represents metadata for an SSTable file in the context of an LSM-tree.
/// </summary>
/// <remarks>
/// <b>Theory:</b>
/// An SSTable (Sorted String Table) is a key component of the Log-Structured Merge-Tree (LSM-tree) architecture. SSTables are immutable, 
/// sorted key-value storage files that are written to disk when the in-memory MemTable is full. These files facilitate efficient read 
/// operations and merge processes. Metadata for an SSTable includes various attributes that describe the state and characteristics of the 
/// SSTable, providing essential information for managing and accessing the table.
/// 
/// <b>Components of SSTable Metadata:</b>
/// - **EntryCount**: Indicates the total number of key-value pairs stored in the SSTable. This attribute helps in assessing the size and 
///   density of the SSTable.
/// - **FileSize**: Represents the size of the SSTable file in bytes. It is useful for managing disk space and understanding the storage 
///   requirements of the SSTable.
/// - **CreationTime**: The timestamp when the SSTable was initially created. This is important for tracking the age of the SSTable and 
///   scheduling compaction processes.
/// - **LastModificationTime**: The timestamp when the SSTable was last modified. This helps in determining the recency of data within the 
///   SSTable and aids in merge decisions.
/// - **FileName**: The name of the SSTable file. It is essential for file management and accessing the correct SSTable on disk.
/// - **FileLocation**: The location (path) of the SSTable file. This attribute specifies where the SSTable is stored on the filesystem, 
///   facilitating efficient file access and retrieval.
/// 
/// <b>Importance in LSM-trees:</b>
/// In an LSM-tree, efficient management and access to SSTables are crucial for maintaining high read and write performance. The metadata 
/// provides necessary information for operations such as compaction, merging, and querying. By keeping track of metadata, the system can 
/// make informed decisions about when to merge SSTables, how to optimize storage, and how to quickly access the required data.
/// </remarks>
/// <param name="EntryCount">The number of entries in the SSTable.</param>
/// <param name="FileSize">The size of the SSTable file in bytes.</param>
/// <param name="CreationTime">The timestamp when the SSTable was created.</param>
/// <param name="LastModificationTime">The timestamp when the SSTable was last modified.</param>
/// <param name="FileName">The name of the SSTable file.</param>
/// <param name="FileLocation">The location (path) of the SSTable file.</param>
public readonly record struct SsTableMetadata(
    int EntryCount,
    long FileSize,
    DateTime CreationTime,
    DateTime LastModificationTime,
    string FileName,
    string FileLocation);