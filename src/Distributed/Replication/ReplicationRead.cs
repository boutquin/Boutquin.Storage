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

/// <summary>A resolved read and the causal frontier required by the client's next operation.</summary>
/// <typeparam name="T">The borrowed immutable value type.</typeparam>
public sealed class ReplicationRead<T>
{
    internal ReplicationRead(IReadOnlyList<T> values, SessionToken session)
    {
        Values = Array.AsReadOnly(values.ToArray());
        Session = session;
    }
    /// <summary>Zero values means absence; multiple values are concurrent siblings.</summary>
    public IReadOnlyList<T> Values { get; }
    /// <summary>Pass this token to the next read or write, including operations on other keys.</summary>
    public SessionToken Session { get; }
}
