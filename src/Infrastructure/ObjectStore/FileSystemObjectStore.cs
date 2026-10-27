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
using Boutquin.Storage.Infrastructure.KeyValueStore;

namespace Boutquin.Storage.Infrastructure.ObjectStore;

/// <summary>
/// File-system-backed object store using atomic writes via temp-file-and-rename.
///
/// <para>Keys are mapped to file paths under a configurable root directory. Keys containing
/// <c>/</c> create nested subdirectories automatically. Key characters are restricted to
/// alphanumeric, <c>.</c>, <c>_</c>, <c>-</c>, and <c>/</c>.</para>
///
/// <para><b>Case-distinct keys:</b> keys are case-sensitive, and distinct keys map to distinct
/// files even on case-insensitive volumes (default macOS APFS, NTFS). A path segment containing
/// an upper-case letter is stored with an exact case mask appended after <c>@</c> (for example
/// key <c>data/AAPL.json</c> is stored as <c>data/AAPL.json@f00@obj</c>); all-lower-case directory
/// segments are stored literally.</para>
///
/// <para><b>Prefix-related keys:</b> keys are opaque, so <c>a</c> and <c>a/b</c> are independent
/// objects that coexist in either write order, as in the in-memory store. The final segment of
/// every key (the object file) carries an <c>@obj</c> suffix, which keeps object files and the
/// directories created for longer keys in disjoint name spaces.</para>
///
/// <para><b>Path-traversal containment:</b> the character whitelist alone does NOT prevent
/// traversal — it permits both <c>..</c> segments and a leading <c>/</c>, either of which can
/// escape the root (a leading <c>/</c> is especially dangerous because <see cref="Path.Combine(string,string)"/>
/// discards the root entirely when its second argument is rooted). Containment is enforced in
/// two layers: key validation rejects <c>..</c> segments and leading <c>/</c>, and every resolved
/// path is canonicalized via <see cref="Path.GetFullPath(string)"/>, then resolved to its physical
/// location (every symlink in the path, including those inside other links' targets, is expanded)
/// and asserted to lie under the physically resolved root before any filesystem access. A key that
/// resolves outside the root raises <see cref="ArgumentException"/>.</para>
///
/// <para><b>Atomicity:</b> writes go to a temp file in the same directory, then are renamed
/// to the final path. On POSIX systems, rename is atomic within the same filesystem.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026),
/// Ch. 11 — "Batch Processing": using object storage for durable batch outputs.</para>
/// </summary>
public sealed partial class FileSystemObjectStore : IObjectStore
{
    // Suffix of every object file's final path segment. '@' cannot occur in a key, and 'o' is
    // not a case-mask hex digit, so no directory segment ever ends with this marker.
    private const string LeafMarker = "@obj";

    // POSIX caps symlink expansion per lookup (SYMLOOP_MAX, commonly 40); a longer chain is a cycle.
    private const int MaxSymlinkHops = 40;

    private readonly string _rootDir;
    private readonly string _realRoot;

    // Test-only observability (InternalsVisibleTo). Counts how many times a delete fsync'd the
    // containing directory, so a test can pin that the unlink is made durable. POSIX-only.
    internal int _deleteDirectoryFsyncCount;

    // Test-only observability: number of ancestor directory LEVELS created durably by writes
    // of nested keys (each created level's parent fsync'd — see DurableFile.CreateDirectoryDurable).
    internal int _ancestorDirectoriesCreatedDurably;

    // Test-only observability: number of root-path directory LEVELS the constructor created
    // (each created level's parent fsync'd). 0 when the root already existed.
    internal int _rootDirectoriesCreatedDurably;

    /// <summary>
    /// Initializes a new instance of <see cref="FileSystemObjectStore"/>.
    /// </summary>
    /// <param name="rootDirectory">The root directory for object storage. Created if it does not exist.</param>
    public FileSystemObjectStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        // Canonicalize the root once so containment checks in ResolvePath compare
        // against an absolute, normalized prefix rather than a caller-supplied
        // relative or denormalized path.
        _rootDir = Path.GetFullPath(rootDirectory);
        // The root (and any missing ancestors) must have each created entry fsync'd: later writes
        // only fsync levels BELOW the by-then-existing root, so a bare CreateDirectory would leave
        // the root's own entries in the page cache and a crash could drop the whole tree.
        _rootDirectoriesCreatedDurably = DurableFile.CreateDirectoryDurable(_rootDir);

