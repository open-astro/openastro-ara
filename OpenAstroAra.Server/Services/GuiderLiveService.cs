#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>The live guide-camera view's control surface (#1364, epic item 1).</summary>
public interface IGuiderLiveService {
    /// <summary>What the guider sees and holds; <c>Connected</c> false with everything null when no
    /// guider is connected (200, never 404 — the panel shows its empty state).</summary>
    Task<GuiderLiveDto> GetAsync(CancellationToken ct);
    /// <summary>The guider's latest frame as its own stretched JPEG, or null when it has none yet
    /// (not looping) or no guider is connected.</summary>
    Task<byte[]?> GetFrameJpegAsync(CancellationToken ct);
    /// <summary>Start looping exposures so there is a picture and a star to pick.</summary>
    Task LoopAsync(CancellationToken ct);
    /// <summary>Stop looping or guiding.</summary>
    Task StopAsync(CancellationToken ct);
    /// <summary>Lock on the star nearest a tap, in guide pixels.</summary>
    Task SelectStarAsync(double x, double y, CancellationToken ct);
    Task DeselectStarAsync(CancellationToken ct);
    /// <summary>This session's calibration run, the stored result and Ara's verdict on it.</summary>
    Task<GuiderCalibrationViewDto> GetCalibrationAsync(CancellationToken ct);
    /// <summary>Drop the stored calibration so the next guide start calibrates again.</summary>
    Task ClearCalibrationAsync(CancellationToken ct);
    /// <summary>#1364 item 3 — the guider's current settings and its algorithm names per axis.</summary>
    Task<GuiderAdvancedViewDto> GetAdvancedAsync(CancellationToken ct);
    /// <summary>#1364 item 5 — the guider's recent guide steps, oldest first, with their ages.</summary>
    Task<GuiderHistoryDto> GetHistoryAsync(int maxPoints, CancellationToken ct);
    /// <summary>#1364 item 4 — the Guiding Assistant's state, figures and recommendations.</summary>
    Task<GuidingAssistantStatusDto> GetGuidingAssistantAsync(CancellationToken ct);
    /// <summary>Start the unguided measurement; the status it returns is the run's first.</summary>
    Task<GuidingAssistantStatusDto> StartGuidingAssistantAsync(double? durationSec, bool measureBacklash, CancellationToken ct);
    Task<GuidingAssistantStatusDto> StopGuidingAssistantAsync(CancellationToken ct);
    /// <summary>Write the named recommendations to the guider's profile; returns what it applied.</summary>
    Task<IReadOnlyList<string>> ApplyGuidingAssistantAsync(IReadOnlyList<string> which, CancellationToken ct);
}

/// <summary>
/// Setup / Live → Guiding → the guide camera's own picture, from Ara instead of the guider's web page:
/// the frame the guider is looping on, the star it locked, the search box, the SNR and mass of the
/// last guide frame, multi-star on or off, and the other stars it found; a tap on the picture picks a
/// star. Everything goes through the daemon's guider connection (the client never talks to the guider,
/// §63.20): the picture is the guider's live JPEG fetched daemon-side, the rest is a handful of RPCs
/// answered best-effort — one the guider refuses (no camera yet, no image) leaves its field null.
/// </summary>
public sealed partial class GuiderLiveService : IGuiderLiveService {

    /// <summary>How many of the guider's detected stars the view lists: enough to show a multi-star
    /// set, few enough to draw without clutter.</summary>
    internal const int MaxStars = 12;

    private readonly GuiderService _guider;
    private readonly IPolarAlignFrameFetcher _fetcher;
    private readonly ILogger<GuiderLiveService> _logger;

    public GuiderLiveService(GuiderService guider, IPolarAlignFrameFetcher fetcher, ILogger<GuiderLiveService>? logger = null) {
        _guider = guider ?? throw new ArgumentNullException(nameof(guider));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _logger = logger ?? NullLogger<GuiderLiveService>.Instance;
    }

    internal static readonly GuiderLiveDto Disconnected = new(
        Connected: false, AppState: null, FrameWidth: null, FrameHeight: null, LockX: null, LockY: null,
        SearchRegion: null, Snr: null, StarMass: null, MultiStarEnabled: null, Stars: Array.Empty<GuiderLiveStarDto>(), HasFrame: false);

