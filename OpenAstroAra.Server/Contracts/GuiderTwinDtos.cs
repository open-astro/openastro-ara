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
using System.Collections.Generic;

namespace OpenAstroAra.Server.Contracts;

/// <summary>
/// §63.4 — <c>GET /api/v1/profiles/{id}/guider-twin</c>: what the delete dialog needs to know about an
/// ARA profile's OpenAstro Guider profile (its "twin") before asking whether to delete it too.
/// </summary>
/// <param name="TwinNames">The names the twin may carry on the daemon: the ARA profile's display name
/// (current scheme) and the legacy <c>ara-&lt;slug&gt;-&lt;id8&gt;</c> name.</param>
/// <param name="Exists">Whether either name is on the daemon; null when no guider is connected (unknown).</param>
/// <param name="Shared">Another ARA profile maps to the same twin (identically named profiles share one),
/// so deleting it takes the survivor's calibration and dark library too.</param>
/// <param name="SharedWith">The other profile's name when <paramref name="Shared"/>.</param>
/// <param name="SelectedOnDaemon">The twin is the daemon's currently selected profile.</param>
/// <param name="CanDeleteNow">The delete can run immediately; otherwise it is queued and
/// <paramref name="Reason"/> says why (no guider connected, guider busy).</param>
public sealed record GuiderTwinPreviewDto(
    IReadOnlyList<string> TwinNames,
    bool? Exists,
    bool Shared,
    string? SharedWith,
    bool SelectedOnDaemon,
    bool CanDeleteNow,
    string? Reason);

/// <summary>
/// §63.4 — the body of a successful <c>DELETE /api/v1/profiles/{id}</c>: what happened to the guider twin.
/// <paramref name="GuiderProfile"/> is one of <c>deleted</c> (gone from the daemon), <c>queued</c> (will be
/// deleted on the next guider connect or once guiding stops), <c>kept</c> (not asked for), <c>not_found</c>
/// (no twin on the daemon) or <c>failed</c> (the daemon refused; <paramref name="Reason"/> carries its text).
/// </summary>
public sealed record ProfileDeleteResultDto(string GuiderProfile, string? Reason);

/// <summary>A guider-twin delete waiting for a guider that is connected and idle (persisted next to the
/// profiles so it survives a daemon restart). <paramref name="Shared"/> records that another ARA profile
/// mapped to the twin when the user asked anyway: the drain then deletes it regardless of live names,
/// exactly as the immediate path would have.</summary>
public sealed record PendingGuiderTwinDelete(
    Guid AraProfileId,
    string? AraProfileName,
    IReadOnlyList<string> TwinNames,
    bool DeleteDarkFiles,
    DateTimeOffset QueuedUtc,
    bool Shared = false);

/// <summary>On-disk shape of the pending guider-twin delete queue.</summary>
public sealed record PendingGuiderTwinDeleteFile(IReadOnlyList<PendingGuiderTwinDelete> Pending);
