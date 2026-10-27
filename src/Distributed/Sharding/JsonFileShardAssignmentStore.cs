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
using Boutquin.Storage.Infrastructure.Schemas;

namespace Boutquin.Storage.Distributed.Sharding;

/// <summary>Persists shard-map snapshots as an atomically replaced JSON file.</summary>
/// <remarks>Same-file instances in one process share one optimistic-concurrency gate, including
/// spellings that differ only by symbolic links or letter case. Content that parses but violates a
/// snapshot invariant is reported as <see cref="InvalidDataException"/>. A snapshot that would not read back
/// as written (an identifier with an unpaired surrogate, which JSON persists as U+FFFD) is refused with
/// <see cref="ArgumentException"/> before the file is replaced.</remarks>
public sealed class JsonFileShardAssignmentStore : IShardAssignmentStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_pathGates =
        new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _pathGate;

    /// <summary>Initializes a store at <paramref name="path"/>.</summary>
    public JsonFileShardAssignmentStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _pathGate = s_pathGates.GetOrAdd(FileCoordinationKey.For(Path), static _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>Gets the snapshot path.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public async Task<ShardMapSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pathGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> TrySaveAsync(
        ShardMapSnapshot snapshot,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _pathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var currentVersion = current?.Version ?? -1;
            if (currentVersion != expectedVersion)
            {
                return false;
            }

            if (snapshot.Version < 0 || (expectedVersion >= 0 && snapshot.Version != expectedVersion + 1))
            {
                throw new ArgumentException(
                    $"Snapshot version {snapshot.Version} must immediately follow expected version {expectedVersion}.",
                    nameof(snapshot));
            }

            var content = JsonSerializer.SerializeToUtf8Bytes(snapshot, s_jsonOptions);
            EnsureReadsBackAsWritten(snapshot, content);

            // rename(2) replaces a symbolic link rather than writing through it; replacing the link's
            // target keeps every spelling the gate coordinates on one physical snapshot.
            var physicalPath = FileCoordinationKey.ResolvePhysicalPath(Path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(physicalPath)!);
            var temporaryPath = $"{physicalPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, physicalPath, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }

            return true;
        }
        finally
        {
            _pathGate.Release();
        }
    }

    // A true result means the snapshot is the durable current version, so it must be the snapshot the
    // next load returns. JSON writes an unpaired surrogate as U+FFFD: two distinct shard ids would persist
    // as one (wedging every later load and save) and a node id would route to a node nobody owns.
    private static void EnsureReadsBackAsWritten(ShardMapSnapshot snapshot, byte[] content)
    {
        string? difference;
        try
        {
            var reloaded = JsonSerializer.Deserialize<ShardMapSnapshot>(content, s_jsonOptions);
            difference = JsonRoundTripIdentity.FindDifference(snapshot, reloaded, s_jsonOptions);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        {
            difference = error.Message;
        }

        if (difference is not null)
        {
            throw new ArgumentException(
                $"The snapshot does not read back as written ({difference}); the store was left unchanged.",
                nameof(snapshot));
        }
    }

    private async Task<ShardMapSnapshot?> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        var stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ShardMapSnapshot? snapshot;
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                snapshot = await JsonSerializer.DeserializeAsync<ShardMapSnapshot>(stream, s_jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException error)
            {
                // The snapshot constructors guard caller arguments; raised here, the argument is file content.
                throw new InvalidDataException($"Shard assignment snapshot '{Path}' violates a snapshot invariant.", error);
            }
        }

        if (snapshot is null)
        {
            throw new InvalidDataException($"Shard assignment snapshot '{Path}' is empty.");
        }

        var shardIds = new HashSet<string>(StringComparer.Ordinal);
        if (snapshot.Assignments.Any(assignment => !shardIds.Add(assignment.ShardId)))
        {
            throw new InvalidDataException($"Shard assignment snapshot '{Path}' assigns a shard more than once.");
        }

        return snapshot;
    }
}
