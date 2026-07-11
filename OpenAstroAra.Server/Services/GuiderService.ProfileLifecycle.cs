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
    /// guider) is an expected non-event, logged and reported as <c>false</c>. ARA refuses
    /// deleting the ACTIVE profile, so the twin can never be the daemon's selected profile.
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
        try {
            return await guider.DeleteGuiderProfileAsync(name, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            LogGuiderProfileDeleteFailed(ex, name);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "§63.4 guider profile '{ProfileName}' not cleaned up — no guider connected (the twin, if any, remains on the daemon)")]
    private partial void LogGuiderProfileDeleteSkipped(string profileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "§63.4 guider profile '{ProfileName}' cleanup failed — the ARA profile delete already succeeded; the twin remains on the daemon")]
    private partial void LogGuiderProfileDeleteFailed(Exception ex, string profileName);
}
