#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Autofocus {

    /// <summary>
    /// §59 — the sequencer-side seam for the live autofocus sweep, following the
    /// <c>IMeridianFlipExecutor</c>/<c>ICenteringExecutor</c> pattern: the
    /// instruction (<see cref="RunAutofocus"/>) and the meridian flip's
    /// re-focus step own the *decision to focus*, this seam owns the
    /// *orchestration* (the §59.8 V-curve sweep — step the focuser through the
    /// configured positions, HFR-probe each, fit the curve, move to the best —
    /// implemented server-side, which this project cannot reference).
    ///
    /// Returning false (or throwing) is a FAILURE: continuing a sequence out of
    /// focus quietly ruins every subsequent frame, so callers fail their step
    /// loudly rather than proceed.
    /// </summary>
    public interface IAutofocusExecutor {

        /// <summary>
        /// Run one full autofocus sweep with the active profile's §37.11
        /// settings. True when the sweep converged and the focuser sits at the
        /// fitted best position; false when it failed (the implementation
        /// restores the starting position per the profile's policy).
        /// </summary>
        Task<bool> RunAutofocusAsync(IProgress<ApplicationStatus> progress, CancellationToken token);
    }
}
