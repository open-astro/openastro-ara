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
    /// No-op on hosts without systemd (dev machines) — the supervisor swallows it by contract.</summary>
    public Task<OperationAcceptedDto> RestartGuiderAsync(string? idempotencyKey, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        _supervisor.RequestRestart();
        return Task.FromResult(Accepted("guider.restart", idempotencyKey));
    }
}
