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
/// #1298 — discovery with the driverless devices added: a filter-wheel listing always ends with
/// the manual filter wheel, found or not, so a rig with a filter drawer can pick it from the same
/// chooser (and at a dark site with no Alpaca server answering at all).
/// </summary>
public sealed class ManualDeviceDiscoveryService : IEquipmentDiscoveryService {

    private readonly IEquipmentDiscoveryService _inner;

    public ManualDeviceDiscoveryService(IEquipmentDiscoveryService inner) {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<IReadOnlyList<DiscoveredDeviceDto>> DiscoverAsync(DeviceType type, bool forceRefresh, CancellationToken ct) {
        var found = await _inner.DiscoverAsync(type, forceRefresh, ct).ConfigureAwait(false);
        if (type != DeviceType.FilterWheel) {
            return found;
        }
        var withManual = new List<DiscoveredDeviceDto>(found.Count + 1);
        withManual.AddRange(found);
        withManual.Add(ManualFilterWheelService.Descriptor);
        return withManual;
    }
}
