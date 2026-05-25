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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Interfaces {

    /// <summary>
    /// Per PORT_PLAYBOOK.md §6.2 (Phase 2 — Equipment to Alpaca-only).
    /// Single concrete impl: <see cref="OpenAstroAra.Equipment.Providers.AlpacaEquipmentProvider"/>.
    /// Vendor-specific providers (Canon, Nikon, ZWO, QHY, Atik, ...) were deleted in Phase 0.5c.
    /// </summary>
    public interface IEquipmentProvider {
        /// <summary>Stable identifier ("alpaca"). Used for profile persistence + telemetry.</summary>
        string Id { get; }

        /// <summary>Human-readable name shown in UI ("ASCOM Alpaca").</summary>
        string DisplayName { get; }

        /// <summary>
        /// Discover devices of <paramref name="type"/> on the local network.
        /// For Alpaca: broadcast UDP on port 32227 + collect responses.
        /// </summary>
        Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(DeviceType type, CancellationToken ct);

        /// <summary>
        /// Connect to a previously-discovered device and return a typed proxy.
        /// For Alpaca: hand back an instance from ASCOM.Alpaca.Components.
        /// </summary>
        Task<T> ConnectAsync<T>(DiscoveredDevice device, CancellationToken ct) where T : class;
    }

    /// <summary>
    /// Device type enumeration matching ASCOM device categories. Used by
    /// <see cref="IEquipmentProvider.DiscoverAsync"/>.
    /// </summary>
    public enum DeviceType {
        Camera,
        Telescope,
        Focuser,
        FilterWheel,
        Rotator,
        Dome,
        SafetyMonitor,
        Switch,
        ObservingConditions,
        CoverCalibrator
    }

    /// <summary>
    /// Lightweight descriptor of an Alpaca-discovered device. Returned by
    /// <see cref="IEquipmentProvider.DiscoverAsync"/>; passed back to
    /// <see cref="IEquipmentProvider.ConnectAsync{T}"/>.
    /// </summary>
    public sealed record DiscoveredDevice(
        string UniqueId,
        string Name,
        DeviceType Type,
        string HostName,
        string IpAddress,
        int IpPort,
        int AlpacaDeviceNumber,
        bool UseHttps);
}
