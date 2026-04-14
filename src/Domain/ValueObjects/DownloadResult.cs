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

namespace Boutquin.Storage.Domain.ValueObjects;

/// <summary>
/// Immutable result of a batch download operation, tracking how many items were downloaded,
/// skipped (already fresh), or failed with errors.
/// </summary>
/// <param name="Downloaded">Count of items successfully downloaded.</param>
/// <param name="Skipped">Count of items skipped because they were already fresh.</param>
/// <param name="Errors">Descriptive error messages for items that failed (e.g., "AAPL: 404 Not Found").</param>
public sealed record DownloadResult(int Downloaded, int Skipped, IReadOnlyList<string> Errors);
