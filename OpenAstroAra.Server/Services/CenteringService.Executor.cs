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
using OpenAstroAra.Sequencer.SequenceItem.Platesolving;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §28/§38 — the service also serves the sequencer's <see cref="ICenteringExecutor"/> seam
/// (one singleton for both surfaces, per the §8.1 mediator-unification pattern), so a
/// sequence's <c>CenterAndRotate</c> drives the SAME capture → plate-solve → sync → re-slew
/// loop the REST surface and the §58.4 meridian-flip recenter use — including its internal
/// serialization (concurrent centre requests queue rather than fight over the mount).
/// </summary>
public sealed partial class CenteringService : ICenteringExecutor {

    /// <inheritdoc/>
    public async Task<bool> CenterAsync(Coordinates target, IProgress<ApplicationStatus> progress, CancellationToken token) {
        var result = await CenterOnTarget(target, solveProgress: null, progress, token);
        return result.Success;
    }
}
