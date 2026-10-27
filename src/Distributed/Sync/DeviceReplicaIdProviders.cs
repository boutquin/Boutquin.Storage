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

namespace Boutquin.Storage.Distributed.Sync;

/// <summary>Returns a caller-supplied stable device identifier.</summary>
public sealed class FixedDeviceReplicaIdProvider : IDeviceReplicaIdProvider
{
    private readonly string _replicaId;

    /// <summary>Initializes a provider with a non-empty identifier.</summary>
    /// <exception cref="ArgumentException">The identifier is empty or not well-formed UTF-16.</exception>
    public FixedDeviceReplicaIdProvider(string replicaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replicaId);
        JsonRoundTripIdentity.ThrowIfIllFormedText(replicaId, nameof(replicaId));
        _replicaId = replicaId;
    }

    /// <inheritdoc />
    public Task<string> GetReplicaIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_replicaId);
    }
}

/// <summary>Persists a generated device identifier in a dedicated text file.</summary>
/// <remarks>Concurrent first-run callers agree on one identifier: same-file instances in one process
/// share a gate, and the file is created exclusively so a competing process adopts the winner.</remarks>
public sealed class FileDeviceReplicaIdProvider : IDeviceReplicaIdProvider
{
    private const int ContentWaitAttempts = 50;
    private static readonly TimeSpan s_contentWaitInterval = TimeSpan.FromMilliseconds(20);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_pathGates =
        new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly SemaphoreSlim _gate;

    /// <summary>Initializes a provider without touching the supplied file path.</summary>
    public FileDeviceReplicaIdProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _gate = s_pathGates.GetOrAdd(FileCoordinationKey.For(_path), static _ => new SemaphoreSlim(1, 1));
    }

    /// <inheritdoc />
    public async Task<string> GetReplicaIdAsync(CancellationToken cancellationToken = default)
    {
        // A per-instance gate let two providers for one file both observe absence, both mint an
        // identifier, and the later write replace the earlier caller's "stable" identity.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_path))
            {
                return await ReadExistingAsync(cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var replicaId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

            // The identifier is written and flushed under a private name, then published with a
            // non-overwriting move. A cancellation or failure before the publish leaves no identity
            // file at all, instead of an empty one that every later call would reject.
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                var stream = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(replicaId), cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                try
                {
                    // overwrite: false is an exclusive publish: another process cannot also win the first run.
                    File.Move(temporaryPath, _path, overwrite: false);
                }
                catch (IOException) when (File.Exists(_path))
                {
                    return await ReadExistingAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                File.Delete(temporaryPath);
            }

            return replicaId;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> ReadExistingAsync(CancellationToken cancellationToken)
    {
        // A competing process may have created the file and not yet written it; an identifier that
        // stays empty is corruption (for example a crash between create and write).
        for (var attempt = 1; ; attempt++)
        {
            var existing = (await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false)).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            if (attempt == ContentWaitAttempts)
            {
                throw new InvalidDataException($"Device identifier file '{_path}' is empty.");
            }

            await Task.Delay(s_contentWaitInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
