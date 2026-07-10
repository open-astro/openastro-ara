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

/// <summary>The in-proc sink device services publish detected §42.2 faults into. Detection-side
/// fire-and-forget: publishing must never block, throw into, or slow a device path.</summary>
public interface IEquipmentFaultSink {
    void Publish(EquipmentFaultEvent fault);
}

/// <summary>
/// §42.2 — the one place detected equipment faults converge. On every publish it logs the fault
/// and broadcasts the <c>equipment.fault</c> WS event (the EquipmentEventPublisher pattern:
/// JsonObject → AOT-safe element, best-effort, never propagates); the §42.3 reaction service
/// subscribes via <see cref="FaultPublished"/> in its own slice — until then this hub makes every
/// detection observable on its own (log + WILMA event), so the detection slice ships standalone.
/// </summary>
public sealed partial class EquipmentFaultHub : IEquipmentFaultSink {

    private readonly IWsBroadcaster _broadcaster;
    private readonly ILogger<EquipmentFaultHub> _logger;

    public EquipmentFaultHub(IWsBroadcaster broadcaster, ILogger<EquipmentFaultHub>? logger = null) {
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _logger = logger ?? NullLogger<EquipmentFaultHub>.Instance;
    }

    // Invoked synchronously on each publish, AFTER the log line (so the reaction slice can never
    // suppress the record of a fault). Subscribers must hand off promptly — the publisher may be
    // a device service's refresh tick. Copy-on-write array: subscriptions happen at startup
    // (the reaction service), publishes for a daemon lifetime.
    private Action<EquipmentFaultEvent>[] _subscribers = [];
    private readonly object _subscribeGate = new();

    /// <summary>Register a fault consumer (the §42.3 reaction service). No unsubscribe — the
    /// hub and its consumers are daemon-lifetime singletons.</summary>
    public void Subscribe(Action<EquipmentFaultEvent> consumer) {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_subscribeGate) {
            var next = new Action<EquipmentFaultEvent>[_subscribers.Length + 1];
            _subscribers.CopyTo(next, 0);
            next[^1] = consumer;
            _subscribers = next;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fault publication is a detection-side, best-effort path invoked from device refresh ticks and op sites; serialization/channel/subscriber faults must be logged and dropped, never propagated back into a device path. CA1031's log-and-recover boundary applies.")]
    public void Publish(EquipmentFaultEvent fault) {
        ArgumentNullException.ThrowIfNull(fault);
        LogFault(fault.DeviceType, fault.Kind, fault.DeviceName ?? fault.DeviceId ?? "?", fault.Details ?? "");
        try {
            var payload = new JsonObject {
                ["device_type"] = fault.DeviceType.ToString().ToLowerInvariant(),
                ["device_id"] = fault.DeviceId,
                ["device_name"] = fault.DeviceName,
                ["kind"] = WireToken(fault.Kind),
                ["details"] = fault.Details,
                ["detected_utc"] = fault.DetectedUtc.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            };
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            var element = doc.RootElement.Clone();
            _ = _broadcaster.PublishAsync(WsEventCatalog.EquipmentFault, element, System.Threading.CancellationToken.None);
        } catch (Exception ex) {
            LogPublishFailed(ex);
        }
        foreach (var subscriber in _subscribers) {
            try {
                subscriber(fault);
            } catch (Exception ex) {
                LogSubscriberFailed(ex);
            }
        }
    }

    /// <summary>The §42.2 snake_case wire token for a fault kind.</summary>
    public static string WireToken(EquipmentFaultKind kind) => kind switch {
        EquipmentFaultKind.Disconnected => "disconnected",
        EquipmentFaultKind.TrackingLost => "tracking_lost",
        EquipmentFaultKind.StallTimeout => "stall_timeout",
        EquipmentFaultKind.ValueMismatch => "value_mismatch",
        EquipmentFaultKind.CoolingDrift => "cooling_drift",
        _ => "op_error",
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Equipment fault: {DeviceType} {Kind} on '{Device}' — {Details}")]
    private partial void LogFault(DeviceType deviceType, EquipmentFaultKind kind, string device, string details);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Equipment fault hub: failed to broadcast the equipment.fault WS event")]
    private partial void LogPublishFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Equipment fault hub: a fault subscriber threw — the fault is still logged/broadcast")]
    private partial void LogSubscriberFailed(Exception ex);
}
