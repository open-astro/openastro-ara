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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Telescope {

    /// <summary>What the probe is asked to measure: the ladder (already capped), the longest exposure
    /// worth measuring, the spec-sheet prior (null for an unknown mount) and the target's name for
    /// the live readout.</summary>
    public sealed record TrackingProbeRequest(
        IReadOnlyList<double> RungsSeconds,
        double MaxExposureSeconds,
        double? PriorSeconds,
        string? TargetName);

    /// <summary>The probe's answer. <see cref="CeilingSeconds"/> null = fewer than two usable rungs, so
    /// the prior (if any) stands; <see cref="Summary"/> is the one-line explanation for the run log.</summary>
    public sealed record TrackingProbeResult(double? CeilingSeconds, int UsableRungs, string Summary);

    /// <summary>
    /// #1264 — the seam the <see cref="TrackingProbe"/> instruction measures through. The daemon's
    /// implementation takes each rung as an analysis frame, runs the §59.5 star analysis (median
    /// eccentricity and FWHM), applies the pass rule, stores <c>tracking_calibration</c> on the profile
    /// and publishes the live events. The sequencer project only knows this interface.
    /// </summary>
    public interface ITrackingProbeExecutor {

        Task<TrackingProbeResult> ProbeAsync(TrackingProbeRequest request, IProgress<ApplicationStatus> progress, CancellationToken token);
    }
}
