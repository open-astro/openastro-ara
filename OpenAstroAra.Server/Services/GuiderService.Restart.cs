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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

// §63.17 (PR 6) — manual restart: POST /guider/restart surfaces the §63.3 supervisor's systemctl restart
// for the "guider is wedged and I want to kick it myself" case the §63.12 table planned. Deliberately NOT
// gated on a connected guider: a manual restart is most useful precisely when the daemon is hung or the
// RPC connection is down. Recovery/auto-reconnect then proceeds through the existing §63.3 machinery
// (guider.state WS events report the outcome), so this is fire-and-forget with an immediate 202.
public sealed partial class GuiderService {

    /// <summary>§63.17 — request a systemd restart of the guider unit (idempotent per §60.5: repeating the
    /// request while a restart is in flight just re-requests the same unit restart, which systemd coalesces).
    /// No-op on hosts without systemd (dev machines) — the supervisor swallows it by contract — and, since
    /// the unit is the local one, a logged no-op when the profile's guider host is another machine (#1192).</summary>
    public async Task<OperationAcceptedDto> RestartGuiderAsync(string? idempotencyKey, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        // #1192: the unit is the LOCAL openastro-guider. When the profile's guider lives on another
        // machine, restarting it here would kick a daemon nobody is using — log and do nothing.
        var settings = _profileService.ActiveProfile.GuiderSettings;
        var (host, port) = (settings.PHD2ServerHost ?? string.Empty, settings.PHD2ServerPort);
        if (!await IsLocalGuiderHostDecision(host, ct).ConfigureAwait(false)) {
            LogRemoteHostNoLocalRestart(host, port);
        } else {
            _supervisor.RequestRestart();
        }
        return Accepted("guider.restart", idempotencyKey);
    }
}
