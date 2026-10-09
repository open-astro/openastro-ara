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
using System.Collections.Generic;

namespace OpenAstroAra.Server.Services;

/// <summary>One polar-alignment residual fit (#1311). <see cref="PaErrorMinArcmin"/> is a lower
/// bound on the polar axis error: Dec drift at one hour angle sees only one component of it.</summary>
internal readonly record struct PaResidualFit(
    double DriftArcsecPerMin,
    double PaErrorMinArcmin,
    double UncertaintyArcmin,
    int Frames,
    double SampleSeconds);

/// <summary>
/// #1311 — the polar alignment error left after Align, measured from the guiding Ara already does.
///
/// The guider holds the star still, so the star's Dec offset alone shows almost no drift: the drift
/// went into the Dec corrections. Adding every correction already applied back onto the offset
/// (pulse length × the calibration's Dec rate) rebuilds where the star would have gone with the
/// guide output off, which is what PHD2's Guiding Assistant measures by switching guiding off.
///
/// Geometry: a polar axis that sits ε from the true pole turns every star about the wrong pole,
/// so its Dec changes at dδ/dt = ω·ε·sin θ, with ω the sidereal rate and θ set by the hour angle
/// and the direction of the error. That rate does not depend on the star's Dec, so the 1 / cos δ
/// of Barrett's formula (which PHD2's Guiding Assistant copies) is not applied; it overstates the
/// error away from the equator. One stretch at one hour angle sees only the sin θ share of ε, so
/// |dδ/dt| / ω is a lower bound on the total error.
///
/// Samples are grouped in segments: a dither, a settle, a lost star or a pause closes one, and each
/// segment gets its own intercept, so a moved lock position never reads as drift. The slope is the
/// pooled within-segment least-squares fit. Pure; the guider wiring is GuiderService.PaResidual.cs.
/// </summary>
internal sealed class PaResidualEstimator {

    /// <summary>Arc-minutes of polar axis error per arc-second-per-minute of Dec drift, ≈ 3.809.
    /// ε = v / ω in radians; with v in ″/min and ε in ′ the unit factors (60 s/min, and 60 between
    /// ″/rad and ′/rad) leave 1 / (3600 × ω), ω in rad/s. PHD2 uses 3.8197 (a solar day).</summary>
    internal const double ArcminPerArcsecPerMin = 1.0 / (3600.0 * SiderealRateRadPerSec);

    // 2π per sidereal day (86164.0905 s).
    private const double SiderealRateRadPerSec = 7.2921159e-5;

    // A segment shorter than this many frames carries no usable slope.
    private const int MinSegmentFrames = 3;

    private readonly List<List<Sample>> _segments = [];
    private List<Sample>? _open;
    // Running totals over the CLOSED usable segments, so the per-frame progress check is O(1).
    private double _closedSeconds;
    private int _closedFrames;

    private readonly record struct Sample(double TimeSec, double DecRawPx, double DecDurationMs);

    /// <summary>Total guided time across the usable segments, in seconds.</summary>
    public double SampleSeconds => _closedSeconds + (IsUsable(_open) ? _open![^1].TimeSec - _open[0].TimeSec : 0);

    /// <summary>Frames in the usable segments.</summary>
    public int Frames => _closedFrames + (IsUsable(_open) ? _open!.Count : 0);

    /// <summary>One guide frame: the star's Dec offset (px) and the Dec pulse issued on it, signed
    /// as <c>IGuideStep.DECDuration</c> (positive = North). A non-finite offset (lost star) closes
    /// the segment instead.</summary>
    public void Add(double timeSec, double decRawPx, double decDurationMs) {
        if (!double.IsFinite(timeSec) || !double.IsFinite(decRawPx)) {
            Break();
            return;
        }
        if (_open is { Count: > 0 } && timeSec <= _open[^1].TimeSec) {
            return; // out-of-order or duplicate frame
        }
        if (_open is null) {
            _open = [];
            _segments.Add(_open);
        }
        _open.Add(new Sample(timeSec, decRawPx, double.IsFinite(decDurationMs) ? decDurationMs : 0));
    }

