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
namespace Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;

/// <summary>
/// Exposes a stable identity for the physical segment represented by a readable stream.
/// </summary>
/// <remarks>
/// Implementations return one immutable, nonempty descriptor for their lifetime. A descriptor
/// identifies the physical storage extent, not a caller's requested logical range, so consumers
/// can deduplicate corruption or recovery work across reopened streams. Implementing this
/// interface performs no I/O; streams without a physical backing may expose an object-scoped
/// identity instead of claiming an unrelated shared source.
/// </remarks>
public interface IPhysicalSegmentIdentity
{
    /// <summary>
    /// Gets the immutable, nonempty descriptor of the physical segment represented by this stream.
    /// </summary>
    string PhysicalSegmentIdentity { get; }
}