        // The symlink-resolved real root. Path.GetFullPath is lexical and does not follow
        // symlinks, so the second containment layer in ResolvePath compares symlink-resolved
        // real paths against this (e.g. on macOS the temp root itself sits under /private).
        _realRoot = ResolveRealPath(_rootDir);
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
        // AC-8.4 (F-21): a non-readable stream (e.g. write-only FileStream) causes CopyToAsync
        // to throw NotSupportedException at copy time, silently producing an empty object file.
        // Reject it upfront with a contract-typed ArgumentException so callers diagnose the
        // error at the correct call site rather than inside the atomic-write temp-file logic.
        if (!content.CanRead)
        {
            throw new ArgumentException(
                "The content stream must be readable. Pass a stream with CanRead == true.", nameof(content));
        }

        ct.ThrowIfCancellationRequested();

        var path = ResolvePath(key);
        var dir = Path.GetDirectoryName(path)!;
        // Nested keys create ancestor directories; their entries must be durable BEFORE the
        // object write acks, or a crash can drop the whole fresh subtree with the acked object
        // in it (the AtomicReplace below fsyncs only the immediate containing directory).
        _ancestorDirectoriesCreatedDurably += DurableFile.CreateDirectoryDurable(dir);

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

            // Route through DurableFile.AtomicReplace so the containing directory is fsync'd after
            // the rename (a bare File.Move leaves the rename itself non-durable — a crash can expose
            // the pre-write file on restart). Matches FileCheckpointStore and the SSTable path.
            DurableFile.AtomicReplace(tempPath, path);
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

