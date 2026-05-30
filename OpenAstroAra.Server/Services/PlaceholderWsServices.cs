#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using OpenAstroAra.Server.Contracts.WsEvents;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Phase 13.17 — placeholder <see cref="IWsBroadcaster"/> +
/// <see cref="IWsEventChannel"/>. The broadcaster is the publish-side
/// API future placeholders can call when they have a state change to
/// announce; the channel is the consume-side that the §60.9 WS upgrade
/// handler will drain when it lands.
///
/// For v0.0.1 the actual <c>/api/v1/ws</c> handler stays 501 (real WS
/// upgrade lifecycle is a separate sub-PR). These service impls let the
/// rest of the daemon wire up against the interfaces today — every
/// placeholder service that "would emit" a WS event can already call
/// <c>broadcaster.PublishAsync(...)</c> with no-ops at the channel
/// boundary.
///
/// Replay buffer: keeps the last 1000 envelopes so the §60.9.6 resume
/// protocol can replay events for a client that disconnected briefly.
/// Capacity matches the §60.9.7 buffer-size target.
/// </summary>
public sealed class InMemoryWsServices : IWsBroadcaster, IWsEventChannel {
    private const int ReplayBufferCapacity = 1000;
    private const int PerSubscriberCapacity = 1000;

    private long _seq;
    // Per-subscriber channels. Each ReadAllAsync call registers its own
    // channel; PublishAsync fans out to all of them. Without this, a
    // single shared Channel<T> would split events between multiple
    // connected clients (each event delivered to exactly one reader)
    // instead of broadcasting — wrong shape for §60.9 events.
    private readonly ConcurrentDictionary<Guid, Channel<WsEventEnvelopeDto>> _subscribers = new();
    // ConcurrentQueue + manual trim — keeping the last N envelopes for
    // resume; ConcurrentQueue's TryDequeue is the safe trim primitive.
    private readonly ConcurrentQueue<WsEventEnvelopeDto> _replay = new();
    private readonly object _replayTrimLock = new();

    public long CurrentSequence => Interlocked.Read(ref _seq);

    public Task PublishAsync(string eventType, System.Text.Json.JsonElement payload, CancellationToken ct) {
        var envelope = new WsEventEnvelopeDto(
            Type: eventType,
            Ts: DateTimeOffset.UtcNow,
            Seq: Interlocked.Increment(ref _seq),
            Payload: payload);
        return EnqueueInternalAsync(envelope, ct);
    }

    // Explicit interface impl — keeps the interface contract for the future WS
    // upgrade handler but prevents callers from resolving InMemoryWsServices
    // directly and enqueueing pre-built envelopes that would bypass the
    // sequencing in PublishAsync.
    async Task IWsEventChannel.EnqueueAsync(WsEventEnvelopeDto envelope, CancellationToken ct)
        => await EnqueueInternalAsync(envelope, ct);

    private Task EnqueueInternalAsync(WsEventEnvelopeDto envelope, CancellationToken ct) {
        _replay.Enqueue(envelope);
        TrimReplay();
        // Fan-out: every subscriber gets the envelope. TryWrite is
        // non-blocking — if a per-sub channel is full, the DropOldest
        // policy on that channel evicts the stalest envelope so a
        // slow client can't backpressure the publisher.
        foreach (var sub in _subscribers.Values) {
            sub.Writer.TryWrite(envelope);
        }
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<WsEventEnvelopeDto> ReadAllAsync(CancellationToken ct) {
        // Eager registration — the subscriber dict entry has to exist before
        // the caller runs any resume-phase logic, otherwise events published
        // between snapshot and iteration are silently dropped (the race
        // Sonnet caught on PR #174). Splitting the registration out of the
        // iterator method moves it to the synchronous call site so it
        // happens immediately, not on the first MoveNextAsync.
        var subscriberId = Guid.NewGuid();
        var sub = Channel.CreateBounded<WsEventEnvelopeDto>(
            new BoundedChannelOptions(PerSubscriberCapacity) {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
        _subscribers[subscriberId] = sub;
        return ReadFromSubscriptionAsync(subscriberId, sub, ct);
    }

    private async IAsyncEnumerable<WsEventEnvelopeDto> ReadFromSubscriptionAsync(
            Guid subscriberId,
            Channel<WsEventEnvelopeDto> sub,
            [EnumeratorCancellation] CancellationToken ct) {
        try {
            await foreach (var envelope in sub.Reader.ReadAllAsync(ct)) {
                yield return envelope;
            }
        } finally {
            _subscribers.TryRemove(subscriberId, out _);
            sub.Writer.TryComplete();
        }
    }

    public Task<IReadOnlyList<WsEventEnvelopeDto>> ResumeFromAsync(long lastSeenSeq, CancellationToken ct) {
        // Snapshot the replay buffer and filter by sequence number.
        // §60.9.6: clients pass their last_seen_seq; we return everything
        // strictly after. If the buffer doesn't go back that far (client
        // was disconnected longer than the buffer's lifetime), the WS
        // handler signals close code 4002 (resume-too-old).
        var snapshot = _replay.ToArray();
        var resume = snapshot.Where(e => e.Seq > lastSeenSeq).ToList();
        return Task.FromResult<IReadOnlyList<WsEventEnvelopeDto>>(resume);
    }

    private void TrimReplay() {
        if (_replay.Count <= ReplayBufferCapacity) return;
        lock (_replayTrimLock) {
            while (_replay.Count > ReplayBufferCapacity && _replay.TryDequeue(out _)) { }
        }
    }
}
