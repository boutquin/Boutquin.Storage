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

using Boutquin.Storage.Domain.ValueObjects.Analytics;

namespace Boutquin.Storage.Domain.Interfaces.Analytics;

/// <summary>Aggregates records across every grouping set of configured dimensions.</summary>
/// <typeparam name="TRecord">The input row type.</typeparam>
/// <typeparam name="TMeasure">The exact measure and accumulator type.</typeparam>
public interface IDataCubeBuilder<in TRecord, TMeasure>
{
    /// <summary>Builds the cube, including the grand-total cell.</summary>
    /// <param name="records">Input records enumerated exactly once.</param>
    /// <returns>One cell for every populated grouping key plus the grand total.</returns>
    IReadOnlyList<DataCubeCell<TMeasure>> Build(IEnumerable<TRecord> records);
}
