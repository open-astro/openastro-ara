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
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.PlateSolving;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §18.I — headless image plate-solving. The solver backends (ASTAP, Platesolve2/3) and the solve algorithms
/// were ported in the §0.5 conversion but never wired into a callable service; <see cref="PlateSolveService"/>
/// is that wiring. The image-in → solution-out core is the foundation the plate-solve centering loop
/// (capture → solve → sync → re-slew) and the §58.4 meridian-flip recenter build on. A solver backend (e.g.
/// ASTAP) must be installed + its path set in the profile for a live solve to succeed.
/// </summary>
public interface IPlateSolveService {

    /// <summary>
    /// Solve <paramref name="image"/> astrometrically. <paramref name="approxCoordinates"/>, when supplied,
    /// seeds a near (non-blind) solve around the telescope's reported position; null requests a blind solve.
    /// </summary>
    Task<PlateSolveResult> SolveImage(IImageData image, Coordinates? approxCoordinates, IProgress<ApplicationStatus>? progress, CancellationToken token);
}
