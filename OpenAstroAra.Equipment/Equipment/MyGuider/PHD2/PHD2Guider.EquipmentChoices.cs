#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    // §63.17 (PR 1) — drive the guider's equipment-selection reads from the connected guider: the per-slot
    // device choices and the daemon-side Alpaca network discovery. Wire shapes live in
    // PHD2Methods.EquipmentChoices.cs; the set_selected_* apply path is §63.17 PR 2.
    public sealed partial class PHD2Guider {

        // Daemon contract (doc/jsonrpc_api.md): num_queries 1..20 (default 2), timeout_seconds 1..30 (default 2).
        internal const int DiscoverMinQueries = 1;
        internal const int DiscoverMaxQueries = 20;
        internal const int DiscoverDefaultQueries = 2;
        internal const int DiscoverMinTimeoutSeconds = 1;
        internal const int DiscoverMaxTimeoutSeconds = 30;
        internal const int DiscoverDefaultTimeoutSeconds = 2;

        // discover_alpaca_servers blocks for roughly num_queries × timeout_seconds before answering, so the
        // receive timeout is derived from the effective parameters plus a fixed grace for daemon overhead.
        internal const int DiscoverReceiveGraceMs = 30000;

        // ARA-side combined bound: the REST /discover endpoint is a SYNCHRONOUS 200 (a picker-button action,
        // not a 202 background job), so the sweep must finish well inside common client HTTP timeouts
        // (HttpClient defaults to 100 s). The daemon's per-field maxima (20 × 30 s = 10 min) are far past
        // that, so ARA rejects any request whose effective sweep exceeds this bound — 60 s sweep + 30 s grace
        // keeps the whole request under ~90 s worst case. A caller needing a longer sweep runs several
        // requests back to back.
        internal const int DiscoverMaxSweepSeconds = 60;

        /// <summary>§63.17 — read the device names the daemon can offer per equipment slot (camera / mount /
        /// aux-mount / AO / rotator). A quick query usable regardless of the daemon's equipment-connected state.
        /// Requires a connected guider; throws on RPC error.</summary>
        public async Task<Phd2EquipmentChoices> GetEquipmentChoicesAsync(CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            var response = await SendMessage<Phd2GetEquipmentChoicesResponse>(new Phd2GetEquipmentChoices());
            if (response.error != null) {
                throw new GuiderRpcException("get_equipment_choices", response.error.code, response.error.message);
            }
            if (response.result is null) {
                throw new GuiderRpcException("get_equipment_choices", 0, "missing result payload");
            }
            return response.result;
        }

        /// <summary>§63.20 — read a camera's sensor pixel size (µm) from its Alpaca driver via the daemon
        /// (<c>get_alpaca_camera_pixelsize</c>). Omitted params fall back to the daemon profile's stored
        /// Alpaca camera. Requires a connected guider; throws <see cref="GuiderRpcException"/> when the
        /// daemon can't reach the camera or the driver reports no usable size.</summary>
        public async Task<double> GetAlpacaCameraPixelSizeAsync(
                string? host, int? port, int? deviceNumber, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            var response = await SendMessage<Phd2GetAlpacaCameraPixelSizeResponse>(
                new Phd2GetAlpacaCameraPixelSize {
                    Parameters = new Phd2GetAlpacaCameraPixelSizeParameter {
                        Host = host,
                        Port = port,
                        DeviceNumber = deviceNumber,
                    },
                });
            if (response.error != null) {
                throw new GuiderRpcException("get_alpaca_camera_pixelsize", response.error.code, response.error.message);
            }
            if (response.result is null || response.result.PixelSize <= 0) {
                throw new GuiderRpcException("get_alpaca_camera_pixelsize", 0, "missing or non-positive pixel_size");
            }
            return response.result.PixelSize;
        }

        /// <summary>
        /// Validates the §63.17 discovery parameters at the send site and builds the wire request — surfaced
        /// before the socket so the caller gets a clear <see cref="ArgumentOutOfRangeException"/> rather than the
        /// daemon's opaque <c>-32602</c>. Null parameters are omitted from the wire (daemon defaults apply).
        /// </summary>
        public static Phd2DiscoverAlpacaServers DiscoverAlpacaServersRequest(int? numQueries, int? timeoutSeconds) {
            if (numQueries is int q && q is < DiscoverMinQueries or > DiscoverMaxQueries) {
                throw new ArgumentOutOfRangeException(nameof(numQueries), q,
                    $"num_queries must be {DiscoverMinQueries}..{DiscoverMaxQueries}.");
            }
            if (timeoutSeconds is int t && t is < DiscoverMinTimeoutSeconds or > DiscoverMaxTimeoutSeconds) {
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), t,
                    $"timeout_seconds must be {DiscoverMinTimeoutSeconds}..{DiscoverMaxTimeoutSeconds}.");
            }
            // Combined bound: /discover is a synchronous endpoint, so the whole sweep must stay well inside
            // common client HTTP timeouts even when both fields are individually daemon-valid.
            var sweepSeconds = (numQueries ?? DiscoverDefaultQueries) * (timeoutSeconds ?? DiscoverDefaultTimeoutSeconds);
            if (sweepSeconds > DiscoverMaxSweepSeconds) {
                throw new ArgumentException(
                    $"num_queries × timeout_seconds must not exceed {DiscoverMaxSweepSeconds} s of sweep time "
                    + $"(requested {sweepSeconds} s); run multiple shorter sweeps instead.", nameof(numQueries));
            }
            return new Phd2DiscoverAlpacaServers {
                Parameters = new Phd2DiscoverAlpacaServersParameter {
                    NumQueries = numQueries,
                    TimeoutSeconds = timeoutSeconds,
                },
            };
        }

        /// <summary>The receive timeout matched to a discovery request: the daemon blocks for roughly
        /// <c>num_queries × timeout_seconds</c> (defaults applied for omitted fields) plus a fixed grace —
        /// so a max-bound sweep (60 s) isn't cut off by the default 60 s receive bound and a short one
        /// fails fast.</summary>
        public static int DiscoverReceiveTimeoutMs(int? numQueries, int? timeoutSeconds) =>
            ((numQueries ?? DiscoverDefaultQueries) * (timeoutSeconds ?? DiscoverDefaultTimeoutSeconds) * 1000)
                + DiscoverReceiveGraceMs;

        /// <summary>
        /// §63.17 — daemon-side Alpaca network discovery (useful when ARA's own discovery and the guider's
        /// disagree about what's on the network). Blocking for roughly <c>num_queries × timeout_seconds</c>
        /// (capped at <see cref="DiscoverMaxSweepSeconds"/> combined, so a dispatched sweep always finishes
        /// promptly); returns the discovered <c>"host:port"</c> strings. Requires a connected guider; throws
        /// <see cref="ArgumentException"/> on out-of-range or over-long parameters and
        /// <see cref="GuiderRpcException"/> on RPC error (including a daemon build without Alpaca support).
        /// </summary>
        /// <remarks>Same in-flight contract as the calibration builds: <paramref name="ct"/> is honored only at
        /// entry — SendMessage takes no cancellation token, so a dispatched sweep runs to completion or its
        /// receive timeout.</remarks>
        public async Task<System.Collections.Generic.IReadOnlyList<string>> DiscoverAlpacaServersAsync(
                int? numQueries, int? timeoutSeconds, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            // Validate before the socket so a bad request never reaches the daemon.
            var request = DiscoverAlpacaServersRequest(numQueries, timeoutSeconds);
            Logger.Info(
                $"Phd2 - Discovering Alpaca servers (queries={numQueries?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default"}, "
                + $"timeoutSeconds={timeoutSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default"}).");
            var response = await SendMessage<Phd2DiscoverAlpacaServersResponse>(
                request, DiscoverReceiveTimeoutMs(numQueries, timeoutSeconds));
            if (response.error != null) {
                throw new GuiderRpcException("discover_alpaca_servers", response.error.code, response.error.message);
            }
            if (response.result is null) {
                throw new GuiderRpcException("discover_alpaca_servers", 0, "missing result payload");
            }
            return response.result;
        }
    }
}
