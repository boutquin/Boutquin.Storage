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

/// <summary>A consistent keyed snapshot paired with the first position to read afterward.</summary>
/// <typeparam name="TKey">Stable key identity.</typeparam>
/// <typeparam name="TValue">Immutable value type.</typeparam>
/// <param name="Records">Live Snapshot records and retained Delete watermarks, carrying their original per-key positions.</param>
/// <param name="NextPosition">Exclusive source high-water mark; tail reads begin here.</param>
/// <remarks>Bootstrap a fresh or explicitly reset target. Omitted keys do not clear pre-existing unrelated target state.</remarks>
public sealed record CdcSnapshot<TKey, TValue>(IReadOnlyList<ChangeRecord<TKey, TValue>> Records, long NextPosition);