    /// <summary>Closes the open segment: the next frame starts a new one with its own intercept.</summary>
    public void Break() {
        if (IsUsable(_open)) {
            _closedSeconds += _open![^1].TimeSec - _open[0].TimeSec;
            _closedFrames += _open.Count;
        }
        _open = null;
    }

    public void Clear() {
        _segments.Clear();
        _open = null;
        _closedSeconds = 0;
        _closedFrames = 0;
    }

    /// <summary>
    /// Fits the uncorrected Dec drift. <paramref name="decRatePxPerSec"/> is the calibration's Dec
    /// guide rate (PHD2 <c>get_calibration_data</c> yRate) and <paramref name="pixelScaleArcsec"/>
    /// the guide camera's scale. Null without a rate or scale, or when the usable segments leave
    /// fewer than 3 degrees of freedom (frames − segments − 1; one 5-frame segment is enough).
    /// </summary>
    public PaResidualFit? Fit(double decRatePxPerSec, double pixelScaleArcsec) {
        if (!(decRatePxPerSec > 0) || !(pixelScaleArcsec > 0)) {
            return null;
        }
        var ratePxPerMs = decRatePxPerSec / 1000.0;

        // Uncorrected position u_k = offset_k − Σ_{j<k} pulse_j × rate: a North pulse (positive)
        // raises the next offset by rate × length, so taking it off leaves the free drift.
        var rebuilt = new List<(double[] T, double[] U)>();
        int n = 0;
        foreach (var segment in Usable()) {
            var t = new double[segment.Count];
            var u = new double[segment.Count];
            double applied = 0;
            for (var k = 0; k < segment.Count; k++) {
                t[k] = segment[k].TimeSec;
                u[k] = segment[k].DecRawPx - applied;
                applied += segment[k].DecDurationMs * ratePxPerMs;
            }
            rebuilt.Add((t, u));
            n += segment.Count;
        }
        var dof = n - rebuilt.Count - 1;
        if (dof < 3) {
            return null;
        }

        double sxx = 0, sxy = 0;
        foreach (var (t, u) in rebuilt) {
            var (tMean, uMean) = (Mean(t), Mean(u));
            for (var k = 0; k < t.Length; k++) {
                sxx += (t[k] - tMean) * (t[k] - tMean);
                sxy += (t[k] - tMean) * (u[k] - uMean);
            }
        }
        if (!(sxx > 0)) {
            return null;
        }
        var slope = sxy / sxx; // px / s

        // Slope uncertainty from the residuals. Guide residuals are correlated frame to frame
        // (seeing, a slightly wrong rate accumulating), which white-noise least squares would
        // under-state, so the variance is widened by the lag-1 autocorrelation: (1 + ρ) / (1 − ρ).
        double sse = 0, lag = 0;
        foreach (var (t, u) in rebuilt) {
            var (tMean, uMean) = (Mean(t), Mean(u));
            double? previous = null;
            for (var k = 0; k < t.Length; k++) {
                var e = u[k] - uMean - slope * (t[k] - tMean);
                sse += e * e;
                if (previous is double p) {
                    lag += p * e;
                }
                previous = e;
            }
        }
        var rho = sse > 0 ? Math.Clamp(lag / sse, 0.0, 0.95) : 0.0;
        var slopeSe = Math.Sqrt(sse / dof / sxx * (1 + rho) / (1 - rho));

        var toArcsecPerMin = 60.0 * pixelScaleArcsec;
        var drift = slope * toArcsecPerMin;
        return new PaResidualFit(
            DriftArcsecPerMin: drift,
            PaErrorMinArcmin: Math.Abs(drift) * ArcminPerArcsecPerMin,
            UncertaintyArcmin: slopeSe * toArcsecPerMin * ArcminPerArcsecPerMin,
            Frames: n,
            SampleSeconds: SampleSeconds);
    }

    private static bool IsUsable(List<Sample>? segment) => segment is { Count: >= MinSegmentFrames };

    private IEnumerable<List<Sample>> Usable() {
        foreach (var segment in _segments) {
            if (IsUsable(segment)) {
                yield return segment;
            }
        }
    }

    private static double Mean(double[] values) {
        double sum = 0;
        foreach (var v in values) {
            sum += v;
        }
        return sum / values.Length;
    }
}
