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
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Platesolving {

    /// <summary>
    /// §28 — the sequencer-side seam for plate-solve centering, following the
    /// <c>IMeridianFlipExecutor</c> pattern: the instruction
    /// (<see cref="CenterAndRotate"/>) owns the *decision to centre* at its point
    /// in the plan, this seam owns the *orchestration* (the §28 capture →
    /// plate-solve → sync → re-slew loop, implemented server-side by the
    /// CenteringService, which is not referenceable from this project).
    ///
    /// Splitting it behind an interface keeps the instruction unit-testable
    /// without a solver/mount, and lets a null executor (prototype-only
    /// construction) fail loudly at execution instead of silently skipping the
    /// centre.
    /// </summary>
    public interface ICenteringExecutor {

        /// <summary>
        /// Centre the mount on <paramref name="target"/> via the capture →
        /// plate-solve → sync → re-slew loop. Returns true when centering
        /// converged within the profile's threshold; false when attempts were
        /// exhausted (the caller fails the instruction — an un-centred target
        /// would quietly ruin every subsequent frame). Implementations may also
        /// throw for unrecoverable failures (no solver installed, no camera).
        /// </summary>
        Task<bool> CenterAsync(Coordinates target, IProgress<ApplicationStatus> progress, CancellationToken token);
    }
}
