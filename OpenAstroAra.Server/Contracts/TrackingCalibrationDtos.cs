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

namespace OpenAstroAra.Server.Contracts;

/// <summary>
/// #1264 — the unguided tracking ceiling measured by the sequencer's <c>TrackingProbe</c> at run start
/// (profile JSON <c>tracking_calibration</c>). Daemon-owned derived data like <see cref="FocusCalibrationDto"/>:
/// written by the probe, never by the client; null on the profile = never measured. The next night's prior is
/// <see cref="CeilingSeconds"/> when the run body carries none of its own (<c>TrackingProbeService.ResolvePriorAsync</c>);
/// a body prior (the mount's spec sheet) wins over it.
/// </summary>
/// <param name="CeilingSeconds">The longest unguided exposure the probe passed (interpolated one step toward the
/// first failing rung when both exist).</param>
/// <param name="MeasuredUtc">When the probe ran. The record is written the moment the ladder finishes, so a run
/// cancelled right after its probe still leaves tonight's measurement as the next night's prior (intended: the
/// mount's behaviour was measured regardless of what the run did next).</param>
/// <param name="PolarAlignErrorArcmin">The polar-alignment routine's final error in force that night (the newest
/// measured §45 run within 24 h), null when none.</param>
/// <param name="PixelScaleArcsec">The optics' pixel scale at the time (arcsec/px, unbinned), null when the profile
/// has no optics.</param>
/// <param name="SeeingFwhmArcsec">The seeing the tolerance was based on: the first rung's measured FWHM converted
/// to arcseconds when the pixel scale is known, else the site's typical seeing.</param>
/// <param name="PriorSeconds">The spec-sheet (or previous night's) prior the probe started from, null for an unknown mount.</param>
/// <param name="Source">"measured" (the ladder decided) or "prior" (fewer than two usable rungs and none failed, so the
/// prior stood and is recorded as what the run used; with no prior nothing is stored).</param>
/// <param name="Rungs">What each rung measured, in ladder order.</param>
public sealed record TrackingCalibrationDto(
    double CeilingSeconds,
    DateTimeOffset MeasuredUtc,
    double? PolarAlignErrorArcmin,
    double? PixelScaleArcsec,
    double? SeeingFwhmArcsec,
    double? PriorSeconds,
    string Source,
    System.Collections.Generic.IReadOnlyList<TrackingProbeRungDto> Rungs);

/// <summary>One rung of the probe's ladder.</summary>
/// <param name="ExposureSeconds">The rung's exposure.</param>
/// <param name="Eccentricity">Median star eccentricity (0 round … 1 line); NaN-free: null when unusable.</param>
/// <param name="FwhmPx">Median FWHM in pixels; null when unusable.</param>
/// <param name="Stars">Stars detected.</param>
/// <param name="Usable">Enough stars to judge.</param>
/// <param name="Passed">Within tolerance (eccentricity ≤ 0.5 and FWHM ≤ 1.2 × the first usable rung's).</param>
public sealed record TrackingProbeRungDto(
    double ExposureSeconds,
    double? Eccentricity,
    double? FwhmPx,
    int Stars,
    bool Usable,
    bool Passed);
