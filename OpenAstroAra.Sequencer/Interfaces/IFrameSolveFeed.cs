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
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Interfaces {

    /// <summary>One saved LIGHT frame (#1195). <see cref="Sequence"/> rises with every save so a consumer
    /// can tell a new frame from the one it already looked at.</summary>
    public sealed record SavedLight(Guid FrameId, long Sequence, DateTimeOffset SavedUtc);

    /// <summary>
    /// #1195 — the seam the <c>CenterAfterDriftTrigger</c> measures pointing drift through: the daemon
    /// records every saved LIGHT in <see cref="LatestLight"/>, and <see cref="SolveFrameAsync"/>
    /// plate-solves one of those frames from disk (no extra capture — the light itself is the
    /// drift measurement, as in NINA's platesolving image follower). A frame that does not solve
    /// (no solver installed, clouds, too few stars) yields null; the trigger stays quiet.
    /// </summary>
    public interface IFrameSolveFeed {

        /// <summary>The most recently saved LIGHT frame, or null before the first one.</summary>
        SavedLight? LatestLight { get; }

        /// <summary>Plate-solve a saved frame; the solved centre (J2000) or null when it did not solve.</summary>
        Task<Coordinates?> SolveFrameAsync(Guid frameId, CancellationToken ct);
    }
}
