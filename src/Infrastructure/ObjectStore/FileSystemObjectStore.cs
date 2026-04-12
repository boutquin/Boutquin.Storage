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
using System.Text.RegularExpressions;

using Boutquin.Storage.Domain.Interfaces.ObjectStore;

namespace Boutquin.Storage.Infrastructure.ObjectStore;

/// <summary>
/// File-system-backed object store using atomic writes via temp-file-and-rename.
///
/// <para>Keys are mapped to file paths under a configurable root directory. Keys containing
/// <c>/</c> create nested subdirectories automatically. Key characters are restricted to
/// alphanumeric, <c>.</c>, <c>_</c>, <c>-</c>, and <c>/</c> to prevent path traversal attacks.</para>
///
/// <para><b>Atomicity:</b> writes go to a temp file in the same directory, then are renamed
/// to the final path. On POSIX systems, rename is atomic within the same filesystem.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 10 — "Batch Processing": using object storage for durable batch outputs.</para>
/// </summary>
public sealed partial class FileSystemObjectStore : IObjectStore
{
    private readonly string _rootDir;

    /// <summary>
    /// Initializes a new instance of <see cref="FileSystemObjectStore"/>.
    /// </summary>
    /// <param name="rootDirectory">The root directory for object storage. Created if it does not exist.</param>
    public FileSystemObjectStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDir = rootDirectory;
        Directory.CreateDirectory(_rootDir);
    }

    /// <inheritdoc />
    public ValueTask<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        ValidateKey(key);
        ct.ThrowIfCancellationRequested();
        var path = ResolvePath(key);
        return new ValueTask<bool>(File.Exists(path));
    }

    /// <inheritdoc />
    public ValueTask<Stream?> ReadAsync(string key, CancellationToken ct = default)
    {
        ValidateKey(key);
        ct.ThrowIfCancellationRequested();
        var path = ResolvePath(key);

        if (!File.Exists(path))
        {
            return new ValueTask<Stream?>((Stream?)null);
        }

        // Return a FileStream opened for reading — caller owns disposal
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new ValueTask<Stream?>(stream);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(string key, Stream content, CancellationToken ct = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(content);
        ct.ThrowIfCancellationRequested();

        var path = ResolvePath(key);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        // Atomic write: write to temp file, then rename
        var tempPath = path + $".tmp.{Guid.NewGuid():N}";
        try
        {
            var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            try
            {
                await content.CopyToAsync(fs, ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }
            finally
            {
                await fs.DisposeAsync().ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Clean up temp file on failure
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        ValidateKey(key);
        ct.ThrowIfCancellationRequested();
        var path = ResolvePath(key);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return default;
    }

    private string ResolvePath(string key) =>
        Path.Combine(_rootDir, key.Replace('/', Path.DirectorySeparatorChar));

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!AllowedKeyPattern().IsMatch(key))
        {
            throw new ArgumentException(
                "Key contains invalid characters. Allowed: alphanumeric, '.', '_', '-', '/'.",
                nameof(key));
        }
    }

    [GeneratedRegex(@"^[a-zA-Z0-9._\-/]+$")]
    private static partial Regex AllowedKeyPattern();
}
