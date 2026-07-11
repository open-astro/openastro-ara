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
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §57.4 — the panic-stop contract behind <c>POST /api/v1/telescope/abort</c>, extracted so the
/// ordering is pinned by tests rather than code review (#836 r5): the device abort is attempted
/// first, and the sequence pause runs UNCONDITIONALLY — even when the device abort throws
/// (driver error, disconnect), the user hit the panic button and the run must not keep firing
/// exposures at a mount that may still be moving.
/// </summary>
public static class MountStopHandler {

    /// <remarks>The pause delegate is expected to be non-throwing (gate-arm semantics — see
    /// <c>SequencerService.PauseActiveRunsAsync</c>); if that ever changes, an exception from the
    /// finally would MASK a device-abort failure rather than wrap it (#836 r5 note).</remarks>
    public static async Task ExecuteAsync(Func<Task> abortSlew, Func<Task> pauseActiveRuns) {
        ArgumentNullException.ThrowIfNull(abortSlew);
        ArgumentNullException.ThrowIfNull(pauseActiveRuns);
        try {
            await abortSlew().ConfigureAwait(false);
        } finally {
            await pauseActiveRuns().ConfigureAwait(false);
        }
    }
}
