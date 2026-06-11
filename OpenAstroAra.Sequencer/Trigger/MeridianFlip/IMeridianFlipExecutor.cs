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

namespace OpenAstroAra.Sequencer.Trigger.MeridianFlip {

    /// <summary>
    /// §58 — runs the actual meridian-flip workflow once <see cref="MeridianFlipTrigger"/> has decided a flip
    /// is due. This is the headless replacement for NINA's WPF <c>IMeridianFlipVMFactory</c> +
    /// <c>MeridianFlipVM</c> (deleted in the §0.5 demolition): the trigger owns the *decision* (when to flip),
    /// this seam owns the *orchestration* (the §58.4 recovery sequence — pause imaging → wait out
    /// <c>pause_after</c> → flip slew → plate-solve re-center → optional re-focus → restart guiding →
    /// §58.5 side-of-pier verification).
    ///
    /// Splitting it behind an interface keeps the trigger's timing logic unit-testable without any of the
    /// equipment mediators, and lets the orchestration land as its own sub-PR.
    /// </summary>
    public interface IMeridianFlipExecutor {

        /// <summary>
        /// Perform the flip for <paramref name="targetCoordinates"/>. <paramref name="timeToFlip"/> is how long
        /// to wait before the flip slew (0 when the meridian has already been passed by <c>pause_after</c>).
        /// Returns true when the flip + recovery completed successfully.
        /// </summary>
        Task<bool> MeridianFlip(Coordinates targetCoordinates, TimeSpan timeToFlip, IProgress<ApplicationStatus> progress, CancellationToken token);
    }
}
