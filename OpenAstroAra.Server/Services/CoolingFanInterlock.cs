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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §25.5.6 / #1065 — the camera-cooling fan interlock, daemon-side. The profile designates the
/// switch port that vents the TEC's heat sink (<see cref="CoolingFanDto"/>, #1700: Alpaca has no
/// fan capability and ADR 0003 rule 2 forbids picking one by device name; no designation, no
/// interlock); two rules keep the camera safe and they now live HERE so every path to the cooler and the fan that goes through
/// these services (REST from any client, the §58 unattended shutdown's warm ramp, the sequencer's
/// SetSwitchValue instruction via <see cref="SwitchService.SetSwitchValue"/>, #1076) gets them, not
/// just the Flutter UI's own buttons.
/// <list type="number">
/// <item><b>The fan follows the cooler.</b> <see cref="CameraService.SetCoolerAsync"/> writes the
/// fan port to its max BEFORE cooler-on (a rig whose fan cannot be started refuses to start
/// cooling, #1076) and to its min after cooler-off.</item>
/// <item><b>No fan-off while the TEC is (or may be) cooling.</b>
/// <see cref="SwitchService.SetValueAsync"/> refuses a write that would take the fan port to its
/// minimum unless the camera's cooler is known to be off. Fails CLOSED on an unknown state.</item>
/// </list>
/// The pure decision helpers are static so they are unit-testable without a device.
/// </summary>
public static class CoolingFanInterlock {

    /// <summary>Whether <paramref name="port"/> on <paramref name="device"/> is the designated fan.</summary>
    public static bool IsDesignatedFanPort(SwitchDto device, SwitchPortDto port, CoolingFanDto fan) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(fan);
        return device.DeviceId == fan.DeviceId && port.Id == fan.PortId;
    }

    /// <summary>Why a designation can't be stored, or null: device and port come as a pair, and the
    /// port is an ASCOM short.</summary>
    public static string? DesignationError(CoolingFanDto fan) {
        ArgumentNullException.ThrowIfNull(fan);
        if (fan.DeviceId is null && fan.PortId is null) {
            return null;
        }
        return string.IsNullOrEmpty(fan.DeviceId) || fan.PortId is not (>= 0 and <= short.MaxValue)
            ? "device_id and port_id go together: a switch's id and a port id from 0 to 32767, or both null for no fan."
            : null;
    }

    /// <summary>The designated port, when its switch is CONNECTED and the port writable; else null.</summary>
    public static (SwitchDto Device, SwitchPortDto Port)? FindFanPort(IEnumerable<SwitchDto> switches, CoolingFanDto fan) {
        ArgumentNullException.ThrowIfNull(switches);
        foreach (var device in switches) {
            if (device.State != EquipmentConnectionState.Connected) {
                continue;
            }
            foreach (var port in device.Ports) {
                if (port.CanWrite && IsDesignatedFanPort(device, port, fan)) {
                    return (device, port);
                }
            }
        }
        return null;
    }

    /// <summary>The fan write that follows a cooler change: the port's own max (full fan) after
    /// cooler-on, its min (off) after cooler-off. Bounds, not a literal 1/0: on a PWM port
    /// (0–100) a hard-coded 1.0 would set ~1% speed while the TEC cools. Null when no fan port is
    /// connected (most rigs). A cooler-ON ALWAYS writes (the cached value may be a stale snapshot,
    /// and a missed fan-on is the hazard); a cooler-OFF skips when the cached port already holds the
    /// floor — the §58 warm ramp calls the cooler once a minute and must not re-issue the same fan
    /// write each time (#1076).</summary>
    public static (string DeviceId, SwitchValueRequestDto Request)? FanSyncRequest(IEnumerable<SwitchDto> switches, CoolingFanDto designation, bool cooling) {
        var fan = FindFanPort(switches, designation);
        if (fan is null) {
            return null;
        }
        var (device, port) = fan.Value;
        var target = cooling ? port.Max : port.Min;
        if (!cooling && Math.Abs(port.Value - target) < 1e-9) {
            return null;
        }
        return (device.DeviceId, new SwitchValueRequestDto(port.Id, target));
    }

    /// <summary>The cooler state the fan-off interlock decides on, from the camera's DTO (#1076):
    /// <c>null</c> (unknown → refuse) when the camera is in <c>Error</c> (it just dropped — the TEC
    /// may still be running) or when this pass's CoolerOn read threw; <c>false</c> when there is
    /// no camera connected — no DTO at all (no camera device was ever configured on this daemon)
    /// or a <c>Disconnected</c> one — since no TEC this daemon started can be running, and for a
    /// camera whose capabilities say it has no cooler at all (its CoolerOn read throws on every
    /// pass, so the runtime flag alone would read as unknown forever); otherwise the reported flag. (A probe that THROWS is unknown, but that is the caller's boundary.)</summary>
    public static bool? CoolerStateFor(CameraDto? camera) {
        if (camera is null) {
            return false;
        }
        if (camera.State == EquipmentConnectionState.Error) {
            return null;
        }
        if (camera.State != EquipmentConnectionState.Connected) {
            return false;
        }
        if (camera.Capabilities is { HasCooler: false }) {
            // An uncooled camera throws on EVERY CoolerOn read (that is how HasCooler was
            // decided), so its runtime CoolerStateKnown is false for good — but "no cooler" is
            // definitively no TEC, not unknown (review of #1084).
            return false;
        }
        return camera.Runtime.CoolerStateKnown ? camera.Runtime.CoolerOn : null;
    }

    /// <summary>Whether <paramref name="value"/> takes the port to "off" — its own minimum, so a
    /// PWM fan whose idle stop isn't 0 is still caught.</summary>
    public static bool IsFanOff(SwitchPortDto port, double value) {
        ArgumentNullException.ThrowIfNull(port);
        return value <= port.Min;
    }

    /// <summary>Whether a write to a CONNECTED switch could be stopping the cooling fan, and so must
    /// ask the camera before it goes through: the designated port driven to its floor, or — while
    /// that port is not in the snapshot (unread for up to one refresh interval after connect, or
    /// skipped by a pass whose read of it failed) — a write of <c>value &lt;= 0</c> to it, the one
    /// way to drive an unread port to its floor. A fan-on, another port or a mid-range PWM value is
    /// never held: refusing a fan-on is the wrong direction for the hazard this interlock exists for.</summary>
    public static bool IsPlausibleFanOff(SwitchDto device, CoolingFanDto fan, SwitchValueRequestDto request) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(fan);
        ArgumentNullException.ThrowIfNull(request);
        if (device.State != EquipmentConnectionState.Connected || device.DeviceId != fan.DeviceId || request.PortId != fan.PortId) {
            return false;
        }
        var port = device.Ports.FirstOrDefault(p => p.Id == fan.PortId);
        return port is null ? request.Value <= 0 : IsFanOff(port, request.Value);
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
