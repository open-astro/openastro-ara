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
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1308 — the guide camera is pinned by Alpaca device NUMBER (<c>Alpaca Camera [host:port/N]</c>), but
/// AlpacaBridge numbers ZWO cameras in SDK enumeration order, which can change across a restart. Ara's
/// own cameras survive that because they reconnect by <c>UniqueID</c>; this check gives the guider the
/// same protection. Before the full profile push, the stored camera's UniqueID is re-resolved against
/// the server's <c>configureddevices</c>: a camera that moved to another number, or that is no longer
/// offered, is refused with a reason naming the slot, and so is a guide camera that resolves to the
/// main imaging camera, whatever its number. Pure: the caller fetches the devices and the main camera.
/// </summary>
public static class GuiderCameraPinCheck {

    /// <summary>The server the guide camera lives on, or null for a non-Alpaca / bare / unset selection.</summary>
    public static (string Host, int Port, int Device)? CameraEndpoint(IGuiderSettings settings) {
        ArgumentNullException.ThrowIfNull(settings);
        return PHD2Guider.ParseAlpacaSelectionEndpoint(settings.GuiderCamera);
    }

    /// <summary>Returns a one-line refusal, or null when the selection is safe to push.
    /// <paramref name="devices"/> is that server's <c>configureddevices</c> map ("type/number" → device),
    /// EMPTY when the lookup failed — in which case the identity check is skipped (an unreachable
    /// management API must not block a push the guider itself may still manage) and only the
    /// main-camera check by host/port/number runs. <paramref name="mainCamera"/> is the imaging
    /// camera the daemon holds (connected or retained), or null.</summary>
    public static string? Describe(IGuiderSettings settings,
            IReadOnlyDictionary<string, AlpacaConfiguredDevice> devices, DiscoveredDeviceDto? mainCamera) {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(devices);
        if (CameraEndpoint(settings) is not { } ep) {
            return null;
        }
        var key = $"camera/{ep.Device.ToString(CultureInfo.InvariantCulture)}";
        var selection = settings.GuiderCamera.Trim();
        var pinned = (settings.GuiderCameraAlpacaUniqueId ?? string.Empty).Trim();

        if (devices.Count > 0 && pinned.Length > 0) {
            devices.TryGetValue(key, out var atNumber);
            if (atNumber is null || !string.Equals(atNumber.UniqueId, pinned, StringComparison.Ordinal)) {
                var moved = devices.FirstOrDefault(d => d.Key.StartsWith("camera/", StringComparison.Ordinal)
                    && string.Equals(d.Value.UniqueId, pinned, StringComparison.Ordinal));
                if (moved.Key is not null) {
                    return $"The guide camera '{selection}' has moved: the camera you picked ({moved.Value.Name}, {pinned}) is now "
                        + $"{moved.Key} on {ep.Host}:{ep.Port}, and {key} is {(atNumber is null ? "not offered" : $"'{atNumber.Name}'")}. "
                        + "Pick the guide camera again before applying.";
                }
                return $"The guide camera '{selection}' is no longer offered by {ep.Host}:{ep.Port}: the camera you picked "
                    + $"({pinned}) is not in its device list{(atNumber is null ? "" : $" and {key} is now '{atNumber.Name}'")}. "
                    + "Pick the guide camera again before applying.";
            }
        }

        if (mainCamera is null) {
            return null;
        }
        // The main imaging camera, by identity when the server told us ids, else by address.
        var sameServer = ep.Port == mainCamera.IpPort
            && (string.Equals(ep.Host, mainCamera.IpAddress, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ep.Host, mainCamera.HostName, StringComparison.OrdinalIgnoreCase)
                || (IsLoopback(ep.Host) && (IsLoopback(mainCamera.IpAddress) || IsLoopback(mainCamera.HostName))));
        var byId = devices.TryGetValue(key, out var picked) && picked.UniqueId.Length > 0
            && string.Equals(picked.UniqueId, mainCamera.UniqueId, StringComparison.Ordinal);
        var byNumber = sameServer && ep.Device == mainCamera.AlpacaDeviceNumber;
        if (byId || byNumber) {
            return $"The guide camera '{selection}' is the main imaging camera ({mainCamera.Name}). "
                + "The guider must not open the camera the sequence images with; pick the guide camera instead.";
        }
        return null;
    }

    private static bool IsLoopback(string? host) =>
        !string.IsNullOrEmpty(host)
        && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.StartsWith("127.", StringComparison.Ordinal) || host == "::1");
}
