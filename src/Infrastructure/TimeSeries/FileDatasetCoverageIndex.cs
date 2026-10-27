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
using System.Text.Json;
using Boutquin.Storage.Domain.Helpers;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Infrastructure.KeyValueStore;

namespace Boutquin.Storage.Infrastructure.TimeSeries;

/// <summary>
/// File-backed implementation of <see cref="IDatasetCoverageIndex{TDatasetKey}"/>.
/// Single JSON manifest per store, written atomically via temp-file + rename.
/// </summary>
/// <remarks>
/// <para><b>Format:</b> top-level <c>schemaVersion: 1</c> plus a list of dataset entries,
/// each carrying a base64-encoded dataset key (using the dataset's own
/// <see cref="ISerializable{T}"/> surface) and the <c>from</c>/<c>to</c> ISO-date bounds.
/// A manifest whose <c>schemaVersion</c> is absent or unknown is treated as corrupt —
/// <see cref="GetAsync"/> returns <c>null</c> and the enclosing store rebuilds via
/// <see cref="IDatasetCoverageIndex{TDatasetKey}.RebuildAsync"/>.</para>
/// <para><b>Why base64 key serialisation:</b> <typeparamref name="TDatasetKey"/> can carry
/// bytes that are not JSON-safe (binary struct layouts, control characters in strings).
/// Base64 over the same byte stream the LSM engine writes to disk means the manifest key
/// is identical after every re-serialisation round-trip, regardless of dataset type.</para>
/// </remarks>
/// <typeparam name="TDatasetKey">The dataset partition key type.</typeparam>
public sealed class FileDatasetCoverageIndex<TDatasetKey>
    : IDatasetCoverageIndex<TDatasetKey>
    where TDatasetKey : IComparable<TDatasetKey>, ISerializable<TDatasetKey>, new()
{
    private const int CurrentSchemaVersion = 1;

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (TDatasetKey Key, DateRange Range)> _state = new(StringComparer.Ordinal);
    private bool _loaded;
    // Set to the load-failure exception when the on-disk manifest was corrupt or unreadable.
    // While set, mutating operations (RegisterAsync/SetAsync) throw rather than silently
    // overwriting recoverable data. Cleared by RebuildAsync, which derives fresh state from
    // a full scan and persists a clean manifest.
    private CorruptCoverageManifestException? _corrupt;

    /// <summary>
    /// Initializes a new instance that reads and writes the manifest at <paramref name="path"/>.
    /// The file is loaded lazily on first operation; if the parent directory does not exist,
    /// it is created on first write.
    /// </summary>
    public FileDatasetCoverageIndex(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc />
    public async Task<DateRange?> GetAsync(TDatasetKey dataset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = EncodeKey(dataset);
            return _state.TryGetValue(key, out var entry) ? entry.Range : (DateRange?)null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RegisterAsync(
        TDatasetKey dataset,
        DateOnly min,
        DateOnly max,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The loader treats an inverted persisted range as corruption, so never persist one.
        if (min > max)
        {
            throw new ArgumentException(
                $"Coverage interval is inverted: min {min:O} is after max {max:O}.", nameof(min));
        }

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfCorruptLocked();
            var key = EncodeKey(dataset);
            if (_state.TryGetValue(key, out var existing))
            {
                var merged = new DateRange(
                    existing.Range.From < min ? existing.Range.From : min,
                    existing.Range.To > max ? existing.Range.To : max);
                _state[key] = (existing.Key, merged);
            }
            else
            {
                _state[key] = (dataset, new DateRange(min, max));
            }

            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(
        TDatasetKey dataset,
        DateRange? coverage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The loader treats an inverted persisted range as corruption, so never persist one.
        if (coverage is { } range && range.From > range.To)
        {
            throw new ArgumentException(
                $"Coverage range is inverted: From {range.From:O} is after To {range.To:O}.", nameof(coverage));
        }

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfCorruptLocked();
            var key = EncodeKey(dataset);
            if (coverage is null)
            {
                _state.Remove(key);
            }
            else
            {
                _state[key] = (dataset, coverage.Value);
            }

            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RebuildAsync(
        IAsyncEnumerable<(TDatasetKey Dataset, DateOnly Date)> scan,
        CancellationToken cancellationToken = default)
    {
        var rebuilt = new Dictionary<string, (TDatasetKey Key, DateOnly Min, DateOnly Max)>(StringComparer.Ordinal);

        await foreach (var (ds, date) in scan.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var k = EncodeKey(ds);
            if (rebuilt.TryGetValue(k, out var cur))
            {
                rebuilt[k] = (
                    cur.Key,
                    date < cur.Min ? date : cur.Min,
                    date > cur.Max ? date : cur.Max);
            }
            else
            {
                rebuilt[k] = (ds, date, date);
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _state.Clear();
            foreach (var kvp in rebuilt)
            {
                _state[kvp.Key] = (kvp.Value.Key, new DateRange(kvp.Value.Min, kvp.Value.Max));
            }

            // RebuildAsync is the explicit recovery path: clear the corrupt flag before
            // persisting so the fresh manifest is written without obstruction.
            _corrupt = null;
            _loaded = true;
            await PersistLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            TryLoadLocked();
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TryLoadLocked()
    {
        _state.Clear();

        if (!File.Exists(_path))
        {
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        // Access-failure family: IOException covers transient I/O errors; UnauthorizedAccessException
        // (permission denied / read-only) and PathTooLongException are NOT IOException subclasses, so
        // they must be funneled into the SAME fail-closed contract rather than escaping raw. Set
        // _corrupt so mutating operations throw rather than silently overwriting potentially
        // recoverable data; callers recover via RebuildAsync.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            var failure = new CorruptCoverageManifestException(
                _path,
                $"[FileDatasetCoverageIndex] Manifest read failure at '{_path}': {ex.Message}",
                ex);
            System.Diagnostics.Debug.WriteLine(failure.Message);
            _corrupt = failure;
            return;
        }

        ManifestDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ManifestDto>(text, s_serializerOptions);
        }
        catch (JsonException ex)
        {
            var failure = new CorruptCoverageManifestException(
                _path,
                $"[FileDatasetCoverageIndex] Manifest parse failure at '{_path}': {ex.Message}",
                ex);
            System.Diagnostics.Debug.WriteLine(failure.Message);
            _corrupt = failure;
            return;
        }

        if (dto is null || dto.SchemaVersion != CurrentSchemaVersion || dto.Datasets is null)
        {
            var failure = new CorruptCoverageManifestException(
                _path,
                dto is null
                    ? $"[FileDatasetCoverageIndex] Manifest at '{_path}' deserialised to null."
                    : dto.SchemaVersion != CurrentSchemaVersion
                        ? $"[FileDatasetCoverageIndex] Manifest at '{_path}' has unsupported schema version {dto.SchemaVersion} (expected {CurrentSchemaVersion})."
                        : $"[FileDatasetCoverageIndex] Manifest at '{_path}' has null datasets list.");
            System.Diagnostics.Debug.WriteLine(failure.Message);
            _corrupt = failure;
            return;
        }

        foreach (var entry in dto.Datasets)
        {
            // System.Text.Json deserialises `"datasets": [ null ]` into a null element; treat it
            // as corruption like a null key so the load fails closed instead of escaping an NRE
            // (which would leave _loaded false and re-throw from every later operation).
            if (entry is null)
            {
                _state.Clear();
                var failure = new CorruptCoverageManifestException(
                    _path,
                    $"[FileDatasetCoverageIndex] Manifest at '{_path}' has a null dataset entry.");
                System.Diagnostics.Debug.WriteLine(failure.Message);
                _corrupt = failure;
                return;
            }

            // A null or un-decodable key means the on-disk manifest cannot be fully trusted.
            // Failing the whole load CLOSED (rather than silently skipping the row) is the
            // correctness choice: a silent skip leaves _state holding a STRICT SUBSET of the
            // persisted rows, and the next RegisterAsync/SetAsync would persist that lossy
            // subset back over the manifest — permanently dropping the un-decodable row.
            // Fail closed so a write throws and the recoverable bytes survive until RebuildAsync.
            if (entry.Key is null)
            {
                _state.Clear();
                var failure = new CorruptCoverageManifestException(
                    _path,
                    $"[FileDatasetCoverageIndex] Manifest at '{_path}' has a dataset entry with a null key.");
                System.Diagnostics.Debug.WriteLine(failure.Message);
                _corrupt = failure;
                return;
            }

            TDatasetKey ds;
            try
            {
                ds = DecodeKey(entry.Key);
            }
            catch (Exception ex)
            {
                _state.Clear();
                var failure = new CorruptCoverageManifestException(
                    _path,
                    $"[FileDatasetCoverageIndex] Manifest at '{_path}' has an un-decodable dataset entry key='{entry.Key}': {ex.Message}",
                    ex);
                System.Diagnostics.Debug.WriteLine(failure.Message);
                _corrupt = failure;
                return;
            }

            // An absent, null or inverted date is corruption, not coverage: an absent property
            // would otherwise load as DateOnly's default (0001-01-01) and an inverted pair as an
            // empty-but-present range, and either is served by the store as the cached coverage
            // instead of scanning the engine. Fail closed like an un-decodable key.
            if (entry.From is not { } from || entry.To is not { } to || from > to)
            {
                _state.Clear();
                var failure = new CorruptCoverageManifestException(
                    _path,
                    $"[FileDatasetCoverageIndex] Manifest at '{_path}' has a dataset entry key='{entry.Key}' with an absent or inverted date range (from={entry.From?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "<absent>"}, to={entry.To?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "<absent>"}).");
                System.Diagnostics.Debug.WriteLine(failure.Message);
                _corrupt = failure;
                return;
            }

            // Re-key through EncodeKey(ds) rather than entry.Key (the raw JSON string).
            // Both values should be byte-identical in practice (EncodeKey is deterministic),
            // but routing every access path through the same encoder removes the coupling
            // assumption and ensures the loaded key matches every future lookup.
            _state[EncodeKey(ds)] = (ds, new DateRange(from, to));
        }
    }

    /// <summary>
    /// Throws <see cref="CorruptCoverageManifestException"/> if the manifest loaded with errors.
    /// Must be called inside the semaphore (<c>_gate</c> already acquired by the caller).
    /// </summary>
    private void ThrowIfCorruptLocked()
    {
        if (_corrupt is not null)
        {
            throw _corrupt;
        }
    }

    private async Task PersistLockedAsync(CancellationToken cancellationToken)
    {
        // Guard: never overwrite a corrupt on-disk manifest with partial in-memory state.
        // This path is only reached from RegisterAsync/SetAsync (which call ThrowIfCorruptLocked
        // first) and RebuildAsync (which clears _corrupt before calling us), so this check is
        // belt-and-suspenders — it prevents an unexpected call path from destroying data.
        if (_corrupt is not null)
        {
            throw _corrupt;
        }

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var dto = new ManifestDto
        {
            SchemaVersion = CurrentSchemaVersion,
            Datasets = [.. _state
                .Select(kvp => new DatasetEntry
                {
                    Key = kvp.Key,
                    From = kvp.Value.Range.From,
                    To = kvp.Value.Range.To,
                })],
        };

        var tempPath = _path + $".tmp.{Guid.NewGuid():N}";
        try
        {
            var fs = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            try
            {
                await JsonSerializer.SerializeAsync(fs, dto, s_serializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }
            finally
            {
                await fs.DisposeAsync().ConfigureAwait(false);
            }

            DurableFile.AtomicReplace(tempPath, _path);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* swallow */ }
            }

            throw;
        }
    }

    private static string EncodeKey(TDatasetKey dataset)
        => Convert.ToBase64String(SerializedKeyEqualityComparer<TDatasetKey>.ToBytes(dataset));

    private static TDatasetKey DecodeKey(string encoded)
    {
        var bytes = Convert.FromBase64String(encoded);
        using var ms = new MemoryStream(bytes);
        return TDatasetKey.Deserialize(ms);
    }

    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class ManifestDto
    {
        public int SchemaVersion { get; set; }
        public List<DatasetEntry>? Datasets { get; set; }
    }

    private sealed class DatasetEntry
    {
        public string? Key { get; set; }
        // Nullable so an absent property is observable at load time instead of silently
        // defaulting to 0001-01-01.
        public DateOnly? From { get; set; }
        public DateOnly? To { get; set; }
    }
}

/// <summary>
/// The time-series family's one definition of key identity: two keys name the same key exactly
/// when their validated <see cref="ISerializable{T}.Serialize"/> bytes are identical.
/// </summary>
/// <remarks>
/// <para>
/// The dataset-key constraint asks only for <see cref="IComparable{T}"/> and
/// <see cref="ISerializable{T}"/>, so a key's own <see cref="object.Equals(object?)"/> may be
/// reference equality. Hash containers keyed by dataset keys (or by
/// <see cref="Boutquin.Storage.Domain.ValueObjects.TimeSeries.TimeSeriesKey{TDatasetKey}"/>, which
/// serializes as the dataset bytes followed by the date) therefore use this comparer instead of the
/// default one; otherwise a key read back from disk, or a second instance naming the same dataset,
/// would miss an entry written under another instance. Serialized bytes are already the identity
/// the persisted coverage manifest and every segment footer use.
/// <see cref="DatasetKeyEncoding"/> rejects encodings that fail to preserve comparison identity
/// or their exact bytes across deserialization, so byte identity agrees with the composite key's
/// comparer for every accepted dataset. A malformed lookup is rejected too, never canonicalized
/// into the identity of a valid neighbor.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The serializable key type.</typeparam>
internal sealed class SerializedKeyEqualityComparer<TKey> : IEqualityComparer<TKey>
    where TKey : ISerializable<TKey>, IComparable<TKey>
{
    private SerializedKeyEqualityComparer()
    {
    }

    /// <summary>Gets the shared stateless instance.</summary>
    public static SerializedKeyEqualityComparer<TKey> Instance { get; } = new();

    /// <inheritdoc />
    public bool Equals(TKey? x, TKey? y)
    {
        if (x is null || y is null)
        {
            return x is null && y is null;
        }

        return ToBytes(x).AsSpan().SequenceEqual(ToBytes(y));
    }

    /// <inheritdoc />
    public int GetHashCode([System.Diagnostics.CodeAnalysis.DisallowNull] TKey obj)
    {
        var hash = new HashCode();
        hash.AddBytes(ToBytes(obj));
        return hash.ToHashCode();
    }

    /// <summary>Returns the key's serialized bytes — the identity this comparer compares.</summary>
    internal static byte[] ToBytes(TKey key) => DatasetKeyEncoding.SerializeValidated(key);
}
