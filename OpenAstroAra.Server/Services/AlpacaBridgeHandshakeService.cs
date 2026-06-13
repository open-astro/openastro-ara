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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §68.1 AlpacaBridge handshake with a short-lived cache. The §68.1 contract runs the
    /// <c>/version</c> handshake every time the equipment layer connects to the bridge but caches
    /// the result for the session; this service caches each bridge's classification for ~30s (the
    /// §51 <c>alpaca_bridge</c> health window) so a burst of equipment connects to the same bridge
    /// doesn't re-probe on every call. The equipment-connect gate (§68-b) reads this.
    /// </summary>
    public interface IAlpacaBridgeHandshakeService {
        /// <summary>
        /// The §68.1 handshake for the AlpacaBridge at <paramref name="bridgeBaseUri"/>
        /// (scheme/host/port of the Alpaca server). A classification probed within the cache TTL is
        /// returned without re-probing; otherwise the bridge's <c>/version</c> is probed and cached.
        /// Only <paramref name="ct"/> cancellation propagates — an unreachable bridge resolves to
        /// <see cref="AlpacaBridgeStatus.Missing"/> via the probe.
        /// </summary>
        Task<AlpacaBridgeHandshake> HandshakeAsync(Uri bridgeBaseUri, CancellationToken ct);
    }

    /// <inheritdoc cref="IAlpacaBridgeHandshakeService"/>
    public sealed class AlpacaBridgeHandshakeService : IAlpacaBridgeHandshakeService {

        /// <summary>How long a probed classification is reused before the bridge is re-probed (§68.1 / §51 30s window).</summary>
        internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        private readonly IAlpacaBridgeVersionProbe _probe;
        private readonly TimeProvider _timeProvider;
        private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);

        private readonly record struct Entry(AlpacaBridgeHandshake Result, DateTimeOffset ProbedAt);

        public AlpacaBridgeHandshakeService(IAlpacaBridgeVersionProbe probe)
            : this(probe, TimeProvider.System) {
        }

        // Test seam: a fake TimeProvider drives the TTL deterministically.
        internal AlpacaBridgeHandshakeService(IAlpacaBridgeVersionProbe probe, TimeProvider timeProvider) {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        }

        public async Task<AlpacaBridgeHandshake> HandshakeAsync(Uri bridgeBaseUri, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(bridgeBaseUri);
            var key = NormalizeKey(bridgeBaseUri);
            var now = _timeProvider.GetUtcNow();

            if (_cache.TryGetValue(key, out var entry) && now - entry.ProbedAt < CacheTtl) {
                return entry.Result; // fresh cache hit — no re-probe.
            }

            // Miss or expired: probe. Two concurrent misses for the same bridge may both probe;
            // that's harmless (the /version GET is idempotent + cheap) and avoids per-key locking.
            var result = await _probe.ProbeAsync(bridgeBaseUri, ct).ConfigureAwait(false);
            _cache[key] = new Entry(result, now);
            return result;
        }

        // Key on scheme://host:port only — Uri already lowercases the scheme + registered-name host,
        // so two device URIs on the same bridge (differing only by path) share one cache entry.
        private static string NormalizeKey(Uri uri) =>
            FormattableString.Invariant($"{uri.Scheme}://{uri.Host}:{uri.Port}");
    }
}
