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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

public sealed partial class PolarAlignService {

    /// <summary>
    /// §77.3 seam: one guide-camera capture → fetch → plate solve, reusing this
    /// service's §45 machinery (capture serialization, stale-event debt bookkeeping,
    /// daemon fetch, solver wiring) without duplicating it. Takes the PA-session
    /// lease for the duration of the single capture and releases it best-effort.
    /// Refused while a polar-align run is active — the §45 routine owns the lease
    /// and the frame stream then. Uses a generation the PA state machine never
    /// matches, so PA progress counters are not polluted.
    /// </summary>
    internal async Task<PolarAlignSolveOutcome> CaptureSolveGuideFrameAsync(
            (double RaDeg, double DecDeg)? hint, CancellationToken ct) {
        lock (_gate) {
            if (_state is not ("idle" or "done" or "failed" or "stopped")) {
                throw new InvalidOperationException(
                    $"polar alignment is {_state} — it owns the guide camera; stop it before planetary pointing");
            }
        }
        var guiderClient = _guider.RequireConnectedGuider();
        var settings = _profileStore.GetPolarAlignSettings();
        var workDir = Path.Combine(Path.GetTempPath(), "ara-pointing");
        Directory.CreateDirectory(workDir);
        var frameId = $"point_{DateTime.UtcNow:HHmmssfff}";
        await guiderClient.SetPaSessionAsync(active: true, timeoutS: LeaseTimeoutSeconds, ct).ConfigureAwait(false);
        try {
            return await CaptureAndSolveAsync(
                guiderClient, workDir, frameId, hint, settings, gen: -1, ct).ConfigureAwait(false);
        } finally {
            try {
                await guiderClient.SetPaSessionAsync(active: false, timeoutS: 0, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) when (ex is not OutOfMemoryException) {
                // Lease release is best-effort — it expires daemon-side anyway.
                LogCaptureFailed(frameId, $"releasing the PA-session lease failed: {ex.Message}");
            }
            try {
                File.Delete(Path.Combine(workDir, frameId + ".fits"));
            } catch (IOException) {
                // §45.5 spirit: pointing frames never accumulate; best-effort.
            }
        }
    }
}
