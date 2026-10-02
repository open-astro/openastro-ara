#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Contracts;

/// <summary>§33 client-pushed update (#1122): a <c>.deb</c> the client uploaded has been
/// staged and inspected, and may now be applied.</summary>
/// <param name="Id">Request id; used by apply and status.</param>
/// <param name="Package">Package name from the control file (always <c>openastroara-server</c>).</param>
/// <param name="Version">Version the upload would install.</param>
/// <param name="InstalledVersion">Version dpkg reports as installed right now.</param>
/// <param name="SizeBytes">Bytes staged.</param>
public sealed record ServerUpdateStagedDto(
    string Id,
    string Package,
    string Version,
    string InstalledVersion,
    long SizeBytes);

/// <summary>Outcome of an apply: <c>pending</c> (helper running or the daemon restarting),
/// <c>applied</c>, <c>rolled_back</c> (the new daemon never answered <c>/healthz</c>, the
/// previous package was reinstalled), or <c>failed</c> (nothing installed; see Output).</summary>
public sealed record ServerUpdateStatusDto(
    string Id,
    string Status,
    string? FromVersion,
    string? ToVersion,
    bool RollbackAvailable,
    string Output);
