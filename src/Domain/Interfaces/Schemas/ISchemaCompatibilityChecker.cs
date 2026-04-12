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
namespace Boutquin.Storage.Domain.Interfaces.Schemas;

/// <summary>
/// Checks whether a candidate schema is compatible with an existing schema under a given mode.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 4 — "Encoding and Evolution": compatibility rules for schema evolution.</para>
/// </summary>
public interface ISchemaCompatibilityChecker
{
    /// <summary>
    /// Checks whether the <paramref name="candidate"/> schema is compatible with the
    /// <paramref name="existing"/> schema under the specified <paramref name="mode"/>.
    /// </summary>
    /// <param name="existing">The currently registered schema.</param>
    /// <param name="candidate">The new schema being proposed.</param>
    /// <param name="mode">The compatibility mode to enforce.</param>
    /// <returns><c>true</c> if the candidate is compatible; otherwise <c>false</c>.</returns>
    bool IsCompatible(ISchema existing, ISchema candidate, CompatibilityMode mode);
}
