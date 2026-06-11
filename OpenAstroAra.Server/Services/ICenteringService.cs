#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.PlateSolving;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §28 centering — slew-then-plate-solve-then-sync until the mount is on target. Wraps the ported
/// <see cref="OpenAstroAra.PlateSolving.Interfaces.ICenteringSolver"/> (capture → solve → sync → re-slew loop)
/// behind a single call, building the capture sequence + solve parameters from the active profile. This is
/// the §28 "slew and center" capability and what the §58.4 meridian-flip recenter step will call.
///
/// Mount-gated to live-validate (the loop syncs/re-slews the telescope); the orchestration is unit-testable
/// with mocked equipment + a mocked solver. Needs a solver backend (ASTAP) installed + configured.
/// </summary>
public interface ICenteringService {

    /// <summary>
    /// Centre the mount on <paramref name="target"/>: capture, plate-solve, sync, and re-slew until the centre
    /// is within the profile's centering threshold (or attempts are exhausted). Returns the final solve result
    /// (its <see cref="PlateSolveResult.Success"/> reflects whether centering converged).
    /// <paramref name="solveProgress"/> surfaces per-solve sub-progress (the inner loop) and
    /// <paramref name="progress"/> the overall status; both optional.
    /// </summary>
    Task<PlateSolveResult> CenterOnTarget(Coordinates target, IProgress<PlateSolveProgress>? solveProgress,
        IProgress<ApplicationStatus>? progress, CancellationToken token);
}
