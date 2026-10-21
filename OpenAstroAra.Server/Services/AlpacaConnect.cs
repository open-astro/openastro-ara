#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Alpaca.Clients;
using System;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// The one way a device service sends <c>Connected = true</c>. AlpacaBridge can search for the
/// hardware inside that call: an <c>auto</c> serial device probes every port and an iOptron on
/// <c>network</c> sweeps the local subnets, 8–13 s on the CM4 rig (AB #1452). The ASCOM client's
/// connect timeout is 5 s and it retries once on a timeout, so the connect failed after ~10 s,
/// sent the bridge a second PUT that restarted its search, and the §42.3 ladder then retried
/// again. The connect gets <see cref="Timeout"/> and a single attempt instead.
/// </summary>
internal static class AlpacaConnect {

    /// <summary>How long a device gets to answer <c>Connected = true</c>.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static void Connect(AlpacaDeviceBaseClass client) {
        ArgumentNullException.ThrowIfNull(client);
        WithConnectBudget(client.ClientConfiguration, () => client.Connected = true);
    }

    /// <summary>Runs <paramref name="connect"/> with the connect budget, then restores the client's
    /// own settings. The library uses the same timeout for every <c>Connected</c> GET, which is the
    /// §42.3 liveness probe, so it must stay short once the device is connected.</summary>
    internal static void WithConnectBudget(ClientConfiguration config, Action connect) {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(connect);
        var timeout = config.EstablishConnectionTimeout;
        var retries = config.NumberOfRetries;
        config.EstablishConnectionTimeout = (int) Timeout.TotalSeconds;
        config.NumberOfRetries = 0; // a retried PUT restarts the bridge's search
        try {
            connect();
        } finally {
            config.EstablishConnectionTimeout = timeout;
            config.NumberOfRetries = retries;
        }
    }
}
