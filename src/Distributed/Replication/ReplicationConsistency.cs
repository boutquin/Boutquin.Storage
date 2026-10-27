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
namespace Boutquin.Storage.Distributed.Replication;

/// <summary>Per-operation response requirements measured against the home replica count.</summary>
public enum ReplicationConsistency
{
    /// <summary>One response; permits stale reads and disjoint read/write sets.</summary>
    One,
    /// <summary>A strict majority of the home count; sloppy substitution still weakens overlap.</summary>
    Quorum,
    /// <summary>Responses equal to the entire home count; sloppy mode may substitute spare nodes.</summary>
    All,
}
