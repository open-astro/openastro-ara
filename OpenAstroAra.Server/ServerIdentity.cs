#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.IO;
using System.Net;
using System.Reflection;

namespace OpenAstroAra.Server;

/// <summary>
/// Lightweight server-identity values returned by <c>/api/v1/server/info</c>.
/// Per PORT_PLAYBOOK.md §60.4 + §32.4. WILMA clients use this to verify the
/// daemon they discovered via mDNS is the one they connected to.
///
/// The <see cref="Uuid"/> is generated on first run and persisted under
/// <c>/var/lib/openastroara/server.uuid</c> (Linux) or
/// <c>%APPDATA%/OpenAstroAra/server.uuid</c> (Windows dev) so subsequent
/// daemon restarts identify as the same install. Phase 4 scaffold stores
/// it in-memory only; Phase 5+ promotes to persistent storage via
/// <c>IServerIdentityService</c>.
/// </summary>
public static class ServerIdentity {

    private static readonly Guid _runtimeUuid = Guid.NewGuid();

    /// <summary>Stable identifier for this daemon install.</summary>
    public static string Uuid => _runtimeUuid.ToString();

    /// <summary>
    /// User-friendly name. Defaults to the machine hostname; profile editor (Phase 12)
    /// lets the user override via the §37 setup wizard.
    /// </summary>
    public static string Nickname => Dns.GetHostName();

    /// <summary>Assembly version. Aligns with §33.2 version-matrix entry.</summary>
    public static string Version =>
        typeof(ServerIdentity).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
