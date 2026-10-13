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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Sequencer.SequenceItem.Telescope;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1264 — the daemon side of the sequencer's <c>TrackingProbe</c>: takes each rung of the ladder as an
/// analysis frame (same capture path and gate as autofocus probes), reduces it to median eccentricity and
/// FWHM (§59.5 star analysis), applies the pass rule, stores <c>tracking_calibration</c> on the profile,
/// publishes the live <c>tracking_probe.*</c> events and notifies when the mount is far off its prior.
/// </summary>
public sealed partial class TrackingProbeService : ITrackingProbeExecutor {

    /// <summary>A rung needs this many stars to be judged (the §59.5 statistic bar, not the autofocus bar).</summary>
    internal const int MinStars = CameraService.MinStarsForAnalysis;
    /// <summary>A rung passes when its median eccentricity is at or under the request's tolerance; this is
    /// the shipped bar when the request carries none (issue #1264, Imaging → Unguided).</summary>
    internal const double EccentricityTolerance = OpenAstroAra.Sequencer.Utility.UnguidedExposureLadder.DefaultEccentricityTolerance;
    /// <summary>…and its median FWHM is at most this × the first usable rung's (trailing fattens stars too).</summary>
    internal const double FwhmGrowth = 1.2;
    /// <summary>Measured ceiling more than this × off the prior (either way) raises a notification.</summary>
    internal const double DivergenceRatio = 2.0;
    /// <summary>The newest polar-alignment routine within this window is "tonight's".</summary>
    internal static readonly TimeSpan PolarAlignWindow = TimeSpan.FromHours(24);

    private readonly IProfileStore _profiles;
    private readonly IAnalysisFrameSource _frames;
    private readonly Func<AnalysisFrame, CancellationToken, FocusFeatureVector> _metric;
    private readonly IWsBroadcaster? _ws;
    private readonly INotificationService? _notifications;
    private readonly IPolarAlignmentLog? _polarAlignLog;
    private readonly IMountSpecSheetSource? _specs;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger<TrackingProbeService> _logger;

