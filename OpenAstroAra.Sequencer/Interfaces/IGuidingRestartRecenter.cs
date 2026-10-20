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

namespace OpenAstroAra.Sequencer.Interfaces {

    /// <summary>
    /// §42.2 — a dither that has to restart guiding after a LONG gap re-centers the target first:
    /// with guiding off the mount drifted on tracking alone, and restarting on the guider's own
    /// star pick would lock that drifted framing in. The server supplies the plate-solve re-center;
    /// a short gap keeps the plain restart.
    /// </summary>
    public interface IGuidingRestartRecenter {

        /// <summary>A guiding gap at or past this is "long": re-center before restarting.</summary>
        TimeSpan LongGap { get; }

        /// <summary>Plate solve and re-center on <paramref name="target"/>. False when it could not
        /// (no solver, the target is below the horizon limit, the solve failed): the caller still
        /// restarts guiding and carries on.</summary>
        Task<bool> RecenterAsync(Coordinates target, IProgress<ApplicationStatus>? progress, CancellationToken token);
    }
}
