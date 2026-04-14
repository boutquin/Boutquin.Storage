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

namespace Boutquin.Storage.Infrastructure.Caching;

/// <summary>
/// Determines whether a cached file is still fresh enough to skip re-download.
/// Compares the file's last-write timestamp against a configurable maximum age.
/// </summary>
/// <remarks>
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 5 — "Replication": staleness and freshness guarantees in caching layers.</para>
/// </remarks>
public static class FreshnessChecker
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="filePath"/> exists and was modified
    /// within the last <paramref name="maxAge"/>; <see langword="false"/> if the file should
    /// be (re-)downloaded.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the cached file.</param>
    /// <param name="maxAge">Maximum acceptable age. Non-positive values mean "always stale".</param>
    /// <param name="force">When <see langword="true"/>, always returns <see langword="false"/>
    /// regardless of file age — forces a re-download.</param>
    public static bool IsFresh(string filePath, TimeSpan maxAge, bool force = false)
    {
        if (force)
        {
            return false;
        }

        if (maxAge <= TimeSpan.Zero)
        {
            return false;
        }

        if (!File.Exists(filePath))
        {
            return false;
        }

        var lastWrite = File.GetLastWriteTimeUtc(filePath);
        var age = DateTime.UtcNow - lastWrite;

        return age < maxAge;
    }
}
