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
namespace Boutquin.Storage.Domain.ValueObjects;

/// <summary>
/// A monotonically increasing version number for schemas in a registry.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 4 — "Encoding and Evolution": schema versions in Avro/Protobuf registries.</para>
/// </summary>
/// <param name="Value">The version number. Must be positive.</param>
public readonly record struct SchemaVersion(int Value)
{
    /// <summary>Returns the string representation of the version.</summary>
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
