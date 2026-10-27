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

namespace Boutquin.Storage.Domain.ValueObjects.Analytics;

/// <summary>Names one cube dimension and extracts its value from a row.</summary>
/// <typeparam name="TRecord">The input row type.</typeparam>
/// <param name="Name">A unique, nonblank dimension name.</param>
/// <param name="Selector">The deterministic dimension-value selector.</param>
public sealed record CubeDimension<TRecord>(string Name, Func<TRecord, object?> Selector);
