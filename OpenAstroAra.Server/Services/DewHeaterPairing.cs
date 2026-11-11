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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1663 — which switch port is the MAIN camera's own anti-dew heater. AlpacaBridge exposes the heater
/// as a separate Switch device with a <c>DewHeater</c> port, and both devices carry the camera's serial
/// after <c>_SN_</c> in their UniqueId (<c>ZWO_SN_x</c> with <c>ZWO_DEW_SN_x</c>, the ToupTek family's
/// <c>TOUPTEK_THERMALSW_SN_x</c>, <c>PLAYERONE_SW_SN_x</c>). Pairing is by that serial alone: never by
/// name or list order, so two same-brand cameras can't swap heaters. A switch's <see cref="SwitchDto.DeviceId"/>
/// IS its Alpaca UniqueId. Pure, so it is unit-testable without a device.
/// </summary>
public static class DewHeaterPairing {

    private const string SerialMarker = "_SN_";

    /// <summary>The port name AlpacaBridge gives a camera heater.</summary>
    public const string PortName = "DewHeater";

    /// <summary>The serial after <c>_SN_</c> in a UniqueId, or null when there is none (a ZWO body
    /// without a serial is listed as <c>ZWO_DEW_&lt;n&gt;</c>).</summary>
    public static string? SerialOf(string? uniqueId) {
        if (string.IsNullOrEmpty(uniqueId)) {
            return null;
        }
        var at = uniqueId.IndexOf(SerialMarker, StringComparison.Ordinal);
        if (at < 0) {
            return null;
        }
        var serial = uniqueId[(at + SerialMarker.Length)..].Trim();
        return serial.Length == 0 ? null : serial;
    }

    /// <summary>The CONNECTED switch port that heats the camera with <paramref name="cameraUniqueId"/>,
    /// or null: no serial on either side, no <c>DewHeater</c> port, or the switch not connected.
    /// Call it for the main camera slot only; the guide camera is never paired.</summary>
    public static (SwitchDto Device, SwitchPortDto Port)? Find(string? cameraUniqueId, IEnumerable<SwitchDto> switches) {
        ArgumentNullException.ThrowIfNull(switches);
        var serial = SerialOf(cameraUniqueId);
        if (serial is null) {
            return null;
        }
        foreach (var device in switches) {
            if (device.State != EquipmentConnectionState.Connected
                || !string.Equals(SerialOf(device.DeviceId), serial, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            foreach (var port in device.Ports) {
                if (string.Equals(port.Name, PortName, StringComparison.OrdinalIgnoreCase)) {
                    return (device, port);
                }
            }
        }
        return null;
    }
}
