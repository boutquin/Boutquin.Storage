// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

namespace Boutquin.Storage.Domain.Interfaces.Batch;

/// <summary>Co-locates equal logical keys into a fixed number of batch partitions.</summary>
/// <typeparam name="T">Input row type.</typeparam>
public interface IShufflePartitioner<in T>
{
    /// <summary>Returns a partition in [0, partitionCount); equal encoded keys must agree.</summary>
    /// <param name="item">Input row.</param>
    /// <param name="partitionCount">Positive partition count, fixed across a shuffle.</param>
    /// <returns>The zero-based partition.</returns>
    int GetPartition(T item, int partitionCount);
}
