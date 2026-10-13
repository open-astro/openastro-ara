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
        string? TargetName,
        string? PriorSource = null,
        // #1264 Imaging → Unguided — a rung is "trailed" above this median eccentricity (0.5 shipped).
        double EccentricityTolerance = 0.5);

    /// <summary>The probe's answer. <see cref="CeilingSeconds"/> null = inconclusive (fewer than two usable
    /// rungs and none of them failed), so the prior (if any) stands; a first rung that already trails IS a
    /// ceiling. <see cref="Summary"/> is the one-line explanation for the run log.</summary>
    public sealed record TrackingProbeResult(double? CeilingSeconds, int UsableRungs, string Summary);

    /// <summary>
    /// #1264 — the seam the <see cref="TrackingProbe"/> instruction measures through. The daemon's
    /// implementation takes each rung as an analysis frame, runs the §59.5 star analysis (median
    /// eccentricity and FWHM), applies the pass rule, stores <c>tracking_calibration</c> on the profile
    /// and publishes the live events. The sequencer project only knows this interface.
    /// </summary>
    public interface ITrackingProbeExecutor {

        Task<TrackingProbeResult> ProbeAsync(TrackingProbeRequest request, IProgress<ApplicationStatus> progress, CancellationToken token);

        /// <summary>The prior the ladder is capped at and an inconclusive probe falls back to: the body's
        /// own value when set (the spec sheet, once AlpacaBridge #815 supplies it), else the profile's
        /// stored <c>tracking_calibration</c> ceiling — last night's measurement is this night's prior.
        /// Null for an unknown mount that was never probed. Default: the body's value.</summary>
        Task<double?> ResolvePriorAsync(double? bodyPriorSeconds, CancellationToken token) => Task.FromResult(bodyPriorSeconds);

        /// <summary>#1264 stage 1 — the prior AND where it came from, for the notifications ("…than the
        /// 45 s prior from the last probe"): the body's own value, the stored calibration, or the
        /// mount's spec sheet. Default wraps <see cref="ResolvePriorAsync"/> with the two sources the
        /// body-or-store rule can produce.</summary>
        async Task<(double? Seconds, string? Source)> ResolvePriorWithSourceAsync(double? bodyPriorSeconds, CancellationToken token) {
            var prior = await ResolvePriorAsync(bodyPriorSeconds, token).ConfigureAwait(false);
            return (prior, prior is null ? null : bodyPriorSeconds is not null ? "the sequence's own prior" : "the last probe");
        }
    }
}
