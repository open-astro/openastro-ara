#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// The per-service half of the §42.3 second channel: a <see cref="DeviceHealthTracker"/> plus the
/// standing <c>degraded</c> fault it published, so the four polling services (filter wheel, camera,
/// focuser, rotator) share one piece of code instead of four copies. Not synchronized — the owning
/// service calls it under its connection gate, like the probe.
/// </summary>
internal sealed class DeviceHealthWatch {
    private readonly DeviceType _deviceType;
    private readonly IEquipmentFaultSink? _faults;
    private readonly DeviceHealthTracker _tracker = new();
    private EquipmentFaultEvent? _degradedFault; // the standing fault to resolve when the reads come back

    public DeviceHealthWatch(DeviceType deviceType, IEquipmentFaultSink? faults) {
        _deviceType = deviceType;
        _faults = faults;
    }

    /// <summary>One call per refresh tick with the essential read's outcome. Publishes the
    /// <c>degraded</c> fault once when the streak reaches the threshold and the <c>recovered</c>
    /// action on the first good read afterwards.</summary>
    public void Observe(bool ok, string? reason, DiscoveredDeviceDto? device) {
        switch (_tracker.Observe(ok, reason)) {
            case HealthVerdict.BecameDegraded: {
                var fault = new EquipmentFaultEvent(_deviceType, device?.UniqueId, device?.Name,
                    EquipmentFaultKind.Degraded,
                    $"still answers its connection probe but its runtime read has failed {_tracker.FailureStreak} ticks in a row: {_tracker.LastReason ?? "unknown error"}",
                    DateTimeOffset.UtcNow);
                _degradedFault = fault;
                _faults?.Publish(fault);
                break;
            }
            case HealthVerdict.Recovered: {
                var fault = _degradedFault;
                _degradedFault = null;
                if (fault is not null) {
                    _faults?.Recovered(fault);
                }
                break;
            }
        }
    }

    /// <summary>A fresh session (connect / disconnect / trip) starts clean. A degraded episode that
    /// was standing ends here too — resolved as recovered, so its §42.5 row does not seed the chip
    /// amber again on the next relaunch (the reconnect hook only resolves <c>disconnected</c> rows).</summary>
    public void Reset() {
        _tracker.Reset();
        var fault = _degradedFault;
        _degradedFault = null;
        if (fault is not null) {
            _faults?.Recovered(fault);
        }
    }
}
