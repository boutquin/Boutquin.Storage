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

namespace Boutquin.Storage.Domain.Interfaces.Analytics;

/// <summary>Transforms one typed analytics batch into an output value.</summary>
/// <typeparam name="TInput">The batch or intermediate value consumed by the operator.</typeparam>
/// <typeparam name="TOutput">The transformed batch or aggregate value.</typeparam>
/// <remarks>
/// Vectorized means that an operator receives a complete column batch and avoids rehydrating
/// records one at a time. Implementations are not required to use hardware SIMD instructions.
/// </remarks>
public interface IVectorizedOperator<in TInput, out TOutput>
{
    /// <summary>Processes one input batch synchronously.</summary>
    /// <param name="input">The input batch.</param>
    /// <returns>The transformed batch or aggregate.</returns>
    TOutput Execute(TInput input);
}
