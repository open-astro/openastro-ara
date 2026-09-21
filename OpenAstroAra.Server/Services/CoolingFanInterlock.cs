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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §25.5.6 / #1065 — the camera-cooling fan interlock, daemon-side. The bridge exposes the
/// ToupTek "Thermal Switch" whose "Fan" port vents the TEC's heat sink; two rules keep the
/// camera safe and they now live HERE so every path to the cooler and the fan that goes through
/// these services (REST from any client, the §58 unattended shutdown's warm ramp) gets them, not
/// just the Flutter UI's own buttons. Not yet covered (#1076): the sequencer's SetSwitchValue
/// instruction writes the switch raw, and its CoolCamera/WarmCamera mediator stubs never reach
/// SetCoolerAsync.
/// <list type="number">
/// <item><b>The fan follows the cooler.</b> <see cref="CameraService.SetCoolerAsync"/> writes the
/// fan port to its max after cooler-on and to its min after cooler-off.</item>
/// <item><b>No fan-off while the TEC is (or may be) cooling.</b>
/// <see cref="SwitchService.SetValueAsync"/> refuses a write that would take the fan port to its
/// minimum unless the camera's cooler is known to be off. Fails CLOSED on an unknown state.</item>
/// </list>
/// The pure decision helpers are static so they are unit-testable without a device.
/// </summary>
public static class CoolingFanInterlock {

    /// <summary>The device-name marker and port name that identify the cooling fan. Scoped to the
    /// bridge's Thermal Switch so an unrelated switch with a port literally named "Fan" is never
    /// actuated (or blocked) by camera cooling. Mirrors the client's
    /// <c>isThermalSwitchFanPort</c>.</summary>
    public static bool IsThermalSwitchFanPort(SwitchDto device, SwitchPortDto port) {
        ArgumentNullException.ThrowIfNull(port);
        return IsThermalSwitchDevice(device) && port.Name == "Fan";
    }

    /// <summary>The bridge's ToupTek Thermal Switch itself (the device that carries the fan port).</summary>
    public static bool IsThermalSwitchDevice(SwitchDto device) {
        ArgumentNullException.ThrowIfNull(device);
        return device.Name.Contains("Thermal Switch", StringComparison.Ordinal);
    }

    /// <summary>The first CONNECTED switch that carries a writable cooling-fan port, or null.</summary>
    public static (SwitchDto Device, SwitchPortDto Port)? FindThermalSwitchFanPort(IEnumerable<SwitchDto> switches) {
        ArgumentNullException.ThrowIfNull(switches);
        foreach (var device in switches) {
            if (device.State != EquipmentConnectionState.Connected) {
                continue;
            }
            foreach (var port in device.Ports) {
                if (port.CanWrite && IsThermalSwitchFanPort(device, port)) {
                    return (device, port);
                }
            }
        }
        return null;
    }

    /// <summary>The fan write that follows a cooler change: the port's own max (full fan) after
    /// cooler-on, its min (off) after cooler-off. Bounds, not a literal 1/0: on a PWM port
    /// (0–100) a hard-coded 1.0 would set ~1% speed while the TEC cools. Null when no fan port is
    /// connected (most rigs), or when the cached port already holds the target — the §58 warm ramp
    /// calls the cooler once a minute and must not re-issue the same fan write each time.</summary>
    public static (string DeviceId, SwitchValueRequestDto Request)? FanSyncRequest(IEnumerable<SwitchDto> switches, bool cooling) {
        var fan = FindThermalSwitchFanPort(switches);
        if (fan is null) {
            return null;
        }
        var (device, port) = fan.Value;
        var target = cooling ? port.Max : port.Min;
        if (Math.Abs(port.Value - target) < 1e-9) {
            return null;
        }
        return (device.DeviceId, new SwitchValueRequestDto(port.Id, target));
    }

    /// <summary>Whether <paramref name="value"/> takes the port to "off" — its own minimum, so a
    /// PWM fan whose idle stop isn't 0 is still caught.</summary>
    public static bool IsFanOff(SwitchPortDto port, double value) {
        ArgumentNullException.ThrowIfNull(port);
        return value <= port.Min;
    }

    /// <summary>The refusal reason for a fan-off, or null when it is allowed. <paramref name="coolerOn"/>
    /// is tri-state: <c>false</c> = the camera resolved and its cooler is off (allowed; a
    /// no-camera state also reads as off — no TEC this daemon started); <c>true</c> = cooling;
    /// <c>null</c> = the state could not be read. Fails CLOSED on null.</summary>
    public static string? FanOffRefusal(bool? coolerOn) {
        if (coolerOn == false) {
            return null;
        }
        return coolerOn == true
            ? "Turn the cooler off before stopping the fan — cooling with the fan off can damage the camera."
            : "The camera's cooler state is unknown — not stopping the fan while the TEC may be cooling. Check the camera connection and try again.";
    }
}

/// <summary>The fan actuation seam <see cref="CameraService"/> uses to sync the fan after a cooler
/// change. It bypasses the fan-off interlock (the cooler write that motivates the fan-off has just
/// landed, and the cached cooler state may still read on), so it is NOT the general
/// <see cref="ISwitchService.SetValueAsync"/>.</summary>
public interface ICoolingFanActuator {
    Task<IReadOnlyList<SwitchDto>> GetAllAsync(CancellationToken ct);
    Task SetFanValueAsync(string deviceId, SwitchValueRequestDto request, CancellationToken ct);
}
