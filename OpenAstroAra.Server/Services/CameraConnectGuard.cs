#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using System;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Keeps the main camera and the guider's camera from being the same Alpaca device at the same time. The
/// guider daemon holds its camera open while connected; Ara connecting that device as the MAIN camera too
/// puts two clients on one sensor (each one's connect/disconnect and exposures land on the other — the
/// failure that wrecked guiding when the guide camera was picked as the main camera to focus it). The guide
/// camera is focused through Setup → Smart Focus instead, which borrows frames through the guider.
/// Pure — unit-tested.
/// </summary>
public static class CameraConnectGuard {

    /// <summary>True when <paramref name="device"/> is the guider's configured camera: the profile's
    /// <c>phd2.guider_camera</c> choice string embeds <c>[host:port/N]</c>, matched against the device's
    /// host name or IP (case-insensitive, loopback spellings equal), its Alpaca port and device number. With the
    /// guider on this machine, any of this machine's addresses on both sides is the same server: the client
    /// discovered the camera at 192.168.1.118 while the guider's choice says localhost (#1443).
    /// <paramref name="isThisMachine"/> is injectable for tests.</summary>
    public static bool IsGuiderCamera(DiscoveredDeviceDto device, Phd2SettingsDto phd2, Func<string?, bool>? isThisMachine = null) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(phd2);
        var endpoint = PHD2Guider.ParseAlpacaSelectionEndpoint(phd2.GuiderCamera);
        if (endpoint is not { } ep) {
            return false;
        }
        if (ep.Port != device.IpPort || ep.Device != device.AlpacaDeviceNumber) {
            return false;
        }
        var local = isThisMachine ?? HostLocality.IsThisMachine;
        var host = AsSeenHere(ep.Host, phd2.Host, local);
        if (HostsEqual(host, device.HostName) || HostsEqual(host, device.IpAddress)) {
            return true;
        }
        return !string.IsNullOrWhiteSpace(host) && local(host)
            && HostLocality.LocalizeDevice(device, local).HostName == HostLocality.LocalHostName;
    }

    /// <summary>The guider's choice string names hosts as the guider sees them: its "localhost" is the guider's
    /// own machine. Translated to this machine's view — the guider's host when the guider runs elsewhere,
    /// unchanged when it runs here (a blank guider host is the default local guider).</summary>
    public static string? AsSeenHere(string? choiceHost, string? guiderHost, Func<string?, bool> isThisMachine) {
        ArgumentNullException.ThrowIfNull(isThisMachine);
        var guiderHere = string.IsNullOrWhiteSpace(guiderHost) || isThisMachine(guiderHost);
        return !guiderHere && IsLoopbackName(choiceHost) ? guiderHost : choiceHost;
    }

    private static bool IsLoopbackName(string? host) {
        var h = host?.Trim().Trim('[', ']');
        return !string.IsNullOrEmpty(h)
            && (h.Equals(HostLocality.LocalHostName, StringComparison.OrdinalIgnoreCase)
                || (System.Net.IPAddress.TryParse(h, out var ip) && System.Net.IPAddress.IsLoopback(ip)));
    }

    internal static bool HostsEqual(string? a, string? b) {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) {
            return false;
        }
        static string Normalize(string h) {
            var t = h.Trim().TrimEnd('.');
            if (t.Equals("localhost", StringComparison.OrdinalIgnoreCase) || t == "::1" || t == "127.0.0.1") {
                return "localhost";
            }
            return t.ToLowerInvariant();
        }
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na == nb) {
            return true;
        }
        // "rc91" vs "rc91.lan" / "rc91.local": compare the first label when one side is unqualified.
        static string Label(string h) => h.Split('.')[0];
        return (!na.Contains('.') || !nb.Contains('.')) && Label(na) == Label(nb) && !char.IsDigit(na[0]);
    }

    /// <summary>The 409 detail shown to the user.</summary>
    public static string Detail(DiscoveredDeviceDto device, Phd2SettingsDto phd2) =>
        $"'{device?.Name}' is the guider's camera ({phd2?.GuiderCamera}). Connecting it as the main camera while the guider is connected would put two programs on one sensor and break guiding. To focus the guide camera use Setup → Smart Focus, which reads frames through the guider; to really use it as the main camera, disconnect the guider first.";
}