            // Make the unlink durable: an atomic write fsyncs the directory after the rename
            // (DurableFile.AtomicReplace), but a bare File.Delete leaves the removal of the
            // directory entry non-durable. A crash after the unlink returns but before the
            // directory write lands can resurrect the deleted object on restart. Match the
            // write path's durability by fsyncing the containing directory on POSIX (NTFS
            // makes the directory update durable on its own).
            if (!OperatingSystem.IsWindows())
            {
                DurableFile.FsyncDirectory(path);
                _deleteDirectoryFsyncCount++;
            }
        }

        return default;
    }

    private string ResolvePath(string key)
    {
        var combined = Path.Combine(_rootDir, EncodeKeyPath(key));
        var full = Path.GetFullPath(combined);

        // Load-bearing containment guard (layer 1 of 2): lexical canonicalization collapses any
        // residual '.', '..', or redundant separators and rejects a resolved path that does not
        // equal the root or sit strictly beneath it. It does NOT follow symlinks — the real-path
        // check below is layer 2 for a symlinked ancestor that escapes the root.
        if (!full.Equals(_rootDir, StringComparison.Ordinal)
            && !full.StartsWith(_rootDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Key resolves outside the object-store root.", nameof(key));
        }

        // Second layer: lexical canonicalization above does not follow symlinks, so an ancestor
        // directory that is a symlink pointing outside the root would pass the lexical check yet
        // resolve outside. Resolve the real (symlink-followed) path and re-assert containment.
        var real = ResolveRealPath(full);
        if (!real.Equals(_realRoot, StringComparison.Ordinal)
            && !real.StartsWith(_realRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Key resolves outside the object-store root (via a symlinked ancestor).", nameof(key));
        }

        return full;
    }

    /// <summary>
    /// Maps a validated key to its relative on-disk path, injectively even on a case-INSENSITIVE
    /// volume (default macOS APFS, NTFS). Keys are case-sensitive opaque identifiers, but such a
    /// volume folds "report" and "Report" (or the directories "data/" and "Data/") onto one entry,
    /// so a literal mapping lets one key's write or delete clobber another key's object. Every
    /// segment containing an upper-case letter therefore gets an exact case mask appended after
    /// '@' (a character keys cannot contain): one lower-case hex digit per four characters, bit j
    /// of digit n set when character 4n+j is upper-case. All-lower-case segments stay literal.
    /// Two segments that fold together share their lower-cased text and length, so their masks
    /// differ exactly when their cases do — the mapping is injective, not merely hash-unique.
    /// The final segment (the object file) additionally carries <see cref="LeafMarker"/>, so an
    /// object file can never share a name with a directory created for a longer key: keys "a" and
    /// "a/b" coexist (as in the in-memory sibling) instead of one needing "a" to be both a file
    /// and a directory. Directory segments never contain the marker ('@' is followed only by
    /// hex mask digits there), so the leaf and directory name spaces are disjoint.
    /// </summary>
    private static string EncodeKeyPath(string key)
    {
        var segments = key.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var isLeaf = i == segments.Length - 1;
            if (!segment.Any(char.IsAsciiLetterUpper))
            {
                if (isLeaf)
                {
                    segments[i] = segment + LeafMarker;
                }

                continue;
            }

            var mask = new char[(segment.Length + 3) / 4];
            for (var n = 0; n < mask.Length; n++)
            {
                var nibble = 0;
                for (var j = 0; j < 4 && (4 * n) + j < segment.Length; j++)
                {
                    if (char.IsAsciiLetterUpper(segment[(4 * n) + j]))
                    {
                        nibble |= 1 << j;
                    }
                }

                mask[n] = "0123456789abcdef"[nibble];
            }

            segments[i] = segment + "@" + new string(mask) + (isLeaf ? LeafMarker : string.Empty);
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    /// <summary>
    /// Computes the physical (realpath-style) location of <paramref name="path"/> by walking it one
    /// component at a time and expanding every symlink it meets. <see cref="Path.GetFullPath(string)"/>
    /// is lexical only; an ancestor symlink can redirect the real location of an otherwise
    /// lexically-contained path. A link's target is itself a path that may cross further symlinks
    /// and may climb with <c>..</c>, so the target's components are pushed back onto the pending
    /// queue and resolved the same way, rather than trusting the target's own string. A dangling
    /// link is expanded too (a later directory creation or write would follow it out of the root).
    /// Components that are not links, including trailing ones that do not exist yet, are appended
    /// as-is. A chain longer than <see cref="MaxSymlinkHops"/> links is treated as a cycle.
    /// </summary>
    private static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return full;
        }

        var resolved = root;
        var pending = new Stack<string>();
        PushSegments(pending, full[root.Length..]);
        var hops = 0;
        while (pending.Count > 0)
        {
            var segment = pending.Pop();
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                // Physical parent of the directory resolved so far; the root is its own parent.
                resolved = Path.GetDirectoryName(resolved) ?? resolved;
                continue;
            }

            var candidate = Path.Combine(resolved, segment);
            var linkTarget = new FileInfo(candidate).LinkTarget;
            if (linkTarget is null)
            {
                resolved = candidate;
                continue;
            }

            if (++hops > MaxSymlinkHops)
            {
                throw new IOException(
                    $"Too many levels of symbolic links while resolving '{path}'.");
            }

            // An absolute target restarts from its own root; a relative target is interpreted
            // against the directory that holds the link (resolved is still that directory).
            var targetRoot = Path.GetPathRoot(linkTarget);
            if (!string.IsNullOrEmpty(targetRoot))
            {
                resolved = targetRoot;
                PushSegments(pending, linkTarget[targetRoot.Length..]);
            }
            else
            {
                PushSegments(pending, linkTarget);
            }
        }

        return resolved;
    }

    // Pushes the components of a path so that the first component is popped first.
    private static void PushSegments(Stack<string> pending, string relativePath)
    {
        var parts = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            pending.Push(parts[i]);
        }
    }

    // Internal so the in-memory sibling enforces the IDENTICAL key contract — swapping
    // IObjectStore implementations must not change which keys are valid.
    internal static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!AllowedKeyPattern().IsMatch(key))
        {
            throw new ArgumentException(
                "Key contains invalid characters. Allowed: alphanumeric, '.', '_', '-', '/'.",
                nameof(key));
        }

        // Defense-in-depth path-traversal rejection. The whitelist permits both of these
        // forms, and each escapes the root: a leading '/' makes the key absolute (which
        // Path.Combine honors by discarding the root), and a '..' segment walks upward.
        // ResolvePath would catch the escape anyway, but rejecting here gives a precise
        // error at the API boundary and keeps the two layers independent.
        if (key[0] == '/')
        {
            throw new ArgumentException(
                "Key must be relative; a leading '/' is not allowed.", nameof(key));
        }

        foreach (var segment in key.Split('/'))
        {
            // Keys are OPAQUE identifiers: distinct keys must map to distinct objects. "."
            // and empty segments are collapsed by filesystem path resolution, silently
            // aliasing "a/./b" and "a//b" onto "a/b" (three distinct keys, one file's
            // Exists/Delete/LWW state) — and diverging from the in-memory sibling, which
            // would store three separate entries. Reject the degenerate forms.
            if (segment.Length == 0 || segment == ".")
            {
                throw new ArgumentException(
                    "Key must not contain empty ('//') or '.' path segments — they alias distinct keys onto one object.",
                    nameof(key));
            }

            if (segment == "..")
            {
                throw new ArgumentException(
                    "Key must not contain '..' path segments.", nameof(key));
            }
        }
    }

    [GeneratedRegex(@"^[a-zA-Z0-9._\-/]+$")]
    private static partial Regex AllowedKeyPattern();
}
