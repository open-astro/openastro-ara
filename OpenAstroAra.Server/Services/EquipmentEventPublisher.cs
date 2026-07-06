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
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §60.9 — the one emitter for the <c>equipment.*</c> connection events, which
/// were catalogued from day one but never published: WILMA's equipment chips
/// had to poll to notice a device appearing, vanishing, or failing. Every
/// device service calls <see cref="StateChanged"/> from its <c>SetState</c>
/// choke point; this publishes <c>equipment.state_changed</c> for every
/// transition plus the notable-moment alias (<c>equipment.connected</c> /
/// <c>equipment.disconnected</c> / <c>equipment.connection_failed</c>) the
/// client can subscribe to without decoding the state machine.
///
/// <para>Payload shape (§60.6 lowercase enum tokens, matching the REST wire):
/// <c>{ "device_type": "telescope", "device_id": ..., "device_name": ...,
/// "state": "connected" }</c>. Built with JsonObject — the same pattern as
/// SequencerService.EmitAsync — so values are always escaped and the enum
/// tokens don't depend on the HTTP pipeline's converter registrations.</para>
///
/// <para>Fire-and-forget by contract: events are UX freshness, and a slow or
/// backpressured WS channel must never block or fault a connect path — the
/// publish's failures are logged and dropped. Callers may invoke this while
/// holding their service lock (the synchronous part only builds a small
/// payload and hands off to the channel).</para>
/// </summary>
public sealed partial class EquipmentEventPublisher {

    private readonly IWsBroadcaster _broadcaster;
    private readonly ILogger<EquipmentEventPublisher> _logger;

    public EquipmentEventPublisher(IWsBroadcaster broadcaster, ILogger<EquipmentEventPublisher>? logger = null) {
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _logger = logger ?? NullLogger<EquipmentEventPublisher>.Instance;
    }

    /// <summary>Publish a device's connection-state transition. Callers gate on an
    /// ACTUAL change (previous != next) so poll-refresh re-assertions don't spam the
    /// stream. Id/name may be null when the transition races a teardown that already
    /// cleared the device.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Event publication is best-effort UX freshness: serialization or channel faults must be logged and dropped, never propagated into a device connect/disconnect path. CA1031's log-and-recover boundary applies.")]
    public void StateChanged(
            DeviceType deviceType,
            string? deviceId,
            string? deviceName,
            EquipmentConnectionState state) {
        try {
            // ToJsonString()+Parse is the AOT-safe way to a JsonElement from a
            // JsonObject (same as the other WS emitters).
            var payload = new JsonObject {
                ["device_type"] = deviceType.ToString().ToLowerInvariant(),
                ["device_id"] = deviceId,
                ["device_name"] = deviceName,
                ["state"] = state.ToString().ToLowerInvariant(),
            };
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            var element = doc.RootElement.Clone();

            Publish(WsEventCatalog.EquipmentStateChanged, element);
            var alias = state switch {
                EquipmentConnectionState.Connected => WsEventCatalog.EquipmentConnected,
                EquipmentConnectionState.Disconnected => WsEventCatalog.EquipmentDisconnected,
                EquipmentConnectionState.Error => WsEventCatalog.EquipmentConnectionFailed,
                _ => null, // Connecting is visible via state_changed only.
            };
            if (alias is not null) {
                Publish(alias, element);
            }
        } catch (Exception ex) {
            LogPublishFailed(ex, WsEventCatalog.EquipmentStateChanged);
        }
    }

    private void Publish(string eventType, JsonElement payload) {
        var task = _broadcaster.PublishAsync(eventType, payload, CancellationToken.None);
        if (!task.IsCompletedSuccessfully) {
            // Observe the asynchronous tail so a channel fault can never become
            // an unobserved task exception; scheduled only on the rare slow path.
            _ = task.ContinueWith(
                t => LogPublishFailed(t.Exception!.GetBaseException(), eventType),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "equipment event {EventType} could not be published")]
    private partial void LogPublishFailed(Exception ex, string eventType);
}