    public TrackingProbeService(
            IProfileStore profiles,
            IAnalysisFrameSource frames,
            ILogger<TrackingProbeService>? logger = null,
            Func<AnalysisFrame, CancellationToken, FocusFeatureVector>? metric = null,
            IWsBroadcaster? ws = null,
            INotificationService? notifications = null,
            IPolarAlignmentLog? polarAlignLog = null,
            Func<DateTimeOffset>? clock = null,
            IMountSpecSheetSource? specs = null) {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _logger = logger ?? NullLogger<TrackingProbeService>.Instance;
        _metric = metric ?? DefaultMetric;
        _ws = ws;
        _notifications = notifications;
        _polarAlignLog = polarAlignLog;
        _specs = specs;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The §59.5 full-frame star analysis reduced to its feature vector.</summary>
    internal static FocusFeatureVector DefaultMetric(AnalysisFrame frame, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(frame);
        var result = StarDetector.Detect(frame.Pixels.Span, frame.Width, frame.Height, CameraService.AnalysisDetectionParams(), ct);
        return FocusFeatureExtractor.Extract(result);
    }

    /// <summary>Median roundness (minor/major axis ratio) → eccentricity: e = √(1 − r²). Unusable → NaN.</summary>
    internal static double Eccentricity(double roundness) {
        if (double.IsNaN(roundness) || roundness <= 0) {
            return roundness == 0 ? 1 : double.NaN;
        }
        var r = Math.Min(1, roundness);
        return Math.Sqrt(Math.Max(0, 1 - (r * r)));
    }

    /// <summary>Arcseconds per unbinned pixel from the profile optics, or null without optics.</summary>
    internal static double? PixelScaleArcsec(OpticsSettingsDto? optics) {
        if (optics is null || optics.FocalLengthMm <= 0 || optics.PixelSizeUm <= 0) {
            return null;
        }
        var reducer = optics.ReducerFactor > 0 ? optics.ReducerFactor : 1;
        return 206.265 * optics.PixelSizeUm / (optics.FocalLengthMm * reducer);
    }

    /// <summary>The ceiling the ladder decided: the longest passing rung, moved one half-step toward the first
    /// failing rung when both exist. A ladder whose FIRST usable rung already trails is a measurement too —
    /// that rung is the ceiling (nothing shorter was tried; the guard may still step down from there) — rather
    /// than an inconclusive probe that would leave the planned long subs in place (review of #1318). Null
    /// (inconclusive) only when fewer than two usable rungs exist and none of them failed.</summary>
    internal static double? Decide(IReadOnlyList<TrackingProbeRungDto> rungs) {
        ArgumentNullException.ThrowIfNull(rungs);
        var usable = rungs.Where(r => r.Usable).ToList();
        if (usable.Count == 0) {
            return null;
        }
        var lastPass = usable.LastOrDefault(r => r.Passed);
        if (lastPass is null) {
            return usable.Min(r => r.ExposureSeconds);
        }
        if (usable.Count < 2) {
            return null;
        }
        var firstFail = usable.FirstOrDefault(r => !r.Passed && r.ExposureSeconds > lastPass.ExposureSeconds);
        return firstFail is null ? lastPass.ExposureSeconds : (lastPass.ExposureSeconds + firstFail.ExposureSeconds) / 2;
    }

    /// <inheritdoc/>
    public async Task<double?> ResolvePriorAsync(double? bodyPriorSeconds, CancellationToken token) =>
        (await ResolvePriorWithSourceAsync(bodyPriorSeconds, token).ConfigureAwait(false)).Seconds;

    /// <summary>The prior and its source, in order: the body's own value; the profile's stored
    /// calibration (last night's measurement); the mount's spec sheet (#1264 stage 1) turned into
    /// seconds for THIS rig's pixel scale, the site's typical seeing and the latest polar-align
    /// residual. Null when none applies — the ladder alone decides.</summary>
    public async Task<(double? Seconds, string? Source)> ResolvePriorWithSourceAsync(double? bodyPriorSeconds, CancellationToken token) {
        if (bodyPriorSeconds is double body && body > 0) {
            return (body, "the sequence's own prior");
        }
        var stored = _profiles.GetTrackingCalibration();
        if (stored is { CeilingSeconds: > 0 }) {
            return (stored.CeilingSeconds, "the last probe");
        }
        var sheet = _specs?.CurrentMountSpecs;
        if (sheet is null) {
            return (null, null);
        }
        var pixelScale = PixelScaleArcsec(_profiles.GetOpticsSettings());
        var seeing = _profiles.GetSiteSettings()?.TypicalSeeingArcsec;
        var polarAlign = await LatestPolarAlignErrorAsync(token).ConfigureAwait(false);
        var basis = _profiles.GetUnguidedSettings().PriorToleranceBasis;
        var seconds = MountSpecSheet.UnguidedPriorSeconds(sheet, pixelScale, seeing, polarAlign, basis);
        if (seconds is null) {
            return (null, null);
        }
        // A sheet with no usable tracking figures (or encoders) contributes nothing: the number is the
        // polar-align drift alone, and the label must not credit the sheet for it.
        var source = !MountSpecSheet.ContributesPeriodicError(sheet)
            ? "the latest polar alignment"
            : sheet.Source is null ? "the mount's spec sheet" : $"the mount's spec sheet ({sheet.Source})";
        return (seconds, source);
    }

    public async Task<TrackingProbeResult> ProbeAsync(TrackingProbeRequest request, IProgress<ApplicationStatus> progress, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(request);
        var ladder = request.RungsSeconds;
        var rungs = new List<TrackingProbeRungDto>(ladder.Count);
        double? referenceFwhm = null;
        await PublishAsync(WsEventCatalog.TrackingProbeStarted, new JsonObject {
            ["target"] = request.TargetName,
            ["rungs"] = new JsonArray(ladder.Select(r => (JsonNode)JsonValue.Create(r)).ToArray()),
            ["prior_seconds"] = request.PriorSeconds,
        }).ConfigureAwait(false);
        for (var i = 0; i < ladder.Count; i++) {
            token.ThrowIfCancellationRequested();
            var exposure = ladder[i];
            progress?.Report(new ApplicationStatus {
                Status = string.Create(CultureInfo.InvariantCulture, $"Tracking probe: {exposure:0.#} s rung {i + 1} of {ladder.Count}"),
                Progress = i, MaxProgress = ladder.Count, ProgressType = ApplicationStatus.StatusProgressType.ValueOfMaxValue,
            });
            var frame = await _frames.CaptureForAnalysisAsync(exposure, 1, token).ConfigureAwait(false);
            var features = _metric(frame, token);
            var usable = features.StarCount >= MinStars && features.MedianFWHM > 0 && !double.IsNaN(features.MedianFWHM);
            var ecc = usable ? Eccentricity(features.MedianRoundness) : double.NaN;
            var tolerance = request.EccentricityTolerance > 0 ? request.EccentricityTolerance : EccentricityTolerance;
            var passed = usable && !double.IsNaN(ecc) && ecc <= tolerance
                && (referenceFwhm is not double reference || features.MedianFWHM <= FwhmGrowth * reference);
            if (usable && referenceFwhm is null) {
                referenceFwhm = features.MedianFWHM;
            }
            var rung = new TrackingProbeRungDto(exposure, usable && !double.IsNaN(ecc) ? ecc : null, usable ? features.MedianFWHM : null, features.StarCount, usable, passed);
            rungs.Add(rung);
            LogRung(exposure, rung.Eccentricity ?? double.NaN, rung.FwhmPx ?? double.NaN, features.StarCount, usable, passed);
            await PublishAsync(WsEventCatalog.TrackingProbeRungComplete, new JsonObject {
                ["rung_index"] = i, ["exposure_seconds"] = exposure, ["eccentricity"] = rung.Eccentricity,
                ["fwhm_px"] = rung.FwhmPx, ["stars"] = features.StarCount, ["usable"] = usable, ["passed"] = passed,
            }).ConfigureAwait(false);
            if (usable && !passed) {
                break; // the ladder stops at the first failing rung
            }
        }

        var ceiling = Decide(rungs);
        var usableCount = rungs.Count(r => r.Usable);
        var optics = _profiles.GetOpticsSettings();
        var pixelScale = PixelScaleArcsec(optics);
        double? seeing = referenceFwhm is double fwhm && pixelScale is double scale ? fwhm * scale : _profiles.GetSiteSettings()?.TypicalSeeingArcsec;
        string summary;
        if (ceiling is not double measured) {
            if (request.PriorSeconds is double prior) {
                // The prior stands — and is recorded as tonight's calibration with source "prior", so the
                // profile says what the run actually used (and the next night's prior is still it).
                summary = string.Create(CultureInfo.InvariantCulture, $"only {usableCount} usable rung(s) (need 2) — keeping the {prior:0.#} s prior");
                var polarAlignForPrior = await LatestPolarAlignErrorAsync(token).ConfigureAwait(false);
                _profiles.PutTrackingCalibration(new TrackingCalibrationDto(prior, _clock(), polarAlignForPrior, pixelScale, seeing, prior, "prior", rungs));
            } else {
                summary = string.Create(CultureInfo.InvariantCulture, $"only {usableCount} usable rung(s) (need 2) and no prior — exposures left as planned");
            }
            await PublishAsync(WsEventCatalog.TrackingProbeCompleted, new JsonObject {
                ["ceiling_seconds"] = request.PriorSeconds, ["prior_seconds"] = request.PriorSeconds, ["source"] = "prior",
                ["usable_rungs"] = usableCount, ["summary"] = summary,
            }).ConfigureAwait(false);
            return new TrackingProbeResult(null, usableCount, summary);
        }

        var polarAlign = await LatestPolarAlignErrorAsync(token).ConfigureAwait(false);
        var calibration = new TrackingCalibrationDto(measured, _clock(), polarAlign, pixelScale, seeing, request.PriorSeconds, "measured", rungs);
        _profiles.PutTrackingCalibration(calibration);
        summary = usableCount == 1
            ? string.Create(CultureInfo.InvariantCulture, $"even the shortest rung ({measured:0.#} s) trailed — unguided ceiling {measured:0.#} s")
            : string.Create(CultureInfo.InvariantCulture, $"unguided ceiling {measured:0.#} s from {usableCount} usable rung(s)");
        LogDecided(measured, usableCount, request.PriorSeconds ?? double.NaN);
        await PublishAsync(WsEventCatalog.TrackingProbeCompleted, new JsonObject {
            ["ceiling_seconds"] = measured, ["prior_seconds"] = request.PriorSeconds, ["source"] = "measured",
            ["usable_rungs"] = usableCount, ["summary"] = summary,
        }).ConfigureAwait(false);
        if (request.PriorSeconds is double p && p > 0) {
            // The ladder is capped at 2 × the prior, so only the "far below" side can fire today; the
            // "far above" side waits for a ladder that may exceed the prior.
            var ratio = measured / p;
            if (ratio > DivergenceRatio || ratio < 1 / DivergenceRatio) {
                await NotifyDivergenceAsync(measured, p, request.PriorSource ?? "the prior").ConfigureAwait(false);
            }
        }
        return new TrackingProbeResult(measured, usableCount, summary);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The polar-alignment log is an enrichment of the calibration record; a store fault must not fail the probe. Log-and-recover boundary.")]
    private async Task<double?> LatestPolarAlignErrorAsync(CancellationToken ct) {
        if (_polarAlignLog is null) {
            return null;
        }
        try {
            var record = await _polarAlignLog.GetLatestMeasuredAsync(_clock() - PolarAlignWindow, ct).ConfigureAwait(false);
            return record?.FinalErrorArcmin;
        } catch (Exception ex) {
            LogPolarAlignLookupFailed(ex);
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Notification store faults must never fail the probe. Log-and-recover boundary.")]
    private async Task NotifyDivergenceAsync(double measured, double prior, string priorSource) {
        if (_notifications is null) {
            return;
        }
        try {
            var worse = measured < prior;
            var factor = worse ? prior / measured : measured / prior;
            var message = worse
                ? string.Create(CultureInfo.InvariantCulture, $"Tracking held {measured:0.#} s unguided, {factor:0.#}× worse than the {prior:0.#} s prior from {priorSource} — check balance, clutch, polar alignment and the worm; or the prior is wrong. Tonight's measurement is now the prior.")
                : string.Create(CultureInfo.InvariantCulture, $"Tracking held {measured:0.#} s unguided, {factor:0.#}× better than the {prior:0.#} s prior from {priorSource} — tonight's measurement is now the prior.");
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(), PostedUtc: _clock(), Severity: worse ? NotificationSeverity.Warning : NotificationSeverity.Info,
                Category: NotificationCategory.Equipment, Title: worse ? "Tracking far below its prior" : "Tracking far above its prior",
                Message: message, Read: false, Dismissed: false, DismissedUtc: null, Payload: null,
                RelatedEntityType: null, RelatedEntityId: null), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogNotifyFailed(ex);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort; a broadcaster fault must never fail the probe. Log-and-recover boundary.")]
    private async Task PublishAsync(string type, JsonObject payload) {
        if (_ws is null) {
            return;
        }
        try {
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(type, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogPublishFailed(ex, type);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tracking probe rung {ExposureSeconds} s: eccentricity {Eccentricity:0.00}, FWHM {FwhmPx:0.00} px, {Stars} stars, usable={Usable}, passed={Passed}")]
    private partial void LogRung(double exposureSeconds, double eccentricity, double fwhmPx, int stars, bool usable, bool passed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tracking probe decided an unguided ceiling of {CeilingSeconds} s from {UsableRungs} usable rung(s) (prior {PriorSeconds} s)")]
    private partial void LogDecided(double ceilingSeconds, int usableRungs, double priorSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tracking probe: polar-alignment log lookup failed")]
    private partial void LogPolarAlignLookupFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tracking probe: divergence notification failed")]
    private partial void LogNotifyFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tracking probe: WS publish of {EventType} failed")]
    private partial void LogPublishFailed(Exception ex, string eventType);
}