    public async Task<GuiderLiveDto> GetAsync(CancellationToken ct) {
        var guider = _guider.ConnectedGuiderOrNull();
        if (guider is null) {
            return Disconnected;
        }
        // Each read is independent and best-effort: a guider without a camera answers the frame size
        // with an error, one that is not looping has no stars — neither is a reason to hide the rest.
        // They run together (each RPC is its own connection), so a slow guider costs one RPC's time,
        // not five, against the client's poll.
        var sizeTask = Quietly(() => guider.GetCameraFrameSizeAsync(ct), "frame size");
        var lockTask = Quietly(() => guider.GetLockPositionQuietlyAsync(ct), "lock position");
        var regionTask = Quietly(() => guider.GetSearchRegionAsync(ct), "search region");
        var multiTask = Quietly(() => guider.GetMultiStarEnabledAsync(ct), "multi-star");
        var centroidsTask = Quietly<IReadOnlyList<Phd2StarCentroid>?>(async () => await guider.GetStarCentroidsAsync(null, MaxStars, ct, PHD2Guider.LiveRpcTimeoutMs, notifyOnError: false).ConfigureAwait(false), "star centroids");
        await Task.WhenAll(sizeTask, lockTask, regionTask, multiTask, centroidsTask).ConfigureAwait(false);
        var size = await sizeTask.ConfigureAwait(false);
        var lockPos = await lockTask.ConfigureAwait(false);
        var region = await regionTask.ConfigureAwait(false);
        var multi = await multiTask.ConfigureAwait(false);
        var centroids = await centroidsTask.ConfigureAwait(false);
        var stars = new List<GuiderLiveStarDto>();
        if (centroids is not null) {
            foreach (var c in centroids) {
                if (c.X is double x && c.Y is double y) {
                    stars.Add(new GuiderLiveStarDto(Math.Round(x, 2), Math.Round(y, 2), c.Snr, c.Mass, c.Hfd));
                }
            }
        }
        var state = guider.AppState?.State;
        // The last guide frame's SNR and mass describe the guide star only while guiding; a stopped or
        // looping guider would otherwise report a previous session's values to every consumer. With a
        // star chosen but not guided there is no guide frame: the chosen star's own detection (the
        // centroid at the lock position) stands in, so a tap shows an SNR at once (#1364 tap item).
        var (snr, mass) = state == "Guiding" ? _guider.LatestStarStats
            : state == "Selected" && lockPos is not null ? SelectedStarStats(stars, lockPos.X, lockPos.Y, region)
            : default;
        return new GuiderLiveDto(
            Connected: true,
            AppState: state,
            FrameWidth: size?.Width,
            FrameHeight: size?.Height,
            LockX: lockPos is null ? null : Math.Round(lockPos.X, 2),
            LockY: lockPos is null ? null : Math.Round(lockPos.Y, 2),
            SearchRegion: region,
            Snr: snr,
            StarMass: mass,
            MultiStarEnabled: multi,
            Stars: stars,
            HasFrame: state is not null && state != "Stopped");
    }

