#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §63.4 profile-lifecycle hooks — the delete half of the playbook's lifecycle table. The
/// connect path already selects-or-creates the <c>ara-&lt;slug&gt;-&lt;id8&gt;</c> twin
/// (PHD2Guider.EnsureAraGuiderProfileAsync); this partial removes the twin (dark files
/// included) when its ARA profile is deleted, so orphaned guider profiles and their dark
/// libraries don't accumulate on the daemon.
/// </summary>
public sealed partial class GuiderService {

    /// <summary>
    /// Best-effort §63.4 cleanup for a just-deleted ARA profile. Maps the profile to its
    /// PHD2 twin (<see cref="PHD2Guider.AraGuiderProfileName(string?, Guid)"/> — the same
    /// id-suffixed name the connect path creates) and asks the daemon to delete it, dark
    /// files included. NEVER throws — the ARA profile is already gone, and a guider that
    /// is disconnected, unreachable, or never had the twin (the profile never connected a
    /// guider) is an expected non-event, logged and reported as <c>false</c>.
    /// <para>The daemon rejects deleting its SELECTED profile, and ARA's active-profile
    /// guard is NOT enough to rule that out: the selected twin tracks the last guider
    /// CONNECT, while `POST /profiles/{id}/select` switches the active ARA profile without
    /// touching the guider — so a profile switched away from since the last connect can be
    /// deletable in ARA while its twin is still selected on the daemon. That case is
    /// detected here and skipped with its own log line (delete the twin later by
    /// reconnecting the guider — the connect re-maps to the NEW active profile's twin,
    /// deselecting this one).</para>
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort lifecycle cleanup: the ARA delete already succeeded, so any RPC/socket/protocol fault here must degrade to a logged skip — never a thrown error that could confuse the (fire-and-forget) caller or surface as an unobserved task exception.")]
    public async Task<bool> TryDeleteAraGuiderProfileAsync(
            string? araProfileName, Guid araProfileId, CancellationToken ct) {
        PHD2Guider? guider;
        lock (_gate) {
            guider = !_disposed && _state == EquipmentConnectionState.Connected ? _guider : null;
        }
        var name = PHD2Guider.AraGuiderProfileName(araProfileName, araProfileId);
        if (guider is null) {
            LogGuiderProfileDeleteSkipped(name);
            return false;
        }
        if (IsTwinSelectedOnDaemon(name, guider.SelectedProfile?.Name)) {
            // The daemon would reject this delete outright (see remarks) — skip with a
            // distinct, diagnosable message instead of burning an RPC on a known refusal.
            LogGuiderProfileDeleteRefusedSelected(name);
            return false;
        }
        try {
            return await guider.DeleteGuiderProfileAsync(name, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            LogGuiderProfileDeleteFailed(ex, name);
            return false;
        }
    }

    /// <summary>The selected-twin guard, pure for tests: PHD2 profile names are exact
    /// identifiers, so the comparison is Ordinal; a daemon with no selected profile
    /// (null) never blocks a delete.</summary>
    internal static bool IsTwinSelectedOnDaemon(string twinName, string? daemonSelectedName) =>
        string.Equals(daemonSelectedName, twinName, StringComparison.Ordinal);

    [LoggerMessage(EventId = 6360, Level = LogLevel.Information, Message = "§63.4 guider profile '{ProfileName}' not cleaned up — no guider connected (the twin, if any, remains on the daemon)")]
    private partial void LogGuiderProfileDeleteSkipped(string profileName);

    [LoggerMessage(EventId = 6361, Level = LogLevel.Warning, Message = "§63.4 guider profile '{ProfileName}' not cleaned up — it is still the guider daemon's SELECTED profile (the guider connected under this ARA profile and no reconnect has re-mapped it since). Reconnect the guider to re-map, then the twin can be removed")]
    private partial void LogGuiderProfileDeleteRefusedSelected(string profileName);

    [LoggerMessage(EventId = 6362, Level = LogLevel.Warning, Message = "§63.4 guider profile '{ProfileName}' cleanup failed — the ARA profile delete already succeeded; the twin remains on the daemon")]
    private partial void LogGuiderProfileDeleteFailed(Exception ex, string profileName);
}
