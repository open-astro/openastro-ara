#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors
    Copyright © 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ASCOM.Alpaca.Discovery;
using ASCOM.Common;
using OpenAstroAra.Equipment.Interfaces;

namespace OpenAstroAra.Equipment.Providers {

    /// <summary>
    /// Single concrete implementation of <see cref="IEquipmentProvider"/> per
    /// PORT_PLAYBOOK.md §6.2 + §52 (Alpaca-only commitment).
    ///
    /// Uses <c>ASCOM.Alpaca.Discovery</c> for UDP broadcast discovery on port 32227
    /// and hands out typed proxies from <c>ASCOM.Alpaca.Components</c>.
    ///
    /// Vendor-specific providers (Canon, Nikon, ZWO, QHY, Atik, ToupTek, FLI, SBIG,
    /// PlayerOne, SVBony, ASTPAN) were deleted in Phase 0.5c. ARA talks to all
    /// equipment via Alpaca; typically through AlpacaBridge running on the same
    /// Raspberry Pi as the OpenAstroAra.Server daemon.
    /// </summary>
    public sealed class AlpacaEquipmentProvider : IEquipmentProvider {

        public string Id => "alpaca";
        public string DisplayName => "ASCOM Alpaca";

        // Discovery tuning constants. Defaults come from ASCOM Alpaca's recommended
        // configuration; profile-level overrides land in Phase 4 (server scaffold)
        // where the daemon reads them from the active profile.
        private const int DefaultNumberOfPolls = 1;
        private const int DefaultPollInterval = 100;
        private const int DefaultDiscoveryPort = 32227;
        private const int DefaultDiscoveryDuration = 2;

        public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(DeviceType type, CancellationToken ct) {
            var ascomType = MapDeviceType(type);
            var discovered = await AlpacaDiscovery.GetAscomDevicesAsync(
                deviceTypes: ascomType,
                numberOfPolls: DefaultNumberOfPolls,
                pollInterval: DefaultPollInterval,
                discoveryPort: DefaultDiscoveryPort,
                discoveryDuration: DefaultDiscoveryDuration,
                resolveDnsName: true,
                useIpV4: true,
                useIpV6: false,
                serviceType: ASCOM.Common.Alpaca.ServiceType.Http,
                logger: null,
                cancellationToken: ct).ConfigureAwait(false);

            var results = new List<DiscoveredDevice>(discovered.Count);
            foreach (var d in discovered) {
                results.Add(new DiscoveredDevice(
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

        public Task<T> ConnectAsync<T>(DiscoveredDevice device, CancellationToken ct) where T : class {
            // TODO(phase-4): full proxy instantiation via ASCOM.Alpaca.Components +
            // wiring into the server's equipment session state per playbook §8.
            // Stub throws for now; daemon scaffold (Phase 4) replaces this
            // with the real typed-proxy factory.
            throw new NotImplementedException(
                "AlpacaEquipmentProvider.ConnectAsync<T> is wired up in Phase 4 (server scaffold). " +
                "Discovery already works via DiscoverAsync; connect-and-proxy follows when " +
                "OpenAstroAra.Server lands.");
        }

        private static DeviceTypes MapDeviceType(DeviceType t) => t switch {
            DeviceType.Camera => DeviceTypes.Camera,
            DeviceType.Telescope => DeviceTypes.Telescope,
            DeviceType.Focuser => DeviceTypes.Focuser,
            DeviceType.FilterWheel => DeviceTypes.FilterWheel,
            DeviceType.Rotator => DeviceTypes.Rotator,
            DeviceType.Dome => DeviceTypes.Dome,
            DeviceType.SafetyMonitor => DeviceTypes.SafetyMonitor,
            DeviceType.Switch => DeviceTypes.Switch,
            DeviceType.ObservingConditions => DeviceTypes.ObservingConditions,
            DeviceType.CoverCalibrator => DeviceTypes.CoverCalibrator,
            _ => throw new ArgumentOutOfRangeException(nameof(t), t, "Unknown device type")
        };
    }
}
