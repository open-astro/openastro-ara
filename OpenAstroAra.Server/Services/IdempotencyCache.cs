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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// A small keyed replay cache for create-style POSTs carrying an
/// <c>Idempotency-Key</c> header: the first application records its result;
/// a replay within the window returns that result instead of re-applying.
/// Closes the duplicate-run window the client-side audit found (a create
/// whose response is lost on marginal rig Wi-Fi gets retried — with the same
/// key, the retry now dedupes instead of creating a second sequence).
///
/// <para>In-process only, by design: the retry window that matters is
/// seconds-to-minutes of link flappiness; a daemon restart mid-retry is rare
/// enough that persisting the map isn't worth the write cost. Entries expire
/// after <see cref="Window"/> and are pruned opportunistically on access.</para>
/// </summary>
public sealed class IdempotencyCache<TResult> where TResult : class {

    /// <summary>How long a key replays its first result. 24 h matches the
    /// documented contract on the create endpoint.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, (TResult Result, DateTimeOffset At)> _entries = new();

    /// <summary>The recorded result for <paramref name="key"/>, or null when
    /// unseen/expired. A null/whitespace key never matches (no header = no dedup).</summary>
    public TResult? TryGet(string? key) {
        Prune();
        if (string.IsNullOrWhiteSpace(key)) {
            return null;
        }
        return _entries.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < Window
            ? hit.Result
            : null;
    }

    /// <summary>Record <paramref name="result"/> as the replay for
    /// <paramref name="key"/>. No-op without a key.</summary>
    public void Record(string? key, TResult result) {
        if (string.IsNullOrWhiteSpace(key)) {
            return;
        }
        _entries[key] = (result, DateTimeOffset.UtcNow);
    }

    private void Prune() {
        // Cheap opportunistic sweep — the map holds at most a few dozen keys
        // (one per user create in the window), so a full pass is fine.
        var cutoff = DateTimeOffset.UtcNow - Window;
        foreach (var kv in _entries) {
            if (kv.Value.At < cutoff) {
                _entries.TryRemove(kv.Key, out _);
            }
        }
    }
}
