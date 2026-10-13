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
        // The twin may exist under the display name (current scheme) or the legacy
        // ara-<slug>-<id8> name (created before the rename migration, or never migrated
        // because the guider hasn't reconnected since) — try both; either hit is success.
        // Display names aren't unique across ARA profiles: identically-named profiles
        // SHARE a twin by design (AraGuiderDisplayProfileName remarks), so deleting one
        // of them takes the shared twin with it — the next connect of the survivor
        // recreates it.
        var name = PHD2Guider.AraGuiderDisplayProfileName(araProfileName);
        var legacyName = PHD2Guider.AraGuiderProfileName(araProfileName, araProfileId);
        if (guider is null) {
            LogGuiderProfileDeleteSkipped(name);
            return false;
        }
        var deleted = false;
        foreach (var candidate in new[] { name, legacyName }) {
            if (IsTwinSelectedOnDaemon(candidate, guider.SelectedProfile?.Name)) {
                // The daemon would reject this delete outright (see remarks) — skip with a
                // distinct, diagnosable message instead of burning an RPC on a known refusal.
                LogGuiderProfileDeleteRefusedSelected(candidate);
                continue;
            }
            try {
                deleted |= await guider.DeleteGuiderProfileAsync(candidate, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                LogGuiderProfileDeleteFailed(ex, candidate);
            }
        }
        return deleted;
    }

    /// <summary>
    /// #1156 clone hook — a duplicated ARA profile gets its twin cloned from the source
    /// profile's twin (<c>create_profile</c> + <c>copy_from</c>), so calibration survives the
    /// duplicate. Best-effort like the delete hook: no connected guider, identical twin
    /// names (identically-named ARA profiles share a twin), a missing source twin or a
    /// daemon refusal all log and return false. Never throws.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort lifecycle hook: the ARA create already succeeded; any RPC/socket fault degrades to a logged skip so the fire-and-forget caller never sees an unobserved exception.")]
    public async Task<bool> TryCloneAraGuiderProfileAsync(string? sourceAraProfileName, string? newAraProfileName, CancellationToken ct) {
        var source = PHD2Guider.AraGuiderDisplayProfileName(sourceAraProfileName);
        var target = PHD2Guider.AraGuiderDisplayProfileName(newAraProfileName);
        if (SameTwin(source, target)) {
            return false; // same display name → same twin already
        }
        var guider = ConnectedGuiderOrNull();
        if (guider is null) {
            LogGuiderProfileCloneSkipped(target);
            return false;
        }
        try {
            return await guider.CloneGuiderProfileAsync(source, target, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            LogGuiderProfileCloneFailed(ex, source, target);
            return false;
        }
    }

    /// <summary>
    /// #1156 rename hook — a renamed ARA profile keeps its twin (<c>rename_profile</c>) instead
    /// of leaving the calibrated one orphaned and getting a blank one on the next connect.
    /// Tries the display-name twin first, then the legacy <c>ara-&lt;slug&gt;-&lt;id8&gt;</c> name.
    /// Best-effort like the delete hook; never throws. The caller decides whether the old
    /// name is shared with another ARA profile (then the twin must stay) — this method only
    /// talks to the daemon.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort lifecycle hook: the ARA rename already succeeded; any RPC/socket fault degrades to a logged skip so the fire-and-forget caller never sees an unobserved exception.")]
    public async Task<bool> TryRenameAraGuiderProfileAsync(string? oldAraProfileName, Guid araProfileId, string? newAraProfileName, CancellationToken ct) {
        var oldName = PHD2Guider.AraGuiderDisplayProfileName(oldAraProfileName);
        var legacyName = PHD2Guider.AraGuiderProfileName(oldAraProfileName, araProfileId);
        var newName = PHD2Guider.AraGuiderDisplayProfileName(newAraProfileName);
        if (SameTwin(oldName, newName)) {
            return false; // a case-only change maps to the same twin (the daemon's names are case-insensitive)
        }
        var guider = ConnectedGuiderOrNull();
        if (guider is null) {
            LogGuiderProfileRenameSkipped(oldName);
            return false;
        }
        foreach (var candidate in new[] { oldName, legacyName }) {
            try {
                if (await guider.RenameGuiderProfileAsync(candidate, newName, ct).ConfigureAwait(false)) {
                    return true;
                }
            } catch (Exception ex) {
                LogGuiderProfileRenameFailed(ex, candidate, newName);
            }
        }
        return false;
    }

    /// <summary>Two ARA display names map to one twin when they differ only by case: the daemon's
    /// profile-name uniqueness and <c>ResolveAraProfileSelection</c> are both case-insensitive.</summary>
    internal static bool SameTwin(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private PHD2Guider? ConnectedGuiderOrNull() {
        lock (_gate) {
            return !_disposed && _state == EquipmentConnectionState.Connected ? _guider : null;
        }
    }

    [LoggerMessage(EventId = 6363, Level = LogLevel.Information, Message = "#1156 guider profile '{ProfileName}' not cloned — no guider connected (the next connect creates a blank twin)")]
    private partial void LogGuiderProfileCloneSkipped(string profileName);

    [LoggerMessage(EventId = 6364, Level = LogLevel.Warning, Message = "#1156 cloning guider profile '{Source}' → '{Target}' failed — the ARA duplicate already succeeded; the next connect creates a blank twin")]
    private partial void LogGuiderProfileCloneFailed(Exception ex, string source, string target);

    [LoggerMessage(EventId = 6365, Level = LogLevel.Information, Message = "#1156 guider profile '{ProfileName}' not renamed — no guider connected (the next connect creates a blank twin under the new name)")]
    private partial void LogGuiderProfileRenameSkipped(string profileName);

    [LoggerMessage(EventId = 6366, Level = LogLevel.Warning, Message = "#1156 renaming guider profile '{Source}' → '{Target}' failed — the ARA rename already succeeded")]
    private partial void LogGuiderProfileRenameFailed(Exception ex, string source, string target);

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
