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
using Boutquin.Storage.Domain.Interfaces.Streaming;
using Boutquin.Storage.Domain.ValueObjects.Streaming;

namespace Boutquin.Storage.Infrastructure.Streaming;

/// <summary>
/// Performs keyed event-time aggregation and emits windows when a monotonic watermark closes them.
/// Session windows merge overlapping provisional windows with the supplied accumulator combiner.
/// </summary>
/// <typeparam name="TIn">The input event value type.</typeparam>
/// <typeparam name="TKey">The non-null grouping-key type.</typeparam>
/// <typeparam name="TAccumulator">
/// The immutable aggregate state and emitted result type. Delegates must return fresh state instead
/// of mutating an accumulator that may already have been published.
/// </typeparam>
/// <remarks>
/// <para>One active enumeration is permitted per instance. Late session bridges emit retractions for
/// superseded windows before an upsert for the merged replacement.</para>
/// <para><b>Producers.</b> A window is emitted only once the completeness threshold has passed its end
/// across all participating producers (DDIA 2e Ch. 12, "Reasoning About Time"). The constructor's
/// single watermark policy tracks one producer's threshold, so a run refuses records naming two
/// different <see cref="StreamRecord{T}.Producer"/> identities. Input merged from several producers
/// uses <see cref="ForProducers"/>, which tracks each declared producer's threshold with its own
/// policy, closes windows on the least advanced one, and refuses records from undeclared producers.</para>
/// </remarks>
public sealed class WindowedAggregator<TIn, TKey, TAccumulator>
    : IStreamProcessor<StreamRecord<TIn>, WindowedAggregate<TKey, TAccumulator>>
    where TKey : notnull
{
    private readonly IWindowAssigner _windowAssigner;
    private readonly IWatermarkPolicy? _watermarkPolicy;
    private readonly Dictionary<string, IWatermarkPolicy>? _producerWatermarkPolicies;
    private readonly LateEventPolicy _lateEventPolicy;
    private readonly Func<TIn, TKey> _keySelector;
    private readonly Func<TAccumulator> _seed;
    private readonly Func<TAccumulator, TIn, TAccumulator> _accumulate;
    private readonly Func<TAccumulator, TAccumulator, TAccumulator>? _combine;
    private int _running;

    /// <summary>Creates a keyed window aggregator that borrows its policy and delegates.</summary>
    /// <param name="windowAssigner">Maps each timestamp to fixed or provisional windows.</param>
    /// <param name="watermarkPolicy">Tracks event-time completeness for this processor instance.</param>
    /// <param name="lateEventPolicy">Controls records behind the current watermark.</param>
    /// <param name="keySelector">Returns the grouping key for one input value.</param>
    /// <param name="seed">Creates a fresh immutable empty accumulator.</param>
    /// <param name="accumulate">Purely returns the next accumulator without mutating its input.</param>
    /// <param name="combine">Purely merges session accumulators; required for session windows.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required collaborator is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when a session assigner has no <paramref name="combine"/> delegate.</exception>
    public WindowedAggregator(
        IWindowAssigner windowAssigner,
        IWatermarkPolicy watermarkPolicy,
        LateEventPolicy lateEventPolicy,
        Func<TIn, TKey> keySelector,
        Func<TAccumulator> seed,
        Func<TAccumulator, TIn, TAccumulator> accumulate,
        Func<TAccumulator, TAccumulator, TAccumulator>? combine = null)
        : this(
            windowAssigner,
            watermarkPolicy ?? throw new ArgumentNullException(nameof(watermarkPolicy)),
            null,
            lateEventPolicy,
            keySelector,
            seed,
            accumulate,
            combine)
    {
    }

    /// <summary>
    /// Creates a keyed window aggregator over records merged from several producers, tracking each
    /// producer's completeness threshold with its own watermark policy.
    /// </summary>
    /// <param name="windowAssigner">Maps each timestamp to fixed or provisional windows.</param>
    /// <param name="producerWatermarkPolicies">
    /// One distinct watermark policy per participating producer, keyed by the ordinal
    /// <see cref="StreamRecord{T}.Producer"/> identity. A window closes only once every listed
    /// producer's watermark has passed its end; records from an unlisted producer are refused.
    /// </param>
    /// <param name="lateEventPolicy">Controls records behind the combined watermark.</param>
    /// <param name="keySelector">Returns the grouping key for one input value.</param>
    /// <param name="seed">Creates a fresh immutable empty accumulator.</param>
    /// <param name="accumulate">Purely returns the next accumulator without mutating its input.</param>
    /// <param name="combine">Purely merges session accumulators; required for session windows.</param>
    /// <returns>An aggregator that closes a window only once every participating producer has passed its end.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required collaborator is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when one policy instance is listed for two producers, or a session assigner has no
    /// <paramref name="combine"/> delegate.
    /// </exception>
    public static WindowedAggregator<TIn, TKey, TAccumulator> ForProducers(
        IWindowAssigner windowAssigner,
        IReadOnlyDictionary<string, IWatermarkPolicy> producerWatermarkPolicies,
        LateEventPolicy lateEventPolicy,
        Func<TIn, TKey> keySelector,
        Func<TAccumulator> seed,
        Func<TAccumulator, TIn, TAccumulator> accumulate,
        Func<TAccumulator, TAccumulator, TAccumulator>? combine = null)
        => new(
            windowAssigner,
            null,
            CopyProducerPolicies(producerWatermarkPolicies),
            lateEventPolicy,
            keySelector,
            seed,
            accumulate,
            combine);

    private WindowedAggregator(
        IWindowAssigner windowAssigner,
        IWatermarkPolicy? watermarkPolicy,
        Dictionary<string, IWatermarkPolicy>? producerWatermarkPolicies,
        LateEventPolicy lateEventPolicy,
        Func<TIn, TKey> keySelector,
        Func<TAccumulator> seed,
        Func<TAccumulator, TIn, TAccumulator> accumulate,
        Func<TAccumulator, TAccumulator, TAccumulator>? combine)
    {
        _windowAssigner = windowAssigner ?? throw new ArgumentNullException(nameof(windowAssigner));
        _watermarkPolicy = watermarkPolicy;
        _producerWatermarkPolicies = producerWatermarkPolicies;
        _lateEventPolicy = lateEventPolicy;
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        _seed = seed ?? throw new ArgumentNullException(nameof(seed));
        _accumulate = accumulate ?? throw new ArgumentNullException(nameof(accumulate));
        _combine = combine;

        if (_windowAssigner.SessionGap is not null && _combine is null)
        {
            throw new ArgumentException(
                "Session windows require an accumulator combiner so overlapping sessions can merge.",
                nameof(combine));
        }
    }

    /// <summary>Gets the number of late events observed during the most recent execution.</summary>
    public long LateEventCount { get; private set; }

    /// <summary>Aggregates one finite input enumeration and flushes open windows at its end.</summary>
    /// <param name="input">The event-time input records.</param>
    /// <param name="ct">Cancels input enumeration and window output.</param>
    /// <returns>Final, upsert, and retraction changes in processing order.</returns>
    /// <exception cref="InvalidOperationException">
    /// Another run is active, a late event uses the throw policy, a single-policy run receives records
    /// from two producers, or a per-producer run receives a record from an undeclared producer.
    /// </exception>
    public async IAsyncEnumerable<WindowedAggregate<TKey, TAccumulator>> ProcessAsync(
        IAsyncEnumerable<StreamRecord<TIn>> input,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("A window aggregation run is already active.");
        }

        try
        {
            ResetWatermarks();
            LateEventCount = 0;
            var state = new Dictionary<(TKey Key, TimeWindow Window), AggregateState>();
            var singleProducer = new SingleProducerGuard();

            await foreach (var record in input.WithCancellation(ct).ConfigureAwait(false))
            {
                var producerPolicy = WatermarkPolicyFor(record, ref singleProducer);
                var currentWatermark = CombinedWatermark();
                var isLate = record.EventTime < currentWatermark;
                if (isLate)
                {
                    LateEventCount++;
                    if (_lateEventPolicy == LateEventPolicy.Drop)
                    {
                        continue;
                    }

                    if (_lateEventPolicy == LateEventPolicy.Throw)
                    {
                        throw new InvalidOperationException(
                            $"Event at {record.EventTime:O} is behind watermark "
                            + $"{currentWatermark:O}.");
                    }
                }

                var key = _keySelector(record.Value);
                var windows = _windowAssigner.AssignWindows(record.EventTime);
                foreach (var assignedWindow in windows)
                {
                    var window = assignedWindow;
                    var aggregate = _seed();
                    var wasEmitted = false;

                    if (_windowAssigner.SessionGap is not null)
                    {
                        var overlaps = state
                            .Where(pair => EqualityComparer<TKey>.Default.Equals(pair.Key.Key, key)
                                && WindowsOverlap(pair.Key.Window, assignedWindow))
                            .ToArray();
                        foreach (var overlap in overlaps)
                        {
                            window = new TimeWindow(
                                Min(window.StartInclusive, overlap.Key.Window.StartInclusive),
                                Max(window.EndExclusive, overlap.Key.Window.EndExclusive));
                            aggregate = _combine!(aggregate, overlap.Value.Value);
                            if (overlap.Value.Emitted)
                            {
                                yield return new WindowedAggregate<TKey, TAccumulator>(
                                    overlap.Key.Window,
                                    key,
                                    overlap.Value.Value,
                                    WindowEmissionKind.Retraction);
                                wasEmitted = true;
                            }

                            state.Remove(overlap.Key);
                        }
                    }
                    else if (state.TryGetValue((key, window), out var existing))
                    {
                        aggregate = existing.Value;
                        wasEmitted = existing.Emitted;
                    }

                    aggregate = _accumulate(aggregate, record.Value);
                    state[(key, window)] = new AggregateState(aggregate, wasEmitted);
                    if (wasEmitted)
                    {
                        yield return new WindowedAggregate<TKey, TAccumulator>(
                            window, key, aggregate, WindowEmissionKind.Upsert);
                    }
                }

                _ = producerPolicy.Observe(record.EventTime);
                var watermark = CombinedWatermark();
                foreach (var closed in GetClosedWindows(state, watermark, _windowAssigner.SessionGap is not null))
                {
                    state[closed.Key] = closed.Value with { Emitted = true };
                    yield return new WindowedAggregate<TKey, TAccumulator>(
                        closed.Key.Window, closed.Key.Key, closed.Value.Value, WindowEmissionKind.Final);
                }
            }

            foreach (var remaining in state
                .Where(pair => !pair.Value.Emitted)
                .OrderBy(pair => pair.Key.Window.EndExclusive)
                .ThenBy(pair => pair.Key.Window.StartInclusive))
            {
                yield return new WindowedAggregate<TKey, TAccumulator>(
                    remaining.Key.Window,
                    remaining.Key.Key,
                    remaining.Value.Value,
                    WindowEmissionKind.Final);
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static Dictionary<string, IWatermarkPolicy> CopyProducerPolicies(
        IReadOnlyDictionary<string, IWatermarkPolicy> producerWatermarkPolicies)
    {
        ArgumentNullException.ThrowIfNull(producerWatermarkPolicies);
        var copy = new Dictionary<string, IWatermarkPolicy>(StringComparer.Ordinal);
        var distinct = new HashSet<IWatermarkPolicy>(ReferenceEqualityComparer.Instance);
        foreach (var (producer, policy) in producerWatermarkPolicies)
        {
            // A policy instance shared by two producers would merge their thresholds again.
            if (!distinct.Add(policy))
            {
                throw new ArgumentException(
                    $"Producer '{producer}' shares a watermark policy instance with another producer; "
                    + "each producer needs its own.", nameof(producerWatermarkPolicies));
            }

            copy.Add(producer, policy);
        }

        return copy;
    }

    private void ResetWatermarks()
    {
        if (_watermarkPolicy is not null)
        {
            _watermarkPolicy.Reset();
            return;
        }

        foreach (var policy in _producerWatermarkPolicies!.Values)
        {
            policy.Reset();
        }
    }

    // One policy holds one threshold, so a single-policy run accepts records from one producer
    // only: merging a second producer into it would close windows on the leader's progress and
    // drop the lagging producer's on-time events. A per-producer run attributes every record to
    // the declared producer whose threshold it advances.
    private IWatermarkPolicy WatermarkPolicyFor(StreamRecord<TIn> record, ref SingleProducerGuard singleProducer)
    {
        if (_watermarkPolicy is not null)
        {
            if (!singleProducer.HasProducer)
            {
                singleProducer.HasProducer = true;
                singleProducer.Producer = record.Producer;
            }
            else if (!string.Equals(singleProducer.Producer, record.Producer, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Records from producers {Describe(singleProducer.Producer)} and {Describe(record.Producer)} "
                    + "share one watermark policy, which cannot track each producer's threshold; "
                    + "create the aggregator with one policy per producer.");
            }

            return _watermarkPolicy;
        }

        if (record.Producer is null
            || !_producerWatermarkPolicies!.TryGetValue(record.Producer, out var policy))
        {
            throw new InvalidOperationException(
                $"Record at offset {record.Offset} names producer {Describe(record.Producer)}, "
                + "which is not one of the declared participating producers.");
        }

        return policy;
    }

    // The completeness threshold across all participating producers is the least advanced one.
    private DateTimeOffset CombinedWatermark()
    {
        if (_watermarkPolicy is not null)
        {
            return _watermarkPolicy.Current.Timestamp;
        }

        var combined = DateTimeOffset.MaxValue;
        foreach (var policy in _producerWatermarkPolicies!.Values)
        {
            var current = policy.Current.Timestamp;
            if (current < combined)
            {
                combined = current;
            }
        }

        return combined;
    }

    private static string Describe(string? producer) => producer is null ? "<none>" : $"'{producer}'";

    // A fixed window [start, end) can take no event at or after its end, so it closes once the
    // watermark reaches the end. A session merges an event stamped exactly at its end (see
    // WindowsOverlap), and an event stamped at the watermark is on time, so a session stays open
    // until the watermark is strictly past its end; otherwise a non-late event revises its Final.
    private static IEnumerable<KeyValuePair<(TKey Key, TimeWindow Window), AggregateState>> GetClosedWindows(
        Dictionary<(TKey Key, TimeWindow Window), AggregateState> state,
        DateTimeOffset watermark,
        bool isSession)
        => state
            .Where(pair => !pair.Value.Emitted
                && (isSession ? pair.Key.Window.EndExclusive < watermark : pair.Key.Window.EndExclusive <= watermark))
            .OrderBy(pair => pair.Key.Window.EndExclusive)
            .ThenBy(pair => pair.Key.Window.StartInclusive)
            .ToArray();

    private static bool WindowsOverlap(TimeWindow left, TimeWindow right)
        => left.StartInclusive <= right.EndExclusive && right.StartInclusive <= left.EndExclusive;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private sealed record AggregateState(TAccumulator Value, bool Emitted);

    private struct SingleProducerGuard
    {
        public bool HasProducer;
        public string? Producer;
    }
}