    /// <summary>How long the frame fetch may take before the picture is skipped this poll — the same
    /// bound the polar-align preview uses (<see cref="PolarAlignService"/>).</summary>
    internal static readonly TimeSpan FrameFetchTimeout = TimeSpan.FromSeconds(5);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The picture is best-effort: any fetch failure (no frame yet, a wedged or slow guider HTTP side, a timeout) means no image this poll — 204, never a 500.")]
    public async Task<byte[]?> GetFrameJpegAsync(CancellationToken ct) {
        var guider = _guider.ConnectedGuiderOrNull();
        if (guider is null || string.IsNullOrEmpty(guider.ConnectedHost)) {
            return null;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FrameFetchTimeout);
        try {
            return await _fetcher.FetchLiveFrameJpegAsync(guider.ConnectedHost, guider.ConnectedRpcPort, timeout.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            // No frame yet (404 while not looping), the guider's HTTP side down or too slow (the fetcher's
            // own timeout surfaces as TaskCanceledException with the caller's token still live).
            LogFrameUnavailable(ex.Message);
            return null;
        }
    }

    public Task LoopAsync(CancellationToken ct) => _guider.RequireConnectedGuider().LoopAsync(ct);

    public Task StopAsync(CancellationToken ct) => _guider.RequireConnectedGuider().StopCaptureAsync(ct);

    /// <summary>Lock on the star nearest (x, y). Refused as out of range (400) when the position is
    /// negative or not finite, or — once the guider reports its frame size — outside the frame; with no
    /// frame size known, the guider itself judges the position.</summary>
    public async Task SelectStarAsync(double x, double y, CancellationToken ct) {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0) {
            throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) is not a position in the frame");
        }
        var guider = _guider.RequireConnectedGuider();
        var size = await Quietly(() => guider.GetCameraFrameSizeAsync(ct), "frame size").ConfigureAwait(false);
        if (size is { } s && (x >= s.Width || y >= s.Height)) {
            throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) is outside the {s.Width}×{s.Height} guide frame");
        }
        await guider.SetLockPositionAsync(x, y, ct).ConfigureAwait(false);
    }

    public Task DeselectStarAsync(CancellationToken ct) => _guider.RequireConnectedGuider().DeselectStarAsync(ct);

    /// <summary>Axes more than this far from square: the mount or the camera orientation moved during
    /// calibration, or Dec backlash ate the first steps — PHD2 warns at the same figure.</summary>
    internal const double OrthogonalityCheckDeg = 10;
    /// <summary>Dec rate outside this factor of the RA rate (RA rate corrected for declination): one
    /// axis barely moved — usually a stuck Dec or a wrong guide rate.</summary>
    internal const double RateRatioCheck = 2.0;
    /// <summary>Above this declination (or with none reported) the RA rate's cos(dec) correction is too
    /// large to trust, so the rate comparison is skipped rather than reading a false "check".</summary>
    internal const double RateCheckMaxDecDeg = 80;

    public async Task<GuiderCalibrationViewDto> GetCalibrationAsync(CancellationToken ct) {
        var guider = _guider.ConnectedGuiderOrNull();
        if (guider is null) {
            return new GuiderCalibrationViewDto(false, "none", Array.Empty<GuiderCalibrationStepDto>(), null, null, "redo", "No guider connected.");
        }
        var (runState, steps, error) = guider.CalibrationRun;
        var data = await Quietly<GuiderCalibrationData?>(async () => await guider.GetCalibrationDataAsync(ct).ConfigureAwait(false), "calibration data").ConfigureAwait(false);
        var dto = data is null ? null : ToDto(data);
        var (verdict, reason) = Judge(runState, error, dto);
        var stepDtos = new List<GuiderCalibrationStepDto>(steps.Count);
        foreach (var s in steps) {
            // A step without dx/dy (an older guider build) has nowhere to go on the plot: drawing it at
            // the origin would read as a star that never moved.
            if (s.Dx is not double dx || s.Dy is not double dy) {
                continue;
            }
            stepDtos.Add(new GuiderCalibrationStepDto(s.Direction ?? "", s.Step, Math.Round(dx, 2), Math.Round(dy, 2), Math.Round(s.Distance, 2)));
        }
        return new GuiderCalibrationViewDto(true, runState, stepDtos, error, dto, verdict, reason);
    }

    public Task ClearCalibrationAsync(CancellationToken ct) => _guider.RequireConnectedGuider().ClearCalibrationAsync(ct);

    public async Task<GuiderHistoryDto> GetHistoryAsync(int maxPoints, CancellationToken ct) {
        var guider = _guider.ConnectedGuiderOrNull();
        if (guider is null) {
            return new GuiderHistoryDto(false, Array.Empty<GuiderHistoryStepDto>());
        }
        var raw = await Quietly<Newtonsoft.Json.Linq.JObject?>(async () => await guider.GetGuideHistoryAsync(maxPoints, ct).ConfigureAwait(false), "guide history").ConfigureAwait(false);
        return new GuiderHistoryDto(true, raw is null ? Array.Empty<GuiderHistoryStepDto>() : MapHistory(raw, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    /// <summary>Spacing assumed between points whose timestamp is not a plausible Unix time (an older
    /// guider build), so the graph still draws them in order.</summary>
    internal const double FallbackStepSpacingSec = 2.0;

    /// <summary>
    /// The guider's <c>get_guide_history</c> result → guide steps, oldest first. <c>ra</c>/<c>dec</c> are
    /// the star's offset on the mount axes in guide px (what <c>guider.step</c> calls raw); the pulse
    /// signs follow the direction letters (E and S negative). Ages come from the guider's millisecond
    /// timestamps against <paramref name="nowUnixMs"/>; a point without one is spaced
    /// <see cref="FallbackStepSpacingSec"/> back from the newest. Pure — unit-tested.
    /// </summary>
    internal static IReadOnlyList<GuiderHistoryStepDto> MapHistory(Newtonsoft.Json.Linq.JObject raw, long nowUnixMs) {
        static double? Num(Newtonsoft.Json.Linq.JToken? t) =>
            t is { Type: Newtonsoft.Json.Linq.JTokenType.Float or Newtonsoft.Json.Linq.JTokenType.Integer } && double.IsFinite(t.Value<double>()) ? t.Value<double>() : null;
        var scale = Num(raw["pixel_scale"]) is > 0 and var s ? s : (double?)null;
        if (raw["points"] is not Newtonsoft.Json.Linq.JArray points) {
            return Array.Empty<GuiderHistoryStepDto>();
        }
        var steps = new List<GuiderHistoryStepDto>(points.Count);
        for (var i = 0; i < points.Count; i++) {
            var p = points[i];
            var ra = Num(p["ra"]);
            var dec = Num(p["dec"]);
            var raDur = Math.Abs(Num(p["ra_dur"]) ?? 0);
            var decDur = Math.Abs(Num(p["dec_dur"]) ?? 0);
            if (string.Equals(p["ra_dir"]?.ToString(), "E", StringComparison.OrdinalIgnoreCase)) {
                raDur = -raDur;
            }
            if (string.Equals(p["dec_dir"]?.ToString(), "S", StringComparison.OrdinalIgnoreCase)) {
                decDur = -decDur;
            }
            var t = Num(p["t"]);
            // Plausible Unix ms: after 2001 and not in the future by more than a minute.
            var age = t is double ms && ms > 1e12 && ms <= nowUnixMs + 60_000
                ? Math.Max(0, (nowUnixMs - ms) / 1000.0)
                : (points.Count - 1 - i) * FallbackStepSpacingSec;
            steps.Add(new GuiderHistoryStepDto(
                Math.Round(age, 3), ra, dec,
                scale is double sc && ra is double r ? Math.Round(r * sc, 3) : null,
                scale is double sc2 && dec is double d ? Math.Round(d * sc2, 3) : null,
                raDur, decDur, scale, Num(p["snr"]), Num(p["mass"])));
        }
        return steps;
    }

    public async Task<GuiderAdvancedViewDto> GetAdvancedAsync(CancellationToken ct) {
        var guider = _guider.ConnectedGuiderOrNull();
        if (guider is null) {
            return new GuiderAdvancedViewDto(false, new GuiderAdvancedSettingsDto(), Array.Empty<string>(), Array.Empty<string>());
        }
        // All three at once: the read-back's thirteen getters already run in parallel, and the two
        // algorithm lists must not add two more timeouts after them on a slow guider.
        var currentT = Quietly<GuiderAdvancedSettings?>(async () => await guider.ReadAdvancedSettingsAsync(ct).ConfigureAwait(false), "advanced settings");
        var raT = Quietly<IReadOnlyList<string>?>(async () => await guider.GetAlgorithmsAsync("ra", ct).ConfigureAwait(false), "RA algorithms");
        var decT = Quietly<IReadOnlyList<string>?>(async () => await guider.GetAlgorithmsAsync("dec", ct).ConfigureAwait(false), "Dec algorithms");
        await Task.WhenAll(currentT, raT, decT).ConfigureAwait(false);
        var current = await currentT.ConfigureAwait(false);
        var ra = await raT.ConfigureAwait(false);
        var dec = await decT.ConfigureAwait(false);
        return new GuiderAdvancedViewDto(
            true,
            current is null ? new GuiderAdvancedSettingsDto() : GuiderAdvancedSettingsMapping.ToDto(current),
            ra ?? Array.Empty<string>(),
            dec ?? Array.Empty<string>());
    }

    /// <summary>The stored calibration with its orthogonality error: how far the RA and Dec axes are
    /// from 90° apart on the camera. Pure.</summary>
    internal static GuiderCalibrationDataDto ToDto(GuiderCalibrationData d) {
        double? ortho = null;
        if (d.XAngleDeg is double xa && d.YAngleDeg is double ya) {
            var diff = Math.Abs(xa - ya) % 180;
            if (diff > 90) {
                diff = 180 - diff;
            }
            ortho = Math.Round(Math.Abs(90 - diff), 1);
        }
        return new GuiderCalibrationDataDto(d.Calibrated, d.XAngleDeg, d.YAngleDeg, d.XRatePxPerSec, d.YRatePxPerSec,
            d.XParity, d.YParity, d.DeclinationDeg, ortho);
    }

    /// <summary>
    /// Ara's verdict on a calibration. <c>redo</c>: the run failed, or there is no stored calibration.
    /// <c>check</c>: there is one, but the axes are more than <see cref="OrthogonalityCheckDeg"/> from
    /// square, or the Dec rate is more than <see cref="RateRatioCheck"/>× off the RA rate once the RA
    /// rate is corrected for the declination (RA moves cos(dec) slower on the sky) — a check made only
    /// when the declination is known and within ±<see cref="RateCheckMaxDecDeg"/>°. <c>good</c> otherwise.
    /// A run still in progress is <c>check</c> with "Calibrating…". Pure — unit-tested.
    /// </summary>
    internal static (string Verdict, string? Reason) Judge(string runState, string? error, GuiderCalibrationDataDto? data) {
        if (runState == "running") {
            return ("check", "Calibrating…");
        }
        if (runState == "failed") {
            var failed = string.IsNullOrWhiteSpace(error) ? "The last calibration failed." : $"The last calibration failed: {error}.";
            // The guider may still hold the calibration from before this run; say so rather than imply
            // there is none.
            return ("redo", data is { Calibrated: true }
                ? $"{failed} The guider still holds an earlier calibration, used until a new one completes."
                : failed);
        }
        if (data is null || !data.Calibrated) {
            return ("redo", "No calibration stored — the next guide start calibrates.");
        }
        if (data.OrthogonalityErrorDeg is double ortho && ortho > OrthogonalityCheckDeg) {
            return ("check", $"RA and Dec axes are {ortho:F0}° from square — the mount or camera may have moved during calibration.");
        }
        if (data.XRatePxPerSec is > 0 and var xr && data.YRatePxPerSec is > 0 and var yr
                && data.DeclinationDeg is double dec && double.IsFinite(dec) && Math.Abs(dec) <= RateCheckMaxDecDeg) {
            var raOnSky = xr / Math.Cos(dec * Math.PI / 180);
            var ratio = yr / raOnSky;
            if (ratio > RateRatioCheck || ratio < 1 / RateRatioCheck) {
                return ("check", $"Dec rate is {ratio:F1}× the RA rate — one axis barely moved (backlash, or a wrong guide rate).");
            }
        }
        return ("good", null);
    }

    internal static readonly GuidingAssistantStatusDto AssistantDisconnected = MapGuidingAssistant(null, connected: false);

    public async Task<GuidingAssistantStatusDto> GetGuidingAssistantAsync(CancellationToken ct) {
        var guider = _guider.ConnectedGuiderOrNull();
        if (guider is null) {
            return AssistantDisconnected;
        }
        var raw = await Quietly<JObject?>(async () => await guider.GetGuidingAssistantStatusAsync(ct).ConfigureAwait(false), "guiding assistant").ConfigureAwait(false);
        // A guider that does not answer (older build without the method) reads as disconnected: the
        // dialog says "not available" rather than idle-and-ready.
        return raw is null ? AssistantDisconnected : MapGuidingAssistant(raw, connected: true);
    }

    public async Task<GuidingAssistantStatusDto> StartGuidingAssistantAsync(double? durationSec, bool measureBacklash, CancellationToken ct) {
        var raw = await _guider.RequireConnectedGuider().StartGuidingAssistantAsync(durationSec, measureBacklash, ct).ConfigureAwait(false);
        return MapGuidingAssistant(raw, connected: true);
    }

    public async Task<GuidingAssistantStatusDto> StopGuidingAssistantAsync(CancellationToken ct) {
        var raw = await _guider.RequireConnectedGuider().StopGuidingAssistantAsync(ct).ConfigureAwait(false);
        return MapGuidingAssistant(raw, connected: true);
    }

    public Task<IReadOnlyList<string>> ApplyGuidingAssistantAsync(IReadOnlyList<string> which, CancellationToken ct) =>
        _guider.RequireConnectedGuider().ApplyGuidingAssistantAsync(which, ct);

    /// <summary>The detected star under the lock position (within the search region, nearest wins):
    /// its SNR and mass are the chosen star's while the guider is looping with a star selected. Pure.</summary>
    internal static (double? Snr, double? Mass) SelectedStarStats(IReadOnlyList<GuiderLiveStarDto> stars, double lockX, double lockY, int? searchRegion) {
        var radius = searchRegion is int r && r > 0 ? r : 15;
        GuiderLiveStarDto? best = null;
        var bestD2 = double.MaxValue;
        foreach (var s in stars) {
            var dx = s.X - lockX;
            var dy = s.Y - lockY;
            var d2 = dx * dx + dy * dy;
            if (d2 <= radius * (double)radius && d2 < bestD2) {
                best = s;
                bestD2 = d2;
            }
        }
        return best is null ? default : (best.Snr, best.Mass);
    }

    /// <summary>The guider's <c>guiding_assistant_get_status</c> result → DTO. Fields the guider left
    /// out (idle; fewer than two samples; no backlash test; not done) are null, never 0. Pure — unit-tested.</summary>
    internal static GuidingAssistantStatusDto MapGuidingAssistant(JObject? raw, bool connected) {
        static double? Num(JToken? t) =>
            t is { Type: JTokenType.Float or JTokenType.Integer } && double.IsFinite(t.Value<double>()) ? t.Value<double>() : null;
        static bool? Flag(JToken? t) => t is { Type: JTokenType.Boolean } ? t.Value<bool>() : null;
        static string? Str(JToken? t) => t is { Type: JTokenType.String } && !string.IsNullOrEmpty(t.Value<string>()) ? t.Value<string>() : null;
        var state = Str(raw?["state"]) ?? "idle";
        GuidingAssistantRecommendationsDto? recs = null;
        if (raw?["recommendations"] is JObject r) {
            var messages = new List<GuidingAssistantMessageDto>();
            if (r["messages"] is JArray msgs) {
                foreach (var m in msgs) {
                    // A bare string in the array would throw on indexing a JValue; skip it.
                    if (m is JObject mo && Str(mo["text"]) is string text) {
                        messages.Add(new GuidingAssistantMessageDto(Str(mo["id"]) ?? "", text));
                    }
                }
            }
            recs = new GuidingAssistantRecommendationsDto(
                MinMoveRaPx: Num(r["min_move_ra_px"]),
                MinMoveDecPx: Num(r["min_move_dec_px"]),
                MinMoveSeeingBased: Flag(r["min_move_seeing_based"]),
                ExposureSec: Num(r["exposure_s"]),
                ExposureMaxSec: Num(r["exposure_max_s"]),
                DriftLimitingExposureSec: Num(r["drift_limiting_exposure_s"]),
                DecBacklashMs: Num(r["dec_backlash_ms"]),
                DecGuideMode: Str(r["dec_guide_mode"]),
                DecAlgorithm: Str(r["dec_algorithm"]),
                Messages: messages);
        }
        return new GuidingAssistantStatusDto(
            Connected: connected,
            State: state,
            DurationSec: Num(raw?["duration_s"]),
            MeasureBacklash: Flag(raw?["measure_backlash"]),
            ElapsedSec: Num(raw?["elapsed_s"]),
            Samples: Num(raw?["samples"]) is double n ? (int)Math.Clamp(n, 0, int.MaxValue) : null,
            PixelScale: Num(raw?["pixel_scale"]),
            RaDriftPxMin: Num(raw?["ra_drift_px_min"]),
            DecDriftPxMin: Num(raw?["dec_drift_px_min"]),
            RaRmsPx: Num(raw?["ra_rms_px"]),
            DecRmsPx: Num(raw?["dec_rms_px"]),
            HighFreqRmsPx: Num(raw?["high_freq_rms_px"]),
            RaPeakPx: Num(raw?["ra_peak_px"]),
            DecPeakPx: Num(raw?["dec_peak_px"]),
            RaPeakPeakPx: Num(raw?["ra_peak_peak_px"]),
            RaMaxDriftPxSec: Num(raw?["ra_max_drift_px_s"]),
            Snr: Num(raw?["snr"]),
            StarMass: Num(raw?["star_mass"]),
            PolarAlignErrorArcmin: Num(raw?["polar_align_error_arcmin"]),
            PolarAlignErrorIsLowerBound: Flag(raw?["polar_align_error_is_lower_bound"]),
            BacklashStatus: Str(raw?["backlash_status"]),
            BacklashResult: Str(raw?["backlash_result"]),
            BacklashMs: Num(raw?["backlash_ms"]),
            BacklashSigmaMs: Num(raw?["backlash_sigma_ms"]),
            BacklashPx: Num(raw?["backlash_px"]),
            Recommendations: recs,
            Error: Str(raw?["error"]));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort read of one field of the live view: a refused RPC leaves it null, the rest of the view still answers. Log-and-recover boundary.")]
    private async Task<T?> Quietly<T>(Func<Task<T?>> read, string what) {
        try {
            return await read().ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            LogFieldUnavailable(what, ex.Message);
            return default;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Guider live view: {What} not available ({Reason})")]
    partial void LogFieldUnavailable(string what, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Guider live view: no frame ({Reason})")]
    partial void LogFrameUnavailable(string reason);
}
