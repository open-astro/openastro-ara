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
/// The PHYSICAL identity of an Alpaca device: its network endpoint (host or IP + port) plus its
/// device number on that server. Used to recognise "the same device" independent of its reported
/// <c>UniqueId</c> — the Alpaca spec intends UniqueIds to be stable, but bridges have been observed
/// renaming them across versions (ZWO dew heater: <c>ZWO_DEW_1</c> → <c>ZWO_DEW_SN_...</c>), and a
/// remembered old-id entry plus a fresh discovery must not be treated as two devices.
/// </summary>
internal static class DiscoveredDeviceEndpoint {

    /// <summary>Case-insensitive endpoint key. Prefers the host NAME (survives a DHCP lease
    /// change) and falls back to the IP when discovery resolved no name.</summary>
    public static string KeyOf(DiscoveredDeviceDto d) {
        var host = string.IsNullOrWhiteSpace(d.HostName) ? d.IpAddress : d.HostName;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{host?.ToUpperInvariant()}:{d.IpPort}:{d.AlpacaDeviceNumber}");
    }
}
