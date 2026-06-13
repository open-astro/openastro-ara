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
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §68.1 — broadcasts the warn-band notification when a device connects through an AlpacaBridge in
    /// the 1.2–1.5 band (usable, but a newer bridge is recommended). The client turns the
    /// <c>equipment.alpaca_bridge_outdated_warn</c> WS event into a dismissible banner. Separated from
    /// the connect gate so the WS publish + best-effort error handling live behind one testable seam.
    /// </summary>
    public interface IAlpacaBridgeGateNotifier {
        /// <summary>Publish the §68.1 warn-band event for a bridge at <paramref name="bridgeVersion"/>.</summary>
        Task NotifyOutdatedWarnAsync(string? bridgeVersion, CancellationToken ct);
    }

    /// <inheritdoc cref="IAlpacaBridgeGateNotifier"/>
    public sealed partial class AlpacaBridgeGateNotifier : IAlpacaBridgeGateNotifier {

        private readonly IWsBroadcaster _ws;
        private readonly ILogger<AlpacaBridgeGateNotifier> _logger;

        public AlpacaBridgeGateNotifier(IWsBroadcaster ws, ILogger<AlpacaBridgeGateNotifier> logger) {
            _ws = ws ?? throw new ArgumentNullException(nameof(ws));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "WS publish is best-effort: a failed publish from a custom IWsBroadcaster (e.g. SocketException) must not fail the equipment connect — the warn banner is advisory. CA1031's log-and-recover boundary applies.")]
        public async Task NotifyOutdatedWarnAsync(string? bridgeVersion, CancellationToken ct) {
            var payload = new JsonObject {
                ["version"] = bridgeVersion,
                ["minimum"] = AlpacaBridgeGate.MinimumVersion.ToString(),
                ["recommended"] = AlpacaBridgeGate.RecommendedVersion.ToString(),
            };
            try {
                // ToJsonString()+Parse (not JsonSerializer.SerializeToElement) is the AOT-safe way to build a
                // JsonElement from a JsonObject — SerializeToElement takes the reflection path (IL2026/IL3050)
                // the warnings=errors gate rejects. Matches GuiderService.DarkLibrary.EmitCalibrationEventAsync.
                using var doc = JsonDocument.Parse(payload.ToJsonString());
                await _ws.PublishAsync(WsEventCatalog.EquipmentAlpacaBridgeOutdatedWarn, doc.RootElement.Clone(), ct).ConfigureAwait(false);
            } catch (Exception ex) when (!ct.IsCancellationRequested) {
                // Best-effort; a dropped banner must not block the connect, but log it so it's visible.
                // A genuine caller cancel (ct) is NOT swallowed — it propagates so the cancelled connect
                // aborts cleanly rather than continuing after the client gave up.
                LogWarnPublishFailed(ex);
            }
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "AlpacaBridge outdated-warn WS event failed to publish")]
        partial void LogWarnPublishFailed(Exception ex);
    }
}
