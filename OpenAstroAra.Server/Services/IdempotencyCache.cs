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
public sealed class IdempotencyKeyConflictException : InvalidOperationException {
    public IdempotencyKeyConflictException()
        : base("The Idempotency-Key was already used with a different request.") { }

    public IdempotencyKeyConflictException(string message) : base(message) { }

    public IdempotencyKeyConflictException(string message, Exception innerException)
        : base(message, innerException) { }
}

public sealed class IdempotencyCache<TResult> where TResult : class {

    /// <summary>How long a completed key replays its result. 24 h matches the
    /// documented contract on the create endpoint.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private const int MaxKeyLength = 256;

    private sealed record Entry(
        Lazy<Task<TResult>> Flight,
        DateTimeOffset At,
        string? RequestFingerprint);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>Run <paramref name="factory"/> once per <paramref name="key"/>:
    /// concurrent callers with the same key await the SAME in-flight task; later
    /// callers within the window get the recorded result. A null/whitespace key
    /// means "no dedup requested" — the factory just runs. A faulted flight is
    /// evicted so the next attempt retries rather than replaying the failure.</summary>
    public Task<TResult> GetOrRunAsync(string? key, Func<Task<TResult>> factory) =>
        GetOrRunCoreAsync(key, requestFingerprint: null, factory);

    /// <summary>
    /// Fingerprinted overload for mutations. A key can replay only the same
    /// logical request; reusing it for a different request fails explicitly.
    /// </summary>
    public Task<TResult> GetOrRunAsync(string? key, string requestFingerprint,
            Func<Task<TResult>> factory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestFingerprint);
        return GetOrRunCoreAsync(key, requestFingerprint, factory);
    }

    private async Task<TResult> GetOrRunCoreAsync(string? key, string? requestFingerprint,
            Func<Task<TResult>> factory) {
        ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(key)) {
            return await factory().ConfigureAwait(false);
        }
        var normalizedKey = key.Trim();
        if (normalizedKey.Length > MaxKeyLength) {
            throw new ArgumentException(
                $"Idempotency-Key must not exceed {MaxKeyLength} characters.", nameof(key));
        }
        Prune();
        // Lazy(ExecutionAndPublication) makes the GetOrAdd race benign: several
        // threads may build an Entry, but only the WINNER's factory ever runs.
        var candidate = new Entry(new Lazy<Task<TResult>>(factory),
            DateTimeOffset.UtcNow, requestFingerprint);
        var entry = _entries.GetOrAdd(normalizedKey, candidate);
        if (!string.Equals(entry.RequestFingerprint, requestFingerprint,
                StringComparison.Ordinal)) {
            throw new IdempotencyKeyConflictException();
        }
        try {
            return await entry.Flight.Value.ConfigureAwait(false);
        } catch {
            // Don't cache failures: the retry that follows a genuine error must
            // re-attempt, not replay the exception for 24 h.
            _entries.TryRemove(new KeyValuePair<string, Entry>(normalizedKey, entry));
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
                _entries.TryRemove(kv);
            }
        }
    }
}