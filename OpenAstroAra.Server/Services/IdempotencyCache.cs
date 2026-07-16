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
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// A keyed <b>single-flight</b> replay cache for create-style POSTs carrying an
/// <c>Idempotency-Key</c> header: concurrent requests with the same key share ONE
/// execution of the factory (a retry racing the still-processing original joins it
/// instead of double-creating — the TOCTOU the #853 review flagged), and later
/// replays within the window return the recorded result.
///
/// <para>In-process, windowed: suitable for surfaces whose retry story is
/// seconds-to-minutes of link flap (e.g. /sequences/import). Callers whose replay
/// window must survive daemon restarts or span days (the persisted-offline-draft
/// push) should ALSO persist their key→result mapping durably — see
/// <c>FileSequenceService</c>'s on-disk replay map.</para>
/// </summary>
public sealed class IdempotencyCache<TResult> where TResult : class {

    /// <summary>How long a completed key replays its result. 24 h matches the
    /// documented contract on the create endpoint.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private sealed record Entry(Lazy<Task<TResult>> Flight, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>Run <paramref name="factory"/> once per <paramref name="key"/>:
    /// concurrent callers with the same key await the SAME in-flight task; later
    /// callers within the window get the recorded result. A null/whitespace key
    /// means "no dedup requested" — the factory just runs. A faulted flight is
    /// evicted so the next attempt retries rather than replaying the failure.</summary>
    public async Task<TResult> GetOrRunAsync(string? key, Func<Task<TResult>> factory) {
        if (string.IsNullOrWhiteSpace(key)) {
            return await factory().ConfigureAwait(false);
        }
        Prune();
        // Lazy(ExecutionAndPublication) makes the GetOrAdd race benign: several
        // threads may build an Entry, but only the WINNER's factory ever runs.
        var entry = _entries.GetOrAdd(key,
            _ => new Entry(new Lazy<Task<TResult>>(factory), DateTimeOffset.UtcNow));
        try {
            return await entry.Flight.Value.ConfigureAwait(false);
        } catch {
            // Don't cache failures: the retry that follows a genuine error must
            // re-attempt, not replay the exception for 24 h.
            _entries.TryRemove(key, out _);
            throw;
        }
    }

    /// <summary>Forget a key (e.g. its result was invalidated downstream).</summary>
    public void Evict(string key) => _entries.TryRemove(key, out _);

    private void Prune() {
        // Cheap opportunistic sweep — the map holds at most a few dozen keys
        // (one per user create in the window), so a full pass is fine. Not for
        // reuse on high-traffic endpoints without revisiting this.
        var cutoff = DateTimeOffset.UtcNow - Window;
        foreach (var kv in _entries) {
            if (kv.Value.At < cutoff) {
                _entries.TryRemove(kv.Key, out _);
            }
        }
    }
}
