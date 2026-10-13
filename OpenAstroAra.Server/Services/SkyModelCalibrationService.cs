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
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1140 P2 — the Integration Budget's calibration store. The planner's sky model predicts a sky flux
/// from spec-sheet numbers (aperture, pixel scale, QE, passband, SQM); this service fits the one scalar
/// <c>k</c> per filter kind (and binning) that scales that prediction to what the rig's own frames
/// measured, from the library's 5th-percentile backgrounds (stage 1, #1341):
/// <list type="bullet">
/// <item>per (kind, bin): regress <c>background = pedestal + rate × exposure</c> over the recent light
/// frames. With two or more exposure lengths the intercept IS the camera's pedestal, so no bias frame
/// is needed (the 2026-07-19 decision: typical users rarely shoot calibration frames).</item>
/// <item>with a single exposure length, a bias frame's background pins the pedestal; without one the
/// kind is reported unfittable, never guessed.</item>
/// <item><c>k = measured rate (e⁻/s/px) / predicted rate</c>; the residual scatter is reported, and
/// the Phase-V gate (|k| in 0.4–1.5, scatter under 30 %, three or more frames) decides <c>Passed</c>.</item>
/// </list>
/// The fit is pure (<see cref="Fit"/>); <see cref="RecalibrateAsync"/> reads the library and stores
/// the record on the profile.
/// </summary>
public sealed class SkyModelCalibrationService {
    public const double GateMinK = 0.4;
    public const double GateMaxK = 1.5;
    public const double GateMaxScatterPct = 30.0;
    public const int GateMinFrames = 3;

    /// <summary>How far back "recent frames" reaches, and how many at most.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(90);
    public const int MaxFrames = 4000;

    // The planner's one sky model (client optimal_sub.dart): mag-0 photon flux per cm² per nm, the
    // Bortle→mag ladder, Tier-0 QE. A change here must reach the client too.
    private const double PhotonFluxMag0PerCm2PerNm = 1.0e4;
    private const double DefaultQuantumEfficiency = 0.8;

    private readonly IProfileStore _profiles;
    private readonly IFrameRepository? _frames;

    public SkyModelCalibrationService(IProfileStore profiles, IFrameRepository? frames = null) {
        _profiles = profiles;
        _frames = frames;
    }

    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Read the library's recent frames, fit, store and return the record.</summary>
    public async Task<SkyModelCalibrationDto> RecalibrateAsync(CancellationToken ct) {
        var now = UtcNow();
        var frames = new List<FrameListItemDto>();
        if (_frames is not null) {
            string? cursor = null;
            var since = now - RecentWindow;
            do {
                var page = await _frames.ListAsync(200, cursor, null, null, ct).ConfigureAwait(false);
                frames.AddRange(page.Items.Where(f => f.CapturedUtc >= since));
                cursor = page.HasMore ? page.NextCursor : null;
            } while (cursor is not null && frames.Count < MaxFrames);
        }
        var record = Fit(frames, _profiles.GetSiteSettings(), _profiles.GetOpticsSettings(), _profiles.GetCameraElectronics(),
            _profiles.GetFilterSet(), now);
        _profiles.PutSkyModelCalibration(record);
        return record;
    }

    /// <summary>The pure fit. Internal static so the maths is unit-tested without a catalog.</summary>
    public static SkyModelCalibrationDto Fit(IReadOnlyList<FrameListItemDto> frames, SiteSettingsDto site, OpticsSettingsDto optics,
            CameraElectronicsDto camera, FilterSetDto filterSet, DateTimeOffset now) {
        var usable = frames.Where(f => f.BackgroundAdu is double bg && !double.IsNaN(bg) && f.ExposureSeconds > 0).ToList();
        var lights = usable.Where(f => f.FrameType == FrameType.Light).ToList();
        var biasPedestal = MedianOrNull(usable.Where(f => f.FrameType == FrameType.Bias).Select(f => f.BackgroundAdu!.Value));

        var results = new List<SkyModelFilterCalibrationDto>();
        foreach (var group in lights.GroupBy(f => (Kind: KindOf(f.FilterName, filterSet), Bin: f.Bin ?? 1))
                     .Where(g => g.Key.Kind is not null).OrderBy(g => KindName(g.Key.Kind!.Value), StringComparer.Ordinal).ThenBy(g => g.Key.Bin)) {
            results.Add(FitKind(group.Key.Kind!.Value, group.Key.Bin, group.ToList(), biasPedestal, site, optics, camera, filterSet));
        }
        return new SkyModelCalibrationDto(now, usable.Count, results);
    }

