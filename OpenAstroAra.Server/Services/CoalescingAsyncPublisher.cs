#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Single-flight, trailing-coalesce pump for a high-rate fire-and-forget publish
/// (the §60.9 "progress-emit back-pressure" guard): <see cref="Poke"/> as often as
/// you like — at most ONE publish runs at a time, a burst of pokes while one is in
/// flight collapses into ONE trailing publish, and every poke is guaranteed to be
/// followed by a publish that started at-or-after it (the consumer reads current
/// state at publish time, so the trailing publish carries the freshest snapshot —
/// intermediate snapshots are deliberately droppable).
///
/// <para>Unlike a time debounce, the first poke publishes immediately (no added
/// latency at low rates) and back-pressure only engages while the transport is
/// actually the bottleneck. The publish delegate must not throw — wrap transports
/// that can (the sequencer's <c>EmitAsync</c> already swallows all failures); a
/// throw escaping the delegate would kill the pump, so it is caught and dropped
/// here as a second line of defence.</para>
/// </summary>
public sealed class CoalescingAsyncPublisher {
    private readonly Func<Task> _publish;
    private int _publishing;          // 1 = a pump owns the publish loop
    private int _pending;             // 1 = a poke arrived that no publish has consumed yet
    private int _sealed;              // 1 = no further publishes accepted (terminal ordering)
    private volatile Task? _pumpTask; // the live pump, for DrainAsync to await

    public CoalescingAsyncPublisher(Func<Task> publish) {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
    }

    /// <summary>Request a publish. Safe from any thread. NOTE: the pump executes
    /// inline on the calling thread up to the delegate's first await — keep the
    /// delegate's synchronous prefix cheap (the sequencer's EmitAsync only builds
    /// a small JSON payload before its first await).</summary>
    public void Poke() {
        if (Volatile.Read(ref _sealed) == 1) {
            // Sealed: a late tick (Progress<T> queues callbacks to the thread pool,
            // so ticks can land after the run has ended — the #648 lesson) must not
            // race a publish past the terminal event.
            return;
        }
        // Pending FIRST, then try to acquire: a concurrent pump that just consumed
        // pending will either see this one in its loop check, or the release-recheck
        // below restarts it — a poke can never be lost between the two flags.
        Volatile.Write(ref _pending, 1);
        if (Interlocked.CompareExchange(ref _publishing, 1, 0) == 0) {
            _pumpTask = PumpAsync();
        }
    }

    /// <summary>Stop accepting pokes, then wait until any in-flight/trailing publish
    /// has fully completed. Call before emitting an ordered terminal event so a
    /// coalesced progress publish can never arrive after it. Late pokes (queued
    /// Progress&lt;T&gt; callbacks landing post-run) become no-ops.</summary>
    public async Task SealAndDrainAsync() {
        Volatile.Write(ref _sealed, 1);
        // The pump may tail-restart once (release-recheck), so loop over snapshots
        // of the live pump task until the machine is quiescent.
        while (Volatile.Read(ref _publishing) == 1 || Volatile.Read(ref _pending) == 1) {
            var t = _pumpTask;
            if (t is not null) {
                await t.ConfigureAwait(false);
            } else {
                await Task.Yield();
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fire-and-forget pump boundary: the publish delegate is documented as non-throwing (the sequencer's EmitAsync swallows its own failures), so anything escaping here is already a contract breach — and letting it fault this thread-pool task would kill the pump and starve every later poke of its publish. CA1031's log-and-recover boundary applies; recovery IS the next loop iteration.")]
    private async Task PumpAsync() {
        try {
            while (Interlocked.Exchange(ref _pending, 0) == 1) {
                try {
                    await _publish().ConfigureAwait(false);
                } catch {
                    // Second line of defence (see class doc): the pump must survive a
                    // throwing delegate — the NEXT poke still gets its publish.
                }
            }
        } finally {
            Volatile.Write(ref _publishing, 0);
            // Close the release window: a poke may have set pending after our last
            // pending-exchange but before the release above — it saw publishing == 1
            // and didn't start a pump, so restart one here if we can re-acquire.
            if (Volatile.Read(ref _pending) == 1
                    && Interlocked.CompareExchange(ref _publishing, 1, 0) == 0) {
                _pumpTask = PumpAsync();
            }
        }
    }
}
