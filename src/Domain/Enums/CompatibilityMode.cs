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
namespace Boutquin.Storage.Domain.Enums;

/// <summary>
/// Defines the schema compatibility mode used when evolving schemas in a registry.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 4 — "Encoding and Evolution": forward and backward compatibility in schema evolution.</para>
/// </summary>
public enum CompatibilityMode
{
    /// <summary>
    /// New schema can read data written by old schema. New fields must be optional;
    /// required fields cannot be removed.
    /// </summary>
    Backward,

    /// <summary>
    /// Old schema can read data written by new schema. Old fields must be optional;
    /// new required fields cannot be added.
    /// </summary>
    Forward,

    /// <summary>
    /// Both backward and forward compatible. The strictest mode.
    /// </summary>
    Full,

    /// <summary>
    /// No compatibility checking. Any schema change is accepted.
    /// </summary>
    None,
}
