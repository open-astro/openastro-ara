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

    private long _seq;
    private readonly Channel<WsEventEnvelopeDto> _channel;
    // ConcurrentQueue + manual trim — keeping the last N envelopes for
    // resume; ConcurrentQueue's TryDequeue is the safe trim primitive.
    private readonly ConcurrentQueue<WsEventEnvelopeDto> _replay = new();
    private readonly object _replayTrimLock = new();

    public InMemoryWsServices() {
        // Bounded channel with DropOldest behavior — under sustained
        // backpressure we keep newer events and drop older ones. Future
        // WS handler can emit backup.stream.backpressure events when
        // dropping happens.
        _channel = Channel.CreateBounded<WsEventEnvelopeDto>(
            new BoundedChannelOptions(ReplayBufferCapacity) {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
    }

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

    private async Task EnqueueInternalAsync(WsEventEnvelopeDto envelope, CancellationToken ct) {
        _replay.Enqueue(envelope);
        TrimReplay();
        await _channel.Writer.WriteAsync(envelope, ct);
    }

    public async IAsyncEnumerable<WsEventEnvelopeDto> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken ct) {
        await foreach (var envelope in _channel.Reader.ReadAllAsync(ct)) {
            yield return envelope;
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
