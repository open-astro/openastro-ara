#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Alpaca.Discovery;
using ASCOM.Common;
using OpenAstroAra.Server.Contracts;
using Contracts = OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// First concrete service implementation under Phase 6. Discovers Alpaca
/// devices on the LAN via UDP broadcast on port 32227 + maps the upstream
/// ASCOM.Alpaca.Discovery result into <see cref="DiscoveredDeviceDto"/>.
///
/// This bypasses the Phase 2 <c>OpenAstroAra.Equipment.IEquipmentProvider</c>
/// type (which lives in the net10.0-windows Equipment project the server
/// can't reference cross-platform) and goes straight to the ASCOM.Alpaca
/// NuGet package — same effective behavior, cross-platform compatible.
/// The Equipment-project provider is kept as the equivalent class for
/// inherited NINA code that may still call into it; the daemon uses this
/// implementation directly.
///
/// Discovery results are NOT cached at this layer (each call triggers a
/// fresh broadcast). Phase 6 will add a §32.4 cache_ttl wrapper before
/// promoting the service tier to "ready".
/// </summary>
public sealed class AlpacaEquipmentDiscoveryService : IEquipmentDiscoveryService {

    public async Task<IReadOnlyList<DiscoveredDeviceDto>> DiscoverAsync(
            Contracts.DeviceType type, bool forceRefresh, CancellationToken ct) {
        var ascomType = MapDeviceType(type);
        var discovered = await AlpacaDiscovery.GetAscomDevicesAsync(
            deviceTypes: ascomType,
            numberOfPolls: 1,
            pollInterval: 100,
            discoveryPort: 32227,
            discoveryDuration: 2.0,
            resolveDnsName: true,
            useIpV4: true,
            useIpV6: false,
            serviceType: ASCOM.Common.Alpaca.ServiceType.Http,
            logger: null,
            cancellationToken: ct).ConfigureAwait(false);

        var results = new List<DiscoveredDeviceDto>(discovered.Count);
        foreach (var d in discovered) {
            results.Add(new DiscoveredDeviceDto(
                UniqueId: d.UniqueId,
                Name: d.AscomDeviceName,
                Type: type,
                HostName: d.HostName,
                IpAddress: d.IpAddress,
                IpPort: d.IpPort,
                AlpacaDeviceNumber: d.AlpacaDeviceNumber,
                UseHttps: false));
        }
        return results;
    }

    private static DeviceTypes MapDeviceType(Contracts.DeviceType t) => t switch {
        Contracts.DeviceType.Camera => DeviceTypes.Camera,
        Contracts.DeviceType.Telescope => DeviceTypes.Telescope,
        Contracts.DeviceType.Focuser => DeviceTypes.Focuser,
        Contracts.DeviceType.FilterWheel => DeviceTypes.FilterWheel,
        Contracts.DeviceType.Rotator => DeviceTypes.Rotator,
        Contracts.DeviceType.Dome => DeviceTypes.Dome,
        Contracts.DeviceType.SafetyMonitor => DeviceTypes.SafetyMonitor,
        Contracts.DeviceType.Switch => DeviceTypes.Switch,
        Contracts.DeviceType.ObservingConditions => DeviceTypes.ObservingConditions,
        Contracts.DeviceType.CoverCalibrator => DeviceTypes.CoverCalibrator,
        // FlatDevice is a NINA UX-facing concept that maps to Alpaca's CoverCalibrator.
        // See DeviceType XML doc; both tokens are kept for client clarity.
        Contracts.DeviceType.FlatDevice => DeviceTypes.CoverCalibrator,
        Contracts.DeviceType.Guider => throw new ArgumentException(
            "Guider (PHD2) is not an Alpaca device; use IGuiderService.ConnectAsync instead.", nameof(t)),
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "Unknown device type")
    };
}