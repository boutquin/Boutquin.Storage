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
namespace Boutquin.Storage.Domain.ValueObjects.Streaming;

/// <summary>Contains a finalized or revised keyed window aggregate.</summary>
/// <typeparam name="TKey">The grouping key type.</typeparam>
/// <typeparam name="TValue">The aggregate value type.</typeparam>
/// <param name="Window">The event-time window.</param>
/// <param name="Key">The grouping key.</param>
/// <param name="Value">The aggregate value.</param>
/// <param name="Kind">How the result changes a downstream materialized view.</param>
/// <remarks>
/// The value is a published immutable snapshot. Aggregators must not mutate a value after emission.
/// </remarks>
public sealed record WindowedAggregate<TKey, TValue>(
    TimeWindow Window,
    TKey Key,
    TValue Value,
    WindowEmissionKind Kind);