    private static SkyModelFilterCalibrationDto FitKind(FilterKind kind, int bin, List<FrameListItemDto> frames, double? biasPedestal,
            SiteSettingsDto site, OpticsSettingsDto optics, CameraElectronicsDto camera, FilterSetDto filterSet) {
        var kindName = KindName(kind);
        var n = frames.Count;
        if (n < GateMinFrames) {
            return Unfit(kindName, bin, n, $"Only {n} frame{(n == 1 ? "" : "s")}; the gate needs {GateMinFrames}.");
        }
        if (camera.ElectronsPerAdu <= 0) {
            return Unfit(kindName, bin, n, "The camera's e⁻/ADU is not on the profile (Imaging → Camera electronics).");
        }
        if (optics.FocalLengthMm <= 0 || optics.PixelSizeUm <= 0 || optics.ApertureMm <= 0) {
            return Unfit(kindName, bin, n, "The optics (focal length, aperture, pixel size) are not on the profile.");
        }

        // Pedestal + rate: a regression when the exposures vary, else the bias frames pin the pedestal.
        var t = frames.Select(f => f.ExposureSeconds).ToArray();
        var bg = frames.Select(f => f.BackgroundAdu!.Value).ToArray();
        double pedestal, rateAduPerSec, scatterPct;
        var distinctExposures = t.Distinct().Count();
        if (distinctExposures >= 2) {
            (pedestal, rateAduPerSec) = LeastSquares(t, bg);
            var fitted = t.Select(x => pedestal + rateAduPerSec * x).ToArray();
            var residualSd = StdDev(bg.Zip(fitted, (m, p) => m - p));
            var meanSky = fitted.Zip(t, (p, x) => rateAduPerSec * x).Average();
            scatterPct = meanSky > 0 ? residualSd / meanSky * 100.0 : double.PositiveInfinity;
        } else if (biasPedestal is double pinned) {
            pedestal = pinned;
            var rates = bg.Zip(t, (m, x) => (m - pinned) / x).ToArray();
            rateAduPerSec = Median(rates);
            scatterPct = rateAduPerSec > 0 ? StdDev(rates) / rateAduPerSec * 100.0 : double.PositiveInfinity;
        } else {
            return Unfit(kindName, bin, n,
                $"All {n} frames share one exposure length ({t[0].ToString("0.#", CultureInfo.InvariantCulture)} s) and the library has no bias frame to pin the pedestal; vary the exposure or shoot a bias.");
        }
        if (rateAduPerSec <= 0) {
            return Unfit(kindName, bin, n, "The fitted sky rate is not positive (the background does not grow with exposure).");
        }

        var measured = rateAduPerSec * camera.ElectronsPerAdu;
        var predicted = PredictedSkyRateEPerSecPerPx(kind, bin, site, optics, camera, filterSet);
        var k = measured / predicted;
        var passed = k >= GateMinK && k <= GateMaxK && scatterPct < GateMaxScatterPct;
        string? note = passed ? null
            : k < GateMinK || k > GateMaxK ? $"k outside the {GateMinK:0.0}–{GateMaxK:0.0} band: the model is missing physics for this rig (moon, haze, a passband far from the default)."
            : $"Scatter {scatterPct:0} % over the window exceeds {GateMaxScatterPct:0} %: nights too different to fit one figure.";
        return new SkyModelFilterCalibrationDto(kindName, bin, Round(k, 3), Round(scatterPct, 1), Round(pedestal, 1),
            Round(measured, 4), Round(predicted, 4), n, passed, note);
    }

    private static SkyModelFilterCalibrationDto Unfit(string kind, int bin, int n, string note) =>
        new(kind, bin, null, null, null, null, null, n, false, note);

    /// <summary>The planner's modelled sky flux in e⁻/s/px (client <c>skyFluxEPerSecPerPx</c>, one sky model).</summary>
    internal static double PredictedSkyRateEPerSecPerPx(FilterKind kind, int bin, SiteSettingsDto site, OpticsSettingsDto optics,
            CameraElectronicsDto camera, FilterSetDto filterSet) {
        var skyMag = site.SqmMagPerArcsec2 is >= 16 and <= 22.5 ? site.SqmMagPerArcsec2 : 22.0 - (Math.Clamp(site.BortleClass, 1, 9) - 1) * 0.5;
        var reducer = optics.ReducerFactor > 0 ? optics.ReducerFactor : 1.0;
        var scale = 206.265 * optics.PixelSizeUm * Math.Max(1, bin) / (optics.FocalLengthMm * reducer);
        var apertureCm2 = Math.PI * Math.Pow(optics.ApertureMm / 20.0, 2);
        var qe = camera.QuantumEfficiencyPeak > 0 ? camera.QuantumEfficiencyPeak : DefaultQuantumEfficiency;
        return PhotonFluxMag0PerCm2PerNm * Math.Pow(10.0, -0.4 * skyMag) * BandwidthNm(kind, filterSet) * apertureCm2 * scale * scale * qe;
    }

    /// <summary>The declared filter's passband when set, else the kind's default (the client's ladder).</summary>
    internal static double BandwidthNm(FilterKind kind, FilterSetDto filterSet) {
        var declared = filterSet.Filters.FirstOrDefault(f => f.Kind == kind && f.BandwidthNm > 0);
        if (declared is not null) {
            return declared.BandwidthNm;
        }
        return kind switch {
            FilterKind.L or FilterKind.Osc => 100,
            FilterKind.R or FilterKind.G or FilterKind.B => 80,
            FilterKind.Ha or FilterKind.Oiii or FilterKind.Sii or FilterKind.Duo => 7,
            FilterKind.Tri => 8,
            _ => 100,
        };
    }

    /// <summary>A frame's filter kind from its FILTER name through the profile's declared set
    /// (case-insensitive). No name: OSC when the set declares one or is empty (a DSLR / colour camera
    /// with no wheel), else unknown. Unknown names are not guessed.</summary>
    internal static FilterKind? KindOf(string? filterName, FilterSetDto filterSet) {
        if (string.IsNullOrWhiteSpace(filterName)) {
            return filterSet.Filters.Count == 0 || filterSet.Filters.Any(f => f.Kind == FilterKind.Osc) ? FilterKind.Osc : null;
        }
        var match = filterSet.Filters.FirstOrDefault(f => string.Equals(f.Name?.Trim(), filterName.Trim(), StringComparison.OrdinalIgnoreCase));
        return match?.Kind;
    }

    internal static string KindName(FilterKind kind) => kind.ToString().ToLowerInvariant();

    private static (double Intercept, double Slope) LeastSquares(double[] x, double[] y) {
        var mx = x.Average();
        var my = y.Average();
        double sxx = 0, sxy = 0;
        for (var i = 0; i < x.Length; i++) {
            sxx += (x[i] - mx) * (x[i] - mx);
            sxy += (x[i] - mx) * (y[i] - my);
        }
        var slope = sxy / sxx;
        return (my - slope * mx, slope);
    }

    private static double StdDev(IEnumerable<double> values) {
        var list = values.ToList();
        if (list.Count < 2) {
            return 0;
        }
        var mean = list.Average();
        return Math.Sqrt(list.Sum(v => (v - mean) * (v - mean)) / (list.Count - 1));
    }

    private static double Median(double[] values) {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static double? MedianOrNull(IEnumerable<double> values) {
        var arr = values.ToArray();
        return arr.Length == 0 ? null : Median(arr);
    }

    private static double Round(double v, int digits) => double.IsFinite(v) ? Math.Round(v, digits) : v;
}
