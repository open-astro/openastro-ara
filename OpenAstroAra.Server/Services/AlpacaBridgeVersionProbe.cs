#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>The result of a single §68.1 AlpacaBridge handshake.</summary>
    /// <param name="Status">The §68.1 gate classification.</param>
    /// <param name="Version">
    /// The raw version string the bridge reported (e.g. <c>"1.3.0"</c>), or <c>null</c> when the
    /// probe couldn't read one (<see cref="AlpacaBridgeStatus.Missing"/>). Carried verbatim so the
    /// §68.1 warn banner / §51 diagnostic can show the actual version to the user.
    /// </param>
    public readonly record struct AlpacaBridgeHandshake(AlpacaBridgeStatus Status, string? Version);

    /// <summary>
    /// §68.1 handshake probe: fetch <c>GET {bridge}/version</c> and classify the bridge. Isolated
    /// behind an interface so the §68-b handshake/cache service and the equipment-gate can be tested
    /// against a fake bridge without real HTTP.
    /// </summary>
    public interface IAlpacaBridgeVersionProbe {
        /// <summary>
        /// Probe the AlpacaBridge at <paramref name="bridgeBaseUri"/> (scheme/host/port of the Alpaca
        /// server) and classify it per §68.1. An unreachable bridge, a non-success status, non-JSON
        /// body, or a missing <c>alpaca_bridge_version</c> field all resolve to
        /// <see cref="AlpacaBridgeStatus.Missing"/> rather than throwing — only
        /// <paramref name="ct"/> cancellation propagates.
        /// </summary>
        Task<AlpacaBridgeHandshake> ProbeAsync(Uri bridgeBaseUri, CancellationToken ct);
    }

    /// <inheritdoc cref="IAlpacaBridgeVersionProbe"/>
    public sealed partial class AlpacaBridgeVersionProbe : IAlpacaBridgeVersionProbe {

        /// <summary>The <see cref="IHttpClientFactory"/> client name §68-b registers for the probe.</summary>
        public const string HttpClientName = "alpaca-bridge";

        // §68.1: a healthy Pi-local bridge answers in < 200 ms. Bound the probe well above that so a
        // wedged/half-open socket resolves to Missing in seconds rather than hanging the handshake.
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<AlpacaBridgeVersionProbe> _logger;

        public AlpacaBridgeVersionProbe(IHttpClientFactory httpClientFactory, ILogger<AlpacaBridgeVersionProbe> logger) {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<AlpacaBridgeHandshake> ProbeAsync(Uri bridgeBaseUri, CancellationToken ct) {
            ArgumentNullException.ThrowIfNull(bridgeBaseUri);
            // Force scheme/host/port + "/version" regardless of any path on the discovered base URI.
            var versionUri = new UriBuilder(bridgeBaseUri) { Path = "/version", Query = string.Empty }.Uri;

            // Self-bound the probe so a hung connection can't stall the handshake past ProbeTimeout,
            // while still honoring a caller cancel (which propagates as OperationCanceledException).
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProbeTimeout);

            try {
                using var client = _httpClientFactory.CreateClient(HttpClientName);
                using var response = await client.GetAsync(versionUri, timeoutCts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) {
                    return Missing(versionUri, $"HTTP {(int)response.StatusCode}");
                }
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                var rawVersion = ExtractVersion(body);
                var status = AlpacaBridgeGate.Classify(rawVersion);
                LogHandshake(versionUri, rawVersion ?? "<none>", status);
                return new AlpacaBridgeHandshake(status, rawVersion);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw; // a genuine caller cancel — let it propagate.
            } catch (OperationCanceledException) {
                return Missing(versionUri, "timed out"); // our ProbeTimeout fired.
            } catch (HttpRequestException ex) {
                return Missing(versionUri, ex.Message); // unreachable / connection refused.
            } catch (UriFormatException ex) {
                return Missing(versionUri, ex.Message);
            }
        }

        private AlpacaBridgeHandshake Missing(Uri versionUri, string reason) {
            LogMissing(versionUri, reason);
            return new AlpacaBridgeHandshake(AlpacaBridgeStatus.Missing, null);
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "AlpacaBridge handshake at {Uri}: version={Version} status={Status}")]
        partial void LogHandshake(Uri uri, string version, AlpacaBridgeStatus status);

        [LoggerMessage(Level = LogLevel.Information, Message = "AlpacaBridge /version probe at {Uri} treated as missing: {Reason}")]
        partial void LogMissing(Uri uri, string reason);

        // Pull alpaca_bridge_version out of the bridge's JSON. Any shape mismatch or non-JSON body
        // yields null → Missing (§68.1 "non-JSON / missing field" row). JsonDocument is AOT-safe and
        // needs no source-gen registration for this read-only parse.
        private static string? ExtractVersion(string body) {
            try {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("alpaca_bridge_version", out var versionEl) &&
                    versionEl.ValueKind == JsonValueKind.String) {
                    return versionEl.GetString();
                }
            } catch (JsonException) {
                // non-JSON body — fall through to null (Missing).
            }
            return null;
        }
    }
}
