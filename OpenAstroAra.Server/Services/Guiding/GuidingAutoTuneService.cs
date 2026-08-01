#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using Microsoft.Extensions.Logging;
using OpenAstroAra.Core.Guiding;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services.Guiding;

public interface IGuidingAutoTuneService {
    GuidingAutoTuneCapabilitiesDto GetCapabilities();
    GuidingAutoTuneStatusDto GetStatus();
    GuidingAutoTuneStatusDto GetStatus(Guid sessionId);
    GuidingAutoTuneReportDto GetReport();
    GuidingAutoTuneReportDto GetReport(Guid sessionId);
    Task<GuidingAutoTuneStatusDto> StartAsync(GuidingAutoTuneStartRequestDto request, CancellationToken ct);
    Task<GuidingAutoTuneStatusDto> CancelAsync(CancellationToken ct);
    Task<GuidingAutoTuneStatusDto> CancelAsync(Guid sessionId, CancellationToken ct);
    Task<GuidingAutoTuneStatusDto> ApplyAsync(CancellationToken ct);
    Task<GuidingAutoTuneStatusDto> ApplyAsync(Guid sessionId, CancellationToken ct);
    Task<GuidingAutoTuneStatusDto> RollbackAsync(CancellationToken ct);
    Task<GuidingAutoTuneStatusDto> RollbackAsync(Guid sessionId, CancellationToken ct);
}

/// <summary>
/// Deterministic auto-tune service. Analysis, planning, bounded experiments, and rollback run server-side.
/// Guide-rate changes require the optional Alpaca telescope adapter and invalidate calibration.
/// </summary>
public sealed class GuidingAutoTuneService : IGuidingAutoTuneService, IDisposable {
    private static readonly string[] MainCameraValidationLock = { "main-camera validation is unavailable" };
    private static readonly string[] GuideRateAdapterLock = { "mount guide-rate read/write adapter is not registered" };
    private static readonly string[] ScaleWarnings = { "Guide image scale unknown." };
    private static readonly string[] ApplyFailureWarnings = { "Apply failed; settings restored." };
    private static readonly string[] RestoreFailureWarnings = { "Apply failed and automatic restore failed; reconnect guider and use rollback." };
    private static readonly string[] DisconnectedCancelWarnings = { "Guider disconnected; snapshot remains available for rollback after reconnect." };
    private static readonly string[] SafetyMonitorWarnings = { "Safety monitor is not connected; environmental safety is not verified." };
    private static readonly string[] TrackingRateWarnings = { "Mount is not using sidereal tracking; auto-tune does not change tracking rate." };
    private static readonly string[] MeridianWarnings = { "Mount is within 10 minutes of the meridian; auto-tune is unsafe near a possible flip." };
    private static readonly string[] NoWinnerWarnings = { "No candidate met the automatic-improvement threshold." };
    private static readonly string[] CancellationTimeoutWarnings = { "Cancellation timeout; keep guider connected and wait for rollback worker." };
    private static readonly string[] ReportNoWarnings = { "- None" };
    private static readonly double[] GuideRateFactors = { 1, .5, 1.5 };
    private const double MainImageCriticalEccentricityIncrease = .05;
    private static readonly Action<ILogger, Exception?> RollbackFailureLog =
        LoggerMessage.Define(LogLevel.Error, new EventId(9101, "GuidingAutoTuneRollback"),
            "Guiding auto-tune rollback failed after apply error");
    private static readonly Action<ILogger, Exception?> PhasePublishFailureLog =
        LoggerMessage.Define(LogLevel.Warning, new EventId(9102, "GuidingAutoTunePhasePublish"),
            "Guiding auto-tune phase event publish failed");
    private readonly GuiderService _guiderService;
    private readonly GuidingTelemetryCollector _collector;
    private readonly IProfileStore _profiles;
    private readonly ITelescopeService? _telescope;
    private readonly ISafetyMonitorService? _safetyMonitor;
    private readonly ActiveRunSessionRegistry? _activeRuns;
    private readonly GuidingAutoTuneRepository _repository;
    private readonly IWsBroadcaster? _ws;
    private readonly MainCameraGuidingValidator? _mainCameraValidator;
    private readonly ILogger<GuidingAutoTuneService> _logger;
    private readonly object _gate = new();
    private GuidingAutoTuneSession? _session;
    private GuidingCandidateResult? _baseline;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private bool _startInProgress;

    private sealed record EvaluationResult(GuidingCandidateResult Result, GuidingTelemetryWindow Telemetry);

    public GuidingAutoTuneService(GuiderService guiderService, GuidingTelemetryCollector collector,
        IProfileStore profiles, GuidingAutoTuneRepository repository,
        ILogger<GuidingAutoTuneService>? logger = null, IWsBroadcaster? ws = null,
        ITelescopeService? telescope = null, ISafetyMonitorService? safetyMonitor = null,
        ActiveRunSessionRegistry? activeRuns = null,
        MainCameraGuidingValidator? mainCameraValidator = null) {
        _guiderService = guiderService ?? throw new ArgumentNullException(nameof(guiderService));
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _telescope = telescope;
        _safetyMonitor = safetyMonitor;
        _activeRuns = activeRuns;
        _mainCameraValidator = mainCameraValidator;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GuidingAutoTuneService>.Instance;
        _ws = ws;
        _session = _repository.RecoverInterruptedSession();
        _baseline = _session?.BaselineResult;
    }

    public GuidingAutoTuneCapabilitiesDto GetCapabilities() {
        var connected = _guiderService.GetInfo().Connected;
        GuidingAutoTuneSession? session;
        lock (_gate) session = _session;
        var locks = (_mainCameraValidator is null ? MainCameraValidationLock : Array.Empty<string>())
            .Concat(_telescope is null ? GuideRateAdapterLock : Array.Empty<string>())
            .ToArray();
        return new GuidingAutoTuneCapabilitiesDto(true, connected, _collector.Count > 0,
            connected && _collector.Count >= 8,
            connected && session?.State == GuidingAutoTuneState.Proposed
                && session.BestCandidate is { Accepted: true, Metrics.CriticalRegression: false },
            _telescope is not null,
            locks);
    }

    public GuidingAutoTuneStatusDto GetStatus() {
        lock (_gate) return ToDto(_session);
    }

    public GuidingAutoTuneStatusDto GetStatus(Guid sessionId) => ToDto(LoadSession(sessionId));

    public GuidingAutoTuneReportDto GetReport() {
        GuidingAutoTuneSession? session;
        lock (_gate) session = _session;
        return BuildReport(session);
    }

    public GuidingAutoTuneReportDto GetReport(Guid sessionId) => BuildReport(LoadSession(sessionId));

    private GuidingAutoTuneReportDto BuildReport(GuidingAutoTuneSession? session) {
        if (session is null)
            return new GuidingAutoTuneReportDto(Guid.Empty, "text/markdown", "# Guiding auto-tune\n\nNo session.\n", DateTimeOffset.UtcNow);
        var lines = new List<string> {
            $"# Guiding auto-tune report",
            $"\n- Session: `{session.Id:D}`",
            $"- State: `{session.State}`",
            $"- Observed behavior: `{session.BehaviorClass?.ToString() ?? "Unknown"}`",
            $"- Confidence: `{session.BehaviorConfidence?.ToString("P1", CultureInfo.InvariantCulture) ?? "n/a"}`",
            $"- Telemetry samples: `{(session.Id == _session?.Id ? _collector.Count : session.CharacterizationTelemetry?.Samples.Count ?? 0)}`",
            $"- Baseline score: `{session.BaselineResult?.Score.Total.ToString("F4", CultureInfo.InvariantCulture) ?? "n/a"}`",
            $"- DEC reversal delay: `{session.BaselineResult?.Metrics.DeclinationReversalDelayMilliseconds?.ToString("F0", CultureInfo.InvariantCulture) ?? "n/a"} ms`",
            "\n## Warnings",
        };
        if (session.Characterization is { } characterization) {
            lines.Add($"- Guide image scale: {characterization.GuidePixelScaleArcsecPerPixel?.ToString("F4", CultureInfo.InvariantCulture) ?? "unknown"} arcsec/pixel");
            lines.Add($"- Dominant periods: {(characterization.DominantPeriodsSeconds.Count == 0 ? "none" : string.Join(", ", characterization.DominantPeriodsSeconds.Select(p => p.ToString("F1", CultureInfo.InvariantCulture) + " s")))}");
            lines.Add($"- RA slope p95/p99: {characterization.RightAscension.Slope95PerSecond.ToString("F4", CultureInfo.InvariantCulture)}/{characterization.RightAscension.Slope99PerSecond.ToString("F4", CultureInfo.InvariantCulture)} arcsec/s");
            lines.Add($"- DEC slope p95/p99: {characterization.Declination.Slope95PerSecond.ToString("F4", CultureInfo.InvariantCulture)}/{characterization.Declination.Slope99PerSecond.ToString("F4", CultureInfo.InvariantCulture)} arcsec/s");
            lines.Add($"- RA/DEC drift: {characterization.RightAscension.DriftArcsecPerSecond.ToString("F4", CultureInfo.InvariantCulture)}/{characterization.Declination.DriftArcsecPerSecond.ToString("F4", CultureInfo.InvariantCulture)} arcsec/s");
        }
        if (session.ExposureProbes is { Count: > 0 }) {
            lines.Add("\n## Exposure probes");
            lines.Add("| Exposure (ms) | Stable frames | Star loss | Cadence | Multi-star | Accepted | Reason |");
            lines.Add("|---:|---:|---:|---:|---:|:---:|:---|");
            foreach (var probe in session.ExposureProbes)
                lines.Add($"| {probe.ExposureMilliseconds} | {probe.Quality.StableStarFrames} | {probe.Quality.StarLossRate.ToString("P1", CultureInfo.InvariantCulture)} | {probe.Quality.ActualCadenceRatio.ToString("P0", CultureInfo.InvariantCulture)} | {probe.Quality.MedianMultiStarCount.ToString("F1", CultureInfo.InvariantCulture)} | {probe.Quality.MeetsMinimumQuality} | {probe.RejectionReason ?? ""} |");
        }
        if (session.BaselineResult?.Metrics is { } baselineMetrics) {
            lines.Add($"- Observed guide-response latency: {baselineMetrics.ObservedResponseLatencyMilliseconds?.ToString("F0", CultureInfo.InvariantCulture) ?? "n/a"} ms");
            lines.Add($"- Effective correction authority: {baselineMetrics.EffectiveCorrectionAuthorityArcsecPerSecond?.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a"} arcsec/s");
        }
        if (session.Snapshot is { } snapshot)
            lines.Add($"- Snapshot hash: {snapshot.SnapshotHash}");
        if (session.Snapshot?.CalibrationQuality is { } calibrationQuality)
            lines.Add($"- Calibration: {(calibrationQuality.IsValid ? "valid" : "invalid")}; axes error {calibrationQuality.OrthogonalityErrorDegrees?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} deg");
        lines.AddRange(session.Warnings.Count == 0 ? ReportNoWarnings : session.Warnings.Select(w => $"- {w}"));
        lines.Add("\n## Candidate results");
        if (session.CandidateResults is not { Count: > 0 }) {
            lines.Add("- No tested candidates.");
        } else {
            lines.Add("| Exposure (ms) | RA MinMo (px) | RA Aggression | Max RA (ms) | DEC mode | Score | RMS | Confidence | Accepted |");
            lines.Add("|---:|---:|---:|---:|:---|---:|---:|---:|:---:|");
            foreach (var result in session.CandidateResults)
                lines.Add($"| {result.Settings.ExposureMilliseconds} | {result.Settings.RaMinimumMovePixels.ToString("F3", CultureInfo.InvariantCulture)} | {result.Settings.RaAggressiveness.ToString("F2", CultureInfo.InvariantCulture)} | {result.Settings.RaMaximumPulseMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} | {result.Settings.DecGuideMode} | {result.Score.Total.ToString("F4", CultureInfo.InvariantCulture)} | {result.Metrics.RobustTotalRms.ToString("F4", CultureInfo.InvariantCulture)} | {result.ImprovementConfidence.ToString("P0", CultureInfo.InvariantCulture)} | {result.Accepted} |");
        }
        return new GuidingAutoTuneReportDto(session.Id, "text/markdown", string.Join(Environment.NewLine, lines) + Environment.NewLine, DateTimeOffset.UtcNow);
    }

    public async Task<GuidingAutoTuneStatusDto> StartAsync(GuidingAutoTuneStartRequestDto request, CancellationToken ct) {
        lock (_gate) {
            if (_startInProgress)
                throw new InvalidOperationException("guiding auto-tune start already running");
            _startInProgress = true;
        }
        try {
            return await StartCoreAsync(request, ct).ConfigureAwait(false);
        } finally {
            lock (_gate) _startInProgress = false;
        }
    }

    private async Task<GuidingAutoTuneStatusDto> StartCoreAsync(GuidingAutoTuneStartRequestDto request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (_activeRuns?.HasActive == true)
            throw new InvalidOperationException("an imaging sequence is active");
        if (!double.IsFinite(request.MinimumAltitudeDegrees)
            || request.MinimumAltitudeDegrees is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(request), request,
                "MinimumAltitudeDegrees must be finite and within -90..90 degrees.");
        ValidateOptionalPositive(request.GuidePixelScaleArcsecPerPixel, "GuidePixelScaleArcsecPerPixel");
        ValidateOptionalPositive(request.MainPixelScaleArcsecPerPixel, "MainPixelScaleArcsecPerPixel");
        if (!double.IsFinite(request.GuideRateArcsecPerSecond) || request.GuideRateArcsecPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), request,
                "GuideRateArcsecPerSecond must be finite and positive.");
        if (request.MaximumSamples is < 8)
            throw new ArgumentOutOfRangeException(nameof(request), request,
                "MaximumSamples must be at least 8.");
        var profilePolicy = _profiles.GetPhd2Settings();
        if (!profilePolicy.AutoTuneEnabled)
            throw new InvalidOperationException("guiding auto-tune is disabled by the active profile");
        if (!double.IsFinite(profilePolicy.AutoTuneMinimumAltitudeDegrees)
            || profilePolicy.AutoTuneMinimumAltitudeDegrees is < -90 or > 90)
            throw new InvalidOperationException("active profile has an invalid auto-tune altitude policy");
        if (profilePolicy.AutoTuneMaximumCandidates is < 1 or > 12
            || profilePolicy.AutoTuneMaximumSessionMinutes is < 1 or > 180
            || !double.IsFinite(profilePolicy.AutoTuneMinimumAutomaticImprovementPercent)
            || profilePolicy.AutoTuneMinimumAutomaticImprovementPercent < 0
            || profilePolicy.AutoTuneMinimumAutomaticImprovementPercent > 100)
            throw new InvalidOperationException("active profile has invalid auto-tune limits");
        if (!double.IsFinite(request.MainCameraValidationExposureSeconds)
            || request.MainCameraValidationExposureSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), request,
                "MainCameraValidationExposureSeconds must be finite and positive.");
        if (request.MainCameraValidationBinning is < 1 or > 4
            || request.MainCameraValidationFrames is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(request), request,
                "Main-camera validation binning must be 1..4 and frame count 1..8.");
        var maximumCandidates = Math.Clamp(request.MaximumCandidates, 1,
            Math.Clamp(profilePolicy.AutoTuneMaximumCandidates, 1, 12));
        var maximumSessionMinutes = Math.Clamp(request.MaximumSessionMinutes, 1,
            Math.Clamp(profilePolicy.AutoTuneMaximumSessionMinutes, 1, 180));
        var minimumAltitudeDegrees = Math.Max(request.MinimumAltitudeDegrees,
            profilePolicy.AutoTuneMinimumAltitudeDegrees);
        lock (_gate) {
            if (_session is { State: not GuidingAutoTuneState.Completed and not GuidingAutoTuneState.Failed and not GuidingAutoTuneState.RolledBack })
                throw new InvalidOperationException("guiding auto-tune session already running");
        }
        var info = _guiderService.GetInfo();
        if (!info.Connected) throw new InvalidOperationException("guider is not connected");
        if (!request.DryRun && _telescope is null)
            throw new InvalidOperationException("mount Alpaca adapter is unavailable");
        var preflightWarnings = new List<string>();
        if (_safetyMonitor is null) {
            if (!request.DryRun)
                throw new InvalidOperationException("safety monitor is required for live auto-tune");
            preflightWarnings.AddRange(SafetyMonitorWarnings);
        } else {
            var safety = await _safetyMonitor.GetAsync(ct).ConfigureAwait(false);
            if (safety?.State == EquipmentConnectionState.Connected && !safety.Safe)
                throw new InvalidOperationException("safety monitor reports unsafe conditions");
            if (safety is null || safety.State != EquipmentConnectionState.Connected) {
                if (!request.DryRun)
                    throw new InvalidOperationException("safety monitor must be connected for live auto-tune");
                preflightWarnings.AddRange(SafetyMonitorWarnings);
            }
        }
        TelescopeDto? telescope = null;
        double? mountAltitudeDegrees = null;
        if (_telescope is not null) {
            telescope = await _telescope.GetAsync(ct).ConfigureAwait(false);
            if (telescope is null || telescope.State != EquipmentConnectionState.Connected)
                throw new InvalidOperationException("mount is not connected");
            if (telescope.Capabilities?.CanPulseGuide != true)
                throw new InvalidOperationException("mount does not support pulse guiding");
            if (telescope.Runtime.Parked || string.Equals(telescope.Runtime.State, "slewing", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("mount is parked or slewing");
            if (!telescope.Runtime.Tracking)
                throw new InvalidOperationException("mount is not tracking");
            if (telescope.Runtime.RightAscensionHours is { } rightAscensionHours
                && telescope.Runtime.DeclinationDegrees is { } declinationDegrees) {
                var site = _profiles.GetSiteSettings();
                if (double.IsFinite(site.LatitudeDeg) && Math.Abs(site.LatitudeDeg) <= 90
                    && double.IsFinite(site.LongitudeDeg) && Math.Abs(site.LongitudeDeg) <= 180) {
                    var lstDegrees = SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, site.LongitudeDeg);
                    var hourAngleDegrees = lstDegrees - rightAscensionHours * 15;
                    mountAltitudeDegrees = SiteAstrometry.AltitudeFromHourAngleDeg(
                        declinationDegrees, site.LatitudeDeg, hourAngleDegrees);
                    var distanceToMeridianDegrees = Math.Abs(((hourAngleDegrees + 540) % 360) - 180);
                    if (distanceToMeridianDegrees <= 2.5) {
                        if (!request.DryRun)
                            throw new InvalidOperationException("mount is too close to the meridian for auto-tune");
                        preflightWarnings.AddRange(MeridianWarnings);
                    }
                }
            }
            if (!request.DryRun) {
                if (mountAltitudeDegrees is not { } altitude)
                    throw new InvalidOperationException("mount altitude is unavailable");
                if (altitude < minimumAltitudeDegrees)
                    throw new InvalidOperationException("mount altitude is below the auto-tune safety minimum");
            }
            if (!string.IsNullOrWhiteSpace(telescope.Runtime.TrackingRate)
                && !string.Equals(telescope.Runtime.TrackingRate, "Sidereal", StringComparison.OrdinalIgnoreCase))
                preflightWarnings.AddRange(TrackingRateWarnings);
        }
        var profileGuider = _profiles.GetPhd2Settings();
        var profileScale = profileGuider.GuidePixelSize > 0 && profileGuider.GuideFocalLength > 0
            ? GuidingPlanner.GuideScaleArcsecPerPixel(profileGuider.GuidePixelSize, 1, profileGuider.GuideFocalLength)
            : 0;
        var scale = request.GuidePixelScaleArcsecPerPixel
            ?? (info.PixelScale > 0 ? info.PixelScale : profileScale);
        var optics = _profiles.GetOpticsSettings();
        var profileMainScale = optics.PixelSizeUm > 0 && optics.FocalLengthMm > 0
            ? GuidingPlanner.GuideScaleArcsecPerPixel(optics.PixelSizeUm, 1,
                optics.FocalLengthMm * Math.Max(optics.ReducerFactor, double.Epsilon))
            : (double?)null;
        var mainScale = request.MainPixelScaleArcsecPerPixel ?? profileMainScale;
        if (!request.DryRun && scale <= 0)
            throw new InvalidOperationException("guide image scale is required for live tuning");
        if (request.UseMainCameraValidation) {
            if (_mainCameraValidator is null)
                throw new InvalidOperationException("main-camera validation is unavailable");
            if (!request.DryRun && mainScale is not > 0)
                throw new InvalidOperationException("main image scale is required for main-camera validation");
        }
        var allowGuideRateChanges = request.AllowGuideRateChanges
            && profilePolicy.AutoTuneAllowGuideRateChanges
            && telescope?.Capabilities?.CanSetGuideRates == true;
        var effectiveRequest = request with {
            Depth = string.IsNullOrWhiteSpace(request.Depth)
                ? profilePolicy.AutoTuneDefaultDepth : request.Depth,
            AllowGuideRateChanges = allowGuideRateChanges,
            AllowAlgorithmChanges = request.AllowAlgorithmChanges && profilePolicy.AutoTuneAllowAlgorithmChanges,
            ApplyAutomatically = request.ApplyAutomatically && !profilePolicy.AutoTuneRequireApplyConfirmation,
            UseMainCameraValidation = request.UseMainCameraValidation || profilePolicy.AutoTuneUseMainCameraValidation,
            MaximumCandidates = maximumCandidates,
            MaximumSessionMinutes = maximumSessionMinutes,
            MinimumAltitudeDegrees = minimumAltitudeDegrees,
        };
        if (effectiveRequest.UseMainCameraValidation) {
            if (_mainCameraValidator is null)
                throw new InvalidOperationException("main-camera validation is unavailable");
            if (!request.DryRun && mainScale is not > 0)
                throw new InvalidOperationException("main image scale is required for main-camera validation");
        }
        var window = _collector.GetWindow(request.MaximumSamples);
        if (window.Samples.Count < 8) throw new InvalidOperationException("need at least 8 accepted guide frames");
        var depth = ParseDepth(effectiveRequest.Depth);
        var mountName = telescope?.Name ?? info.Name;
        var mountPrior = MountKnowledgeBase.Find("", mountName);
        var mountCapabilities = telescope?.Capabilities;
        var fingerprint = new MountFingerprint("", mountName,
            mountCapabilities?.DriverInfo ?? "Alpaca",
            mountCapabilities?.DriverVersion ?? "",
            mountPrior?.DeclaredDriveType ?? "", null,
            mountPrior?.ExpectedPeriodicPeriodsSeconds ?? Array.Empty<double>(),
            mountCapabilities?.GuideRateRightAscensionDegreesPerSecond is { } raRate
                ? raRate * 3600 : request.GuideRateArcsecPerSecond,
            mountCapabilities?.GuideRateDeclinationDegreesPerSecond is { } decRate
                ? decRate * 3600 : request.GuideRateArcsecPerSecond,
            mountAltitudeDegrees, telescope?.Runtime.DeclinationDegrees, telescope?.Runtime.SideOfPier,
            mountCapabilities?.Description, mountCapabilities?.DriverInfo,
            mountCapabilities?.InterfaceVersion, mountCapabilities?.SupportedActions,
            telescope?.Runtime.AzimuthDegrees);
        var characterization = GuidingSignalAnalyzer.Analyze(fingerprint, window, scale > 0 ? scale : null);
        var phd = _guiderService.RequireConnectedGuider();
        var current = await ReadCurrentSettingsAsync(phd, ct).ConfigureAwait(false);
        _collector.SetContext(guidePixelScaleArcsecPerPixel: scale,
            mountRightAscensionHours: telescope?.Runtime.RightAscensionHours,
            mountDeclinationDegrees: telescope?.Runtime.DeclinationDegrees,
            mountAzimuthDegrees: telescope?.Runtime.AzimuthDegrees);
        var quality = GuidingSignalAnalyzer.AnalyzeStarQuality(window.Samples, current.ExposureMilliseconds);
        if (!request.DryRun && !quality.MeetsMinimumQuality)
            throw new InvalidOperationException("guide-star quality does not meet the auto-tune minimum");
        if (!request.DryRun && !string.Equals(phd.State, "Guiding", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("live experiments require active guiding");
        var context = new GuidingPlanningContext(characterization, await phd.GetSupportedExposureDurationsAsync(ct).ConfigureAwait(false),
            quality, scale, mainScale, MedianIntervalSeconds(window),
            ResolveGuideRateArcsecPerSecond(current, request), depth, current, allowGuideRateChanges,
            effectiveRequest.AllowAlgorithmChanges,
            RequireKnownImageScale: true,
            DeclinationReversalDelayMilliseconds: GuidingResponseAnalyzer.EstimateDeclinationReversalDelayMilliseconds(window.Samples),
            GuideRateCandidatePairs: GetGuideRateCandidatePairs(current, effectiveRequest));
        var unboundedPlan = GuidingPlanner.CreatePlan(context);
        var plan = unboundedPlan with {
            Candidates = unboundedPlan.Candidates.Take(maximumCandidates).ToArray(),
        };
        var snapshot = await CaptureSnapshotAsync(phd, current,
            telescope?.Runtime.Tracking ?? true,
            telescope?.Runtime.TrackingRate ?? "unknown", ct).ConfigureAwait(false);
        if (!request.DryRun && snapshot.CalibrationQuality?.IsValid != true)
            throw new InvalidOperationException("valid PHD2 calibration is required before live auto-tune");
        if (!request.DryRun && !snapshot.GuideOutputEnabled)
            throw new InvalidOperationException("live tuning requires guide output enabled");
        double? baselineMainEccentricity = null;
        if (effectiveRequest.UseMainCameraValidation && !request.DryRun)
            baselineMainEccentricity = await CaptureMainCameraEccentricityAsync(effectiveRequest, ct).ConfigureAwait(false);
        var baselineMetrics = GuidingScorer.CalculateMetrics(window.Samples, scale, mainScale) with {
            MainCameraEccentricity = baselineMainEccentricity,
        };
        var baselineResult = new GuidingCandidateResult(current, baselineMetrics,
            GuidingScorer.CalculateScore(baselineMetrics, baselineMainEccentricity), true, string.Empty);
        var initialState = plan.Candidates.Count == 0
            ? GuidingAutoTuneState.Failed
            : request.DryRun ? GuidingAutoTuneState.Proposed : GuidingAutoTuneState.CharacterizingUnguided;
        var session = new GuidingAutoTuneSession(Guid.NewGuid(), initialState,
            plan.Candidates.Count == 0 ? 1 : request.DryRun ? .75 : .2,
            request.DryRun ? "analysis and plan complete" : "preparing unguided characterization",
            characterization.BehaviorClass,
            characterization.Confidence, plan, null, snapshot,
            plan.Reasons.Concat(scale > 0 ? Array.Empty<string>() : ScaleWarnings)
                .Concat(request.AllowGuideRateChanges && !allowGuideRateChanges ? GuideRateAdapterLock : Array.Empty<string>())
                .Concat(preflightWarnings)
                .ToArray(),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, baselineResult,
            Array.Empty<GuidingCandidateResult>(), window, characterization);
        lock (_gate) {
            _session = session;
            _baseline = baselineResult;
        }
        await _repository.SaveAsync(session, ct).ConfigureAwait(false);
        await SaveTelemetryAsync(session.Id, "initial-baseline", window, ct).ConfigureAwait(false);
        await PublishStateAsync(WsEventCatalog.GuidingAutoTuneStarted, ct).ConfigureAwait(false);
        if (session.Warnings.Count > 0)
            await PublishWarningsAsync(session.Warnings, ct).ConfigureAwait(false);
        if (request.DryRun)
            await PublishStateAsync(WsEventCatalog.GuidingAutoTuneProposalReady, ct).ConfigureAwait(false);
        if (!request.DryRun && session.State == GuidingAutoTuneState.CharacterizingUnguided) {
            StartExperimentTask(session, effectiveRequest, current, baselineResult);
        }
        return ToDto(session);
    }

    public async Task<GuidingAutoTuneStatusDto> ApplyAsync(CancellationToken ct) {
        GuidingAutoTuneSession session;
        lock (_gate) {
            session = _session ?? throw new InvalidOperationException("no auto-tune session");
            if (session.State != GuidingAutoTuneState.Proposed || session.Plan is null || session.BestCandidate is null)
                throw new InvalidOperationException("session has no applicable proposal");
            if (!session.BestCandidate.Accepted || session.BestCandidate.Metrics.CriticalRegression)
                throw new InvalidOperationException("proposal failed candidate safety checks");
        }
        var phd = _guiderService.RequireConnectedGuider();
        try {
            await ApplySettingsAsync(phd, session.BestCandidate.Settings, session.Snapshot!, ct).ConfigureAwait(false);
            if (session.Snapshot?.GuidingActive == true)
                await StartGuidingOrThrowAsync(phd,
                    RequiresCalibration(session.BestCandidate.Settings, session.Snapshot), ct).ConfigureAwait(false);
            PersistProfileSettings(session.BestCandidate.Settings);
            UpdateState(GuidingAutoTuneState.Completed, 1, "proposal applied", session.Warnings);
            await SaveCurrentAsync(ct).ConfigureAwait(false);
            await PublishStateAsync(WsEventCatalog.GuidingAutoTuneApplied, ct).ConfigureAwait(false);
        } catch {
            try {
                await RestoreSnapshotAsync(phd, session.Snapshot!, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception restoreError) {
                RollbackFailureLog(_logger, restoreError);
                UpdateState(GuidingAutoTuneState.Failed, 1, "apply failed; restore failed",
                    session.Warnings.Concat(RestoreFailureWarnings).ToArray());
                await SaveCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            UpdateState(GuidingAutoTuneState.RolledBack, 1, "apply failed; snapshot restored", ApplyFailureWarnings);
            await SaveCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            await PublishStateAsync(WsEventCatalog.GuidingAutoTuneRolledBack, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return GetStatus();
    }

    public Task<GuidingAutoTuneStatusDto> ApplyAsync(Guid sessionId, CancellationToken ct) {
        EnsureCurrentSession(sessionId);
        return ApplyAsync(ct);
    }

    public async Task<GuidingAutoTuneStatusDto> RollbackAsync(CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var session = GetSessionOrThrow();
        var phd = _guiderService.RequireConnectedGuider();
        await RestoreSnapshotAsync(phd, session.Snapshot ?? throw new InvalidOperationException("snapshot missing"),
            CancellationToken.None).ConfigureAwait(false);
        UpdateState(GuidingAutoTuneState.RolledBack, 1, "snapshot restored", session.Warnings);
        await SaveCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        await PublishStateAsync(WsEventCatalog.GuidingAutoTuneRolledBack, CancellationToken.None).ConfigureAwait(false);
        return GetStatus();
    }

    public Task<GuidingAutoTuneStatusDto> RollbackAsync(Guid sessionId, CancellationToken ct) {
        EnsureCurrentSession(sessionId);
        return RollbackAsync(ct);
    }

    public async Task<GuidingAutoTuneStatusDto> CancelAsync(CancellationToken ct) {
        var session = GetSessionOrThrow();
        CancellationTokenSource? runCts;
        Task? runTask;
        lock (_gate) {
            runCts = _runCts;
            runTask = _runTask;
        }
        runCts?.Cancel();
        if (runTask is not null) {
            try { await runTask.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException) {
                UpdateState(GuidingAutoTuneState.Failed, 1,
                    "cancellation timed out; experiment worker still owns rollback",
                    session.Warnings.Concat(CancellationTimeoutWarnings).ToArray());
                await SaveCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                return GetStatus();
            }
            catch (OperationCanceledException) { }
        }
        UpdateState(GuidingAutoTuneState.Cancelling, session.Progress, "cancellation requested", session.Warnings);
        if (session.Snapshot is not null) {
            if (!_guiderService.GetInfo().Connected) {
                UpdateState(GuidingAutoTuneState.Failed, 1, "cancelled; reconnect guider to restore snapshot",
                    session.Warnings.Concat(DisconnectedCancelWarnings).ToArray());
                await SaveCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                return GetStatus();
            }
            var phd = _guiderService.RequireConnectedGuider();
            await RestoreSnapshotAsync(phd, session.Snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        UpdateState(GuidingAutoTuneState.RolledBack, 1, "cancelled; snapshot restored", session.Warnings);
        await SaveCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        await PublishStateAsync(WsEventCatalog.GuidingAutoTuneCancelled, CancellationToken.None).ConfigureAwait(false);
        return GetStatus();
    }

    public Task<GuidingAutoTuneStatusDto> CancelAsync(Guid sessionId, CancellationToken ct) {
        EnsureCurrentSession(sessionId);
        return CancelAsync(ct);
    }

    private void StartExperimentTask(GuidingAutoTuneSession session,
        GuidingAutoTuneStartRequestDto request, GuidingParameterSet baselineSettings,
        GuidingCandidateResult baselineResult) {
        lock (_gate) {
            if (_runTask is { IsCompleted: false })
                throw new InvalidOperationException("guiding auto-tune experiment already running");
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource(
                TimeSpan.FromMinutes(Math.Clamp(request.MaximumSessionMinutes, 1, 180)));
            var token = _runCts.Token;
            _runTask = Task.Run(() => RunExperimentsAsync(session, request, baselineSettings, baselineResult, token),
                CancellationToken.None);
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "The experiment boundary must catch every hardware, protocol, and persistence failure so rollback is attempted before the task ends.")]
    private async Task RunExperimentsAsync(GuidingAutoTuneSession initialSession,
        GuidingAutoTuneStartRequestDto request, GuidingParameterSet baselineSettings,
        GuidingCandidateResult baselineResult, CancellationToken ct) {
        try {
            var phd = _guiderService.RequireConnectedGuider();
            var exposureProbes = await ProbeExposureQualityAsync(phd, baselineSettings, request, ct)
                .ConfigureAwait(false);
            var characterization = await CaptureUnguidedAsync(phd, initialSession, request, ct).ConfigureAwait(false);
            var scale = request.GuidePixelScaleArcsecPerPixel
                ?? initialSession.Characterization?.GuidePixelScaleArcsecPerPixel;
            var fingerprint = initialSession.Characterization?.Fingerprint
                ?? new MountFingerprint("", "", "", "", "", null, Array.Empty<double>(),
                request.GuideRateArcsecPerSecond, request.GuideRateArcsecPerSecond, null, null, null);
            var observed = GuidingSignalAnalyzer.Analyze(fingerprint, characterization, scale);
            var quality = GuidingSignalAnalyzer.AnalyzeStarQuality(characterization.Samples,
                baselineSettings.ExposureMilliseconds);
            var unboundedObservedPlan = GuidingPlanner.CreatePlan(new GuidingPlanningContext(observed,
                await phd.GetSupportedExposureDurationsAsync(ct).ConfigureAwait(false), quality,
                scale ?? 0, ResolveMainPixelScale(request), MedianIntervalSeconds(characterization),
                ResolveGuideRateArcsecPerSecond(baselineSettings, request), ParseDepth(request.Depth), baselineSettings,
                request.AllowGuideRateChanges, request.AllowAlgorithmChanges, RequireKnownImageScale: true,
                DeclinationReversalDelayMilliseconds: GuidingResponseAnalyzer.EstimateDeclinationReversalDelayMilliseconds(characterization.Samples),
                GuideRateCandidatePairs: GetGuideRateCandidatePairs(baselineSettings, request)));
            var observedPlan = unboundedObservedPlan with {
                Candidates = OrderExposureCandidates(unboundedObservedPlan.Candidates,
                    GuidingSignalAnalyzer.SelectShortestReliableExposure(exposureProbes))
                    .Take(Math.Clamp(request.MaximumCandidates, 1, 12)).ToArray(),
            };
            UpdateState(GuidingAutoTuneState.AnalyzingMount, .55,
                "analyzing unguided mount motion", GetSessionOrThrow().Warnings);
            UpdateCharacterization(observed, observedPlan, characterization, exposureProbes);
            await SaveCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            await SaveTelemetryAsync(initialSession.Id, "characterization-unguided", characterization, CancellationToken.None).ConfigureAwait(false);
            await PublishStateAsync(WsEventCatalog.GuidingAutoTuneCharacterizationComplete, CancellationToken.None).ConfigureAwait(false);
            await PublishStateAsync(WsEventCatalog.GuidingAutoTuneTelemetrySummary, CancellationToken.None).ConfigureAwait(false);
            var current = GetSessionOrThrow();
            var plan = current.Plan ?? throw new InvalidOperationException("tune plan missing");
            if (plan.Candidates.Count == 0)
                throw new InvalidOperationException("unguided characterization produced no safe candidates");
            var results = new List<GuidingCandidateResult>();
            var baselineTelemetry = initialSession.CharacterizationTelemetry?.Samples ?? Array.Empty<GuidingTelemetrySample>();
            var baseline = baselineResult;
            var mainPixelScale = ResolveMainPixelScale(request);

            foreach (var candidate in plan.Candidates) {
                ct.ThrowIfCancellationRequested();
                UpdateState(GuidingAutoTuneState.ApplyingCandidate, .75,
                    $"applying candidate exposure {candidate.ExposureMilliseconds} ms", current.Warnings);
                await SaveCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                await PublishStateAsync(WsEventCatalog.GuidingAutoTuneCandidateStarted, CancellationToken.None).ConfigureAwait(false);

                var candidateEvaluation = await EvaluateSettingsAsync(phd, candidate, initialSession.Snapshot!, request,
                    mainPixelScale, baselineResult.Metrics.MainCameraEccentricity, ct)
                    .ConfigureAwait(false);

                // Interleave a nearby baseline window to reduce seeing/weather bias.
                UpdateState(GuidingAutoTuneState.RestoringBaseline, .86,
                    "measuring interleaved baseline", GetSessionOrThrow().Warnings);
                var interleavedBaseline = await EvaluateSettingsAsync(phd, baselineSettings, initialSession.Snapshot!, request,
                    mainPixelScale, baselineResult.Metrics.MainCameraEccentricity, ct)
                    .ConfigureAwait(false);
                if (interleavedBaseline.Result.Metrics.SampleCount >= 8) {
                    baseline = interleavedBaseline.Result;
                    baselineTelemetry = interleavedBaseline.Telemetry.Samples;
                }
                var confidence = GuidingScorer.EstimateImprovementConfidence(
                    baselineTelemetry, candidateEvaluation.Telemetry.Samples,
                    GetGuideScale(initialSession, request));
                var candidateResult = candidateEvaluation.Result with { ImprovementConfidence = confidence };
                results.Add(candidateResult);
                UpdateCandidateResults(results, candidateResult);
                await SaveTelemetryAsync(initialSession.Id, $"candidate-{results.Count}", candidateEvaluation.Telemetry, CancellationToken.None).ConfigureAwait(false);
                await SaveTelemetryAsync(initialSession.Id, $"baseline-{results.Count}", interleavedBaseline.Telemetry, CancellationToken.None).ConfigureAwait(false);
                UpdateBaselineResult(baseline);
                current = GetSessionOrThrow();
                await SaveCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                await PublishStateAsync(WsEventCatalog.GuidingAutoTuneCandidateComplete, CancellationToken.None).ConfigureAwait(false);
                await PublishStateAsync(WsEventCatalog.GuidingAutoTuneTelemetrySummary, CancellationToken.None).ConfigureAwait(false);
            }

            UpdateState(GuidingAutoTuneState.ValidatingWinner, .95,
                "validating candidate winner", GetSessionOrThrow().Warnings);
            var winner = results.Where(r => r.Accepted)
                .OrderBy(r => r.Score.Total)
                .FirstOrDefault(r => GuidingScorer.IsAutomaticWinner(baseline, r,
                    minimumImprovementPercent: _profiles.GetPhd2Settings().AutoTuneMinimumAutomaticImprovementPercent,
                    confidence: r.ImprovementConfidence));
            if (winner is null) {
                await RestoreSnapshotAsync(phd, initialSession.Snapshot!, CancellationToken.None).ConfigureAwait(false);
                UpdateState(GuidingAutoTuneState.Proposed, 1,
                    "experiments complete; no statistically safe winner", current.Warnings
                    .Concat(NoWinnerWarnings).ToArray());
            } else if (request.ApplyAutomatically && plan.CanAutoApply) {
                UpdateState(GuidingAutoTuneState.ApplyingWinner, .97,
                    "applying winning candidate", current.Warnings);
                await ApplySettingsAsync(phd, winner.Settings, initialSession.Snapshot!, ct).ConfigureAwait(false);
                if (initialSession.Snapshot!.GuidingActive)
                    await StartGuidingOrThrowAsync(phd,
                        RequiresCalibration(winner.Settings, initialSession.Snapshot), ct).ConfigureAwait(false);
                PersistProfileSettings(winner.Settings);
                UpdateBestCandidate(winner);
                UpdateState(GuidingAutoTuneState.Completed, 1, "winning candidate applied", current.Warnings);
            } else {
                await RestoreSnapshotAsync(phd, initialSession.Snapshot!, CancellationToken.None).ConfigureAwait(false);
                UpdateBestCandidate(winner);
                UpdateState(GuidingAutoTuneState.Proposed, 1,
                    request.ApplyAutomatically
                        ? "experiments complete; low-confidence proposal requires confirmation"
                        : "experiments complete; proposal ready", current.Warnings);
            }
            await SaveCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            await PublishStateAsync(WsEventCatalog.GuidingAutoTuneProposalReady, CancellationToken.None).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            try {
                var phd = _guiderService.RequireConnectedGuider();
                await RestoreSnapshotAsync(phd, initialSession.Snapshot!, CancellationToken.None).ConfigureAwait(false);
                UpdateState(GuidingAutoTuneState.RolledBack, 1, "experiment cancelled; snapshot restored",
                    GetSessionOrThrow().Warnings);
            } catch (Exception restoreError) {
                RollbackFailureLog(_logger, restoreError);
                UpdateState(GuidingAutoTuneState.Failed, 1,
                    "experiment cancelled; restore failed", RestoreFailureWarnings);
            }
            await SaveCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        } catch (Exception error) {
            RollbackFailureLog(_logger, error);
            try {
                var phd = _guiderService.RequireConnectedGuider();
                await RestoreSnapshotAsync(phd, initialSession.Snapshot!, CancellationToken.None).ConfigureAwait(false);
                UpdateState(GuidingAutoTuneState.RolledBack, 1, "experiment failed; snapshot restored",
                    GetSessionOrThrow().Warnings.Concat(new[] { error.Message }).ToArray());
            } catch (Exception restoreError) {
                RollbackFailureLog(_logger, restoreError);
                UpdateState(GuidingAutoTuneState.Failed, 1,
                    "experiment failed; restore failed", RestoreFailureWarnings);
            }
            await SaveCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            await PublishStateAsync(WsEventCatalog.GuidingAutoTuneFailed, CancellationToken.None).ConfigureAwait(false);
            await PublishWarningsAsync(GetSessionOrThrow().Warnings, CancellationToken.None).ConfigureAwait(false);
        } finally {
            lock (_gate) {
                _runCts?.Dispose();
                _runCts = null;
                _runTask = null;
            }
        }
    }

    private async Task<GuidingTelemetryWindow> CaptureUnguidedAsync(PHD2Guider phd,
        GuidingAutoTuneSession session, GuidingAutoTuneStartRequestDto request, CancellationToken ct) {
        UpdateState(GuidingAutoTuneState.CharacterizingUnguided, .35,
            "measuring native mount motion with guide output disabled", session.Warnings);
        await SaveCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        var outputWasEnabled = await phd.GetGuideOutputEnabledAsync(ct).ConfigureAwait(false);
        var needed = request.Depth.Equals("quick", StringComparison.OrdinalIgnoreCase) ? 20 : 40;
        var seconds = request.MaximumCharacterizationSeconds > 0
            ? Math.Clamp(request.MaximumCharacterizationSeconds, 10, 2400)
            : request.Depth.Equals("deep", StringComparison.OrdinalIgnoreCase) ? 1200
            : request.Depth.Equals("quick", StringComparison.OrdinalIgnoreCase) ? 300 : 600;
        _collector.Clear();
        _collector.SetContext(guideOutputEnabled: false);
        try {
            await phd.SetGuideOutputEnabledAsync(false, ct).ConfigureAwait(false);
            await WaitForSamplesWithSafetyAsync(phd, needed, TimeSpan.FromSeconds(seconds), ct)
                .ConfigureAwait(false);
            var window = _collector.GetWindow();
            if (window.Samples.Count < 8)
                throw new InvalidOperationException("unguided characterization produced too few guide frames");
            return window;
        } finally {
            await phd.SetGuideOutputEnabledAsync(outputWasEnabled, CancellationToken.None).ConfigureAwait(false);
            _collector.SetContext(guideOutputEnabled: outputWasEnabled);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Probe cleanup must attempt both device restores after arbitrary guider-driver failures.")]
    private async Task<IReadOnlyList<GuidingExposureProbeResult>> ProbeExposureQualityAsync(
        PHD2Guider phd, GuidingParameterSet current, GuidingAutoTuneStartRequestDto request,
        CancellationToken ct) {
        var supported = (await phd.GetSupportedExposureDurationsAsync(ct).ConfigureAwait(false))
            .Where(ms => ms > 0).Distinct().OrderBy(ms => ms).Take(8).ToArray();
        if (supported.Length == 0) return Array.Empty<GuidingExposureProbeResult>();

        var originalExposure = await phd.GetGuideExposureMillisecondsAsync(ct).ConfigureAwait(false);
        var outputWasEnabled = await phd.GetGuideOutputEnabledAsync(ct).ConfigureAwait(false);
        var samplesPerExposure = request.Depth.Equals("quick", StringComparison.OrdinalIgnoreCase) ? 12 : 20;
        var timeoutPerExposure = TimeSpan.FromSeconds(request.Depth.Equals("quick", StringComparison.OrdinalIgnoreCase) ? 45 : 90);
        var results = new List<GuidingExposureProbeResult>(supported.Length);
        Exception? restoreError = null;
        try {
            await phd.SetGuideOutputEnabledAsync(false, ct).ConfigureAwait(false);
            foreach (var exposure in supported) {
                ct.ThrowIfCancellationRequested();
                UpdateState(GuidingAutoTuneState.MeasuringBaseline, .28,
                    $"probing guide exposure {exposure} ms", GetSessionOrThrow().Warnings);
                await phd.SetGuideExposureMillisecondsAsync(exposure, ct).ConfigureAwait(false);
                _collector.Clear();
                _collector.SetContext(exposureMilliseconds: exposure,
                    isDither: false, isSettling: false, isCalibration: false,
                    guideOutputEnabled: false);
                await WaitForSamplesWithSafetyAsync(phd, samplesPerExposure, timeoutPerExposure, ct)
                    .ConfigureAwait(false);
                var window = _collector.GetWindow();
                var quality = GuidingSignalAnalyzer.AnalyzeStarQuality(window.Samples, exposure);
                var reason = quality.MeetsMinimumQuality ? null
                    : $"quality gate failed: stable={quality.StableStarFrames}, loss={quality.StarLossRate:P1}, cadence={quality.ActualCadenceRatio:P0}";
                results.Add(new GuidingExposureProbeResult(exposure, quality,
                    MedianIntervalMilliseconds(window), true, reason));
            }
        } finally {
            try {
                await phd.SetGuideExposureMillisecondsAsync(originalExposure > 0
                    ? originalExposure : current.ExposureMilliseconds, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception error) {
                restoreError = error;
            }
            try {
                await phd.SetGuideOutputEnabledAsync(outputWasEnabled, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception error) {
                restoreError ??= error;
            }
            _collector.SetContext(exposureMilliseconds: originalExposure > 0
                    ? originalExposure : current.ExposureMilliseconds,
                guideOutputEnabled: outputWasEnabled);
            if (restoreError is not null)
                RollbackFailureLog(_logger, restoreError);
        }
        if (restoreError is not null) throw restoreError;
        return results;
    }

    private async Task WaitForSamplesWithSafetyAsync(PHD2Guider phd, int minimumSamples,
        TimeSpan timeout, CancellationToken ct) {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (_collector.Count < minimumSamples) {
            ct.ThrowIfCancellationRequested();
            await CheckAutoTuneSafetyAsync(phd, ct).ConfigureAwait(false);
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException($"auto-tune did not receive {minimumSamples} guide frames within {timeout.TotalSeconds:0} seconds");
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }
    }

    private async Task WaitForDurationWithSafetyAsync(PHD2Guider phd, TimeSpan duration,
        CancellationToken ct) {
        var deadline = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < deadline) {
            ct.ThrowIfCancellationRequested();
            await CheckAutoTuneSafetyAsync(phd, ct).ConfigureAwait(false);
            var remaining = deadline - DateTimeOffset.UtcNow;
            await Task.Delay(remaining > TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1) : remaining, ct).ConfigureAwait(false);
        }
    }

    private async Task CheckAutoTuneSafetyAsync(PHD2Guider phd, CancellationToken ct) {
        if (_activeRuns?.HasActive == true)
            throw new InvalidOperationException("an imaging sequence became active during auto-tune");
        if (!_guiderService.GetInfo().Connected || !phd.Connected)
            throw new InvalidOperationException("guider disconnected during auto-tune");
        if (_telescope is not null) {
            var telescope = await _telescope.GetAsync(ct).ConfigureAwait(false);
            if (telescope is null || telescope.State != EquipmentConnectionState.Connected)
                throw new InvalidOperationException("mount disconnected during auto-tune");
            if (telescope.Runtime.Parked
                || string.Equals(telescope.Runtime.State, "slewing", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("mount slewed or parked during auto-tune");
            if (!telescope.Runtime.Tracking)
                throw new InvalidOperationException("mount stopped tracking during auto-tune");
            _collector.SetContext(
                mountRightAscensionHours: telescope.Runtime.RightAscensionHours,
                mountDeclinationDegrees: telescope.Runtime.DeclinationDegrees,
                mountAzimuthDegrees: telescope.Runtime.AzimuthDegrees);
        }
        if (_safetyMonitor is null)
            throw new InvalidOperationException("safety monitor disconnected during auto-tune");
        var safety = await _safetyMonitor.GetAsync(ct).ConfigureAwait(false);
        if (safety is null || safety.State != EquipmentConnectionState.Connected)
            throw new InvalidOperationException("safety monitor disconnected during auto-tune");
        if (!safety.Safe)
            throw new InvalidOperationException("safety monitor became unsafe during auto-tune");
        var recent = _collector.GetWindow(16).Samples;
        if (recent.Count >= 6 && recent.Count(s => s.StarLost) >= Math.Max(3, recent.Count / 2))
            throw new InvalidOperationException("guide star lost during auto-tune");
        var baselineRms = GetSessionOrThrow().BaselineResult?.Metrics.RobustTotalRms;
        if (baselineRms is > 0 && recent.Count >= 8) {
            var scale = recent.Select(s => s.GuidePixelScaleArcsecPerPixel)
                .FirstOrDefault(s => s is > 0) ?? 1;
            var currentRms = GuidingScorer.CalculateMetrics(recent, scale, null).RobustTotalRms;
            if (currentRms > Math.Max(baselineRms.Value * 4, scale * 8))
                throw new InvalidOperationException("guiding error exceeded auto-tune safety threshold");
        }
    }

    private static IReadOnlyList<GuidingParameterSet> OrderExposureCandidates(
        IReadOnlyList<GuidingParameterSet> candidates, int? preferredExposure) {
        if (preferredExposure is not { } preferred) return candidates;
        return candidates.OrderBy(candidate => candidate.ExposureMilliseconds == preferred ? 0 : 1)
            .ThenBy(candidate => candidate.ExposureMilliseconds).ToArray();
    }

    private static double MedianIntervalMilliseconds(GuidingTelemetryWindow window) {
        var values = window.Samples.Zip(window.Samples.Skip(1), (a, b) =>
                (b.TimestampUtc - a.TimestampUtc).TotalMilliseconds)
            .Where(value => value > 0 && double.IsFinite(value)).OrderBy(value => value).ToArray();
        return values.Length == 0 ? 0 : values[values.Length / 2];
    }

    private async Task<EvaluationResult> EvaluateSettingsAsync(PHD2Guider phd,
        GuidingParameterSet settings, GuidingSettingsSnapshot snapshot,
        GuidingAutoTuneStartRequestDto request, double? mainPixelScaleArcsecPerPixel,
        double? baselineMainEccentricity, CancellationToken ct) {
        await ApplySettingsAsync(phd, settings, snapshot, ct).ConfigureAwait(false);
        var started = await phd.StartGuiding(RequiresCalibration(settings, snapshot),
            new Progress<OpenAstroAra.Core.Model.ApplicationStatus>(), ct).ConfigureAwait(false);
        if (!started) throw new InvalidOperationException("candidate guiding did not start");
        UpdateState(GuidingAutoTuneState.SettlingCandidate, .8,
            $"settling candidate exposure {settings.ExposureMilliseconds} ms", GetSessionOrThrow().Warnings);
        _collector.Clear();
        _collector.SetContext(exposureMilliseconds: settings.ExposureMilliseconds,
            isDither: false, isSettling: true, isCalibration: false, guideOutputEnabled: true);
        await WaitForDurationWithSafetyAsync(phd,
            TimeSpan.FromSeconds(Math.Clamp(request.StabilizationSeconds, 1, 300)), ct)
            .ConfigureAwait(false);
        _collector.Clear();
        _collector.SetContext(isSettling: false);
        UpdateState(GuidingAutoTuneState.EvaluatingCandidate, .9,
            $"evaluating candidate exposure {settings.ExposureMilliseconds} ms", GetSessionOrThrow().Warnings);
        var plannedEvaluationSeconds = GetSessionOrThrow().Plan?.ExpectedEvaluationSeconds ?? 0;
        var evaluationSeconds = Math.Max(request.EvaluationSeconds, plannedEvaluationSeconds);
        await WaitForDurationWithSafetyAsync(phd,
            TimeSpan.FromSeconds(Math.Clamp(evaluationSeconds, 10, 1800)), ct)
            .ConfigureAwait(false);
        var window = _collector.GetWindow();
        var scale = GetSessionOrThrow().CharacterizationTelemetry?.Samples
            .Select(s => s.GuidePixelScaleArcsecPerPixel).FirstOrDefault(s => s is > 0) ?? 1;
        var mainEccentricity = request.UseMainCameraValidation
            ? await CaptureMainCameraEccentricityAsync(request, ct).ConfigureAwait(false)
            : (double?)null;
        var metrics = GuidingScorer.CalculateMetrics(window.Samples, scale,
            mainPixelScaleArcsecPerPixel) with {
                MainCameraEccentricity = mainEccentricity,
            };
        if (baselineMainEccentricity is { } baselineEccentricity
            && mainEccentricity is { } candidateEccentricity
            && candidateEccentricity > baselineEccentricity + MainImageCriticalEccentricityIncrease)
            metrics = metrics with { CriticalRegression = true };
        var score = GuidingScorer.CalculateScore(metrics, baselineMainEccentricity);
        return new EvaluationResult(new GuidingCandidateResult(settings, metrics, score,
            metrics.SampleCount >= 8 && !metrics.CriticalRegression, metrics.SampleCount < 8 ? "too few evaluation frames" : string.Empty), window);
    }

    private Task<double> CaptureMainCameraEccentricityAsync(GuidingAutoTuneStartRequestDto request,
        CancellationToken ct) => _mainCameraValidator is null
        ? throw new InvalidOperationException("main-camera validation is unavailable")
        : _mainCameraValidator.CaptureMedianEccentricityAsync(request.MainCameraValidationExposureSeconds,
            request.MainCameraValidationBinning, request.MainCameraValidationFrames, ct);

    private double? ResolveMainPixelScale(GuidingAutoTuneStartRequestDto request) {
        if (request.MainPixelScaleArcsecPerPixel is { } requested && requested > 0)
            return requested;
        var optics = _profiles.GetOpticsSettings();
        return optics.PixelSizeUm > 0 && optics.FocalLengthMm > 0
            ? GuidingPlanner.GuideScaleArcsecPerPixel(optics.PixelSizeUm, 1,
                optics.FocalLengthMm * Math.Max(optics.ReducerFactor, double.Epsilon))
            : null;
    }

    private void UpdateCandidateResults(IReadOnlyList<GuidingCandidateResult> results, GuidingCandidateResult latest) {
        lock (_gate) {
            if (_session is null) return;
            _session = _session with {
                CandidateResults = results.ToArray(),
                BestCandidate = results.Where(r => r.Accepted).OrderBy(r => r.Score.Total).FirstOrDefault(),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
        }
    }

    private void UpdateBaselineResult(GuidingCandidateResult baseline) {
        lock (_gate) {
            if (_session is null) return;
            _session = _session with { BaselineResult = baseline, UpdatedAtUtc = DateTimeOffset.UtcNow };
            _baseline = baseline;
        }
    }

    private void UpdateBestCandidate(GuidingCandidateResult winner) {
        lock (_gate) {
            if (_session is null) return;
            _session = _session with { BestCandidate = winner, UpdatedAtUtc = DateTimeOffset.UtcNow };
        }
    }

    private void UpdateCharacterization(MountCharacterization characterization,
        GuidingTunePlan plan, GuidingTelemetryWindow window,
        IReadOnlyList<GuidingExposureProbeResult>? exposureProbes = null) {
        lock (_gate) {
            if (_session is null) return;
            _session = _session with {
                BehaviorClass = characterization.BehaviorClass,
                BehaviorConfidence = characterization.Confidence,
                Characterization = characterization,
                Plan = plan,
                CharacterizationTelemetry = window,
                ExposureProbes = exposureProbes ?? _session.ExposureProbes,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
        }
    }

    private async Task<GuidingParameterSet> ReadCurrentSettingsAsync(PHD2Guider phd, CancellationToken ct) {
        var settings = _profiles.GetPhd2Settings();
        var exposure = await phd.GetGuideExposureMillisecondsAsync(ct).ConfigureAwait(false);
        var raAlgorithm = await phd.GetAlgorithmAsync("ra", ct).ConfigureAwait(false);
        var decAlgorithm = await phd.GetAlgorithmAsync("dec", ct).ConfigureAwait(false);
        var raParams = await phd.GetAlgorithmParametersAsync("ra", ct).ConfigureAwait(false);
        var decParams = await phd.GetAlgorithmParametersAsync("dec", ct).ConfigureAwait(false);
        var limits = await phd.GetGuideLimitsAsync(ct).ConfigureAwait(false);
        var telescope = _telescope is null ? null : await _telescope.GetAsync(ct).ConfigureAwait(false);
        var mountRaRate = telescope?.Capabilities?.GuideRateRightAscensionDegreesPerSecond;
        var mountDecRate = telescope?.Capabilities?.GuideRateDeclinationDegreesPerSecond;
        var additional = raParams.Concat(decParams.Select(p => new KeyValuePair<string, double>("dec." + p.Key, p.Value)))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return new GuidingParameterSet(exposure > 0 ? exposure : 1000, FindParam(raParams, "minMove", settings.MinimumMove),
            FindParam(decParams, "minMove", settings.MinimumMove), FindParam(raParams, "aggression", settings.RaAggressiveness),
            FindParam(decParams, "aggression", settings.DecAggressiveness), limits.RaMaximumPulseMilliseconds,
            limits.DecMaximumPulseMilliseconds, raAlgorithm, decAlgorithm,
            await phd.GetDecGuideModeAsync(ct).ConfigureAwait(false), FindParam(raParams, "hysteresis", .1), additional,
            mountRaRate, mountDecRate);
    }

    private static async Task<GuidingSettingsSnapshot> CaptureSnapshotAsync(PHD2Guider phd,
        GuidingParameterSet current, bool trackingEnabled, string trackingRate, CancellationToken ct) {
        var output = await phd.GetGuideOutputEnabledAsync(ct).ConfigureAwait(false);
        var calibration = await phd.GetCalibrationJsonAsync(ct).ConfigureAwait(false);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(current)));
        return new GuidingSettingsSnapshot(current, output, trackingEnabled, trackingRate, "active", DateTimeOffset.UtcNow, hash,
            current.RaAlgorithm, current.DecAlgorithm, current.AdditionalParameters ?? new Dictionary<string, double>(), calibration,
            string.Equals(phd.State, "Guiding", StringComparison.OrdinalIgnoreCase),
            current.GuideRateRightAscensionDegreesPerSecond,
            current.GuideRateDeclinationDegreesPerSecond,
            GuidingCalibrationAnalyzer.Analyze(calibration));
    }

    private static bool RequiresCalibration(GuidingParameterSet settings, GuidingSettingsSnapshot snapshot) =>
        settings.GuideRateRightAscensionDegreesPerSecond is { } settingsRa
        && settings.GuideRateDeclinationDegreesPerSecond is { } settingsDec
        && snapshot.MountGuideRateRightAscensionDegreesPerSecond is { } snapshotRa
        && snapshot.MountGuideRateDeclinationDegreesPerSecond is { } snapshotDec
        && (Math.Abs(settingsRa - snapshotRa) > 1e-9 || Math.Abs(settingsDec - snapshotDec) > 1e-9);

    private async Task ApplySettingsAsync(PHD2Guider phd, GuidingParameterSet settings,
        GuidingSettingsSnapshot snapshot, CancellationToken ct) {
        await phd.StopGuiding(ct).ConfigureAwait(false);
        await phd.SetGuideExposureMillisecondsAsync(settings.ExposureMilliseconds, ct).ConfigureAwait(false);
        await phd.SetGuideLimitsAsync(settings.RaMaximumPulseMilliseconds, settings.DecMaximumPulseMilliseconds, ct).ConfigureAwait(false);
        await phd.SetAlgorithmAsync("ra", settings.RaAlgorithm, ct).ConfigureAwait(false);
        await phd.SetAlgorithmAsync("dec", settings.DecAlgorithm, ct).ConfigureAwait(false);
        var guideRateChanged = false;
        if (_telescope is not null && settings.GuideRateRightAscensionDegreesPerSecond is { } raRate
            && settings.GuideRateDeclinationDegreesPerSecond is { } decRate
            )
        {
            var current = await _telescope.GetAsync(ct).ConfigureAwait(false);
            var capabilities = current?.Capabilities;
            guideRateChanged = capabilities?.GuideRateRightAscensionDegreesPerSecond is not { } currentRa
                || capabilities.GuideRateDeclinationDegreesPerSecond is not { } currentDec
            || Math.Abs(raRate - currentRa) > 1e-9
            || Math.Abs(decRate - currentDec) > 1e-9;
            if (guideRateChanged) {
                await _telescope.SetGuideRatesAsync(raRate, decRate, ct).ConfigureAwait(false);
                await VerifyGuideRatesAsync(raRate, decRate, ct).ConfigureAwait(false);
            }
        }
        if (guideRateChanged && !await phd.ClearCalibration(ct).ConfigureAwait(false))
            throw new InvalidOperationException("guide-rate change could not clear calibration");
        await phd.SetDecGuideModeAsync(settings.DecGuideMode, ct).ConfigureAwait(false);
        await SetParamIfSupported(phd, "ra", "minMove", settings.RaMinimumMovePixels, ct).ConfigureAwait(false);
        await SetParamIfSupported(phd, "dec", "minMove", settings.DecMinimumMovePixels, ct).ConfigureAwait(false);
        await SetParamIfSupported(phd, "ra", "aggression", settings.RaAggressiveness, ct).ConfigureAwait(false);
        await SetParamIfSupported(phd, "dec", "aggression", settings.DecAggressiveness, ct).ConfigureAwait(false);
        await phd.SetGuideOutputEnabledAsync(snapshot.GuideOutputEnabled, ct).ConfigureAwait(false);
        _collector.SetContext(parameterSnapshotHash: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(settings))));
        await VerifySettingsReadbackAsync(phd, settings, snapshot.GuideOutputEnabled, ct).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Transactional rollback must continue restoring every independent setting after arbitrary driver failures.")]
    private async Task RestoreSnapshotAsync(PHD2Guider phd, GuidingSettingsSnapshot snapshot, CancellationToken ct) {
        var operations = new List<Func<Task>> {
            () => phd.StopGuiding(ct),
            () => phd.SetGuideExposureMillisecondsAsync(snapshot.Settings.ExposureMilliseconds, ct),
            () => phd.SetGuideLimitsAsync(snapshot.Settings.RaMaximumPulseMilliseconds,
                snapshot.Settings.DecMaximumPulseMilliseconds, ct),
            () => phd.SetAlgorithmAsync("ra", snapshot.RightAscensionAlgorithm, ct),
            () => phd.SetAlgorithmAsync("dec", snapshot.DeclinationAlgorithm, ct),
        };
        var guideRateChanged = false;
        if (_telescope is not null && snapshot.MountGuideRateRightAscensionDegreesPerSecond is { } raRate
            && snapshot.MountGuideRateDeclinationDegreesPerSecond is { } decRate)
        {
            try {
                var before = await _telescope.GetAsync(ct).ConfigureAwait(false);
                var capabilities = before?.Capabilities;
                if (capabilities?.CanSetGuideRates == false) {
                    // Tuning cannot mutate this mount's guide rate. Do not report
                    // restore failure for a read-only capability.
                    guideRateChanged = false;
                } else {
                    guideRateChanged = capabilities?.GuideRateRightAscensionDegreesPerSecond is not { }
                        || capabilities.GuideRateDeclinationDegreesPerSecond is not { }
                        || Math.Abs(capabilities.GuideRateRightAscensionDegreesPerSecond.Value - raRate) > 1e-9
                        || Math.Abs(capabilities.GuideRateDeclinationDegreesPerSecond.Value - decRate) > 1e-9;
                }
            } catch (Exception error) {
                operations.Add(() => Task.FromException(error));
                guideRateChanged = true;
            }
            if (guideRateChanged)
                operations.Add(async () => {
                    await _telescope.SetGuideRatesAsync(raRate, decRate, ct).ConfigureAwait(false);
                    await VerifyGuideRatesAsync(raRate, decRate, ct).ConfigureAwait(false);
                });
        }
        if (guideRateChanged)
            operations.Add(() => phd.ClearCalibration(ct));
        if (_telescope is not null)
            operations.Add(async () => {
                var current = await _telescope.GetAsync(ct).ConfigureAwait(false);
                if (current?.Runtime.Tracking != snapshot.TrackingEnabled)
                    await _telescope.SetTrackingAsync(snapshot.TrackingEnabled, ct).ConfigureAwait(false);
                var restored = await _telescope.GetAsync(ct).ConfigureAwait(false);
                if (restored?.Runtime.Tracking != snapshot.TrackingEnabled)
                    throw new InvalidOperationException("telescope tracking readback mismatch during snapshot restore");
            });
        operations.Add(() => phd.SetDecGuideModeAsync(snapshot.Settings.DecGuideMode, ct));
        foreach (var parameter in (snapshot.AlgorithmParameters ?? new Dictionary<string, double>())
            .Where(p => !p.Key.StartsWith("dec.", StringComparison.OrdinalIgnoreCase)))
            operations.Add(() => SetParamIfSupported(phd, "ra", parameter.Key, parameter.Value, ct));
        foreach (var parameter in (snapshot.AlgorithmParameters ?? new Dictionary<string, double>())
            .Where(p => p.Key.StartsWith("dec.", StringComparison.OrdinalIgnoreCase)))
            operations.Add(() => SetParamIfSupported(phd, "dec", parameter.Key[4..], parameter.Value, ct));
        operations.Add(() => phd.SetGuideOutputEnabledAsync(snapshot.GuideOutputEnabled, ct));
        if (snapshot.GuidingActive)
            operations.Add(async () => {
                if (!await phd.StartGuiding(guideRateChanged,
                    new Progress<OpenAstroAra.Core.Model.ApplicationStatus>(), ct).ConfigureAwait(false))
                    throw new InvalidOperationException("PHD2 did not restart guiding during snapshot restore");
            });
        operations.Add(() => VerifySettingsReadbackAsync(phd, snapshot.Settings,
            snapshot.GuideOutputEnabled, ct));
        await GuidingRollbackTransaction.ExecuteAllAsync(operations).ConfigureAwait(false);
        _profiles.PutPhd2Settings(_profiles.GetPhd2Settings() with {
            GuideExposureMilliseconds = snapshot.Settings.ExposureMilliseconds,
            RaAggressiveness = snapshot.Settings.RaAggressiveness,
            DecAggressiveness = snapshot.Settings.DecAggressiveness,
            MinimumMove = snapshot.Settings.RaMinimumMovePixels,
            DecGuideMode = snapshot.Settings.DecGuideMode,
        });
    }

    private void PersistProfileSettings(GuidingParameterSet settings) {
        _profiles.PutPhd2Settings(_profiles.GetPhd2Settings() with {
            GuideExposureMilliseconds = settings.ExposureMilliseconds,
            RaAggressiveness = settings.RaAggressiveness,
            DecAggressiveness = settings.DecAggressiveness,
            MinimumMove = settings.RaMinimumMovePixels,
            DecGuideMode = settings.DecGuideMode.ToLowerInvariant(),
        });
    }

    private static async Task SetParamIfSupported(PHD2Guider phd, string axis, string parameter, double value, CancellationToken ct) {
        var available = await phd.GetAlgorithmParametersAsync(axis, ct).ConfigureAwait(false);
        var wanted = NormalizeParameterName(parameter);
        var actual = available.Keys.FirstOrDefault(name => NormalizeParameterName(name) == wanted);
        if (actual is null && parameter.Equals("aggression", StringComparison.OrdinalIgnoreCase))
            actual = available.Keys.FirstOrDefault(name => NormalizeParameterName(name) == "aggressiveness");
        if (actual is not null) {
            await phd.SetAlgorithmParameterAsync(axis, actual, value, ct).ConfigureAwait(false);
            var readback = await phd.GetAlgorithmParametersAsync(axis, ct).ConfigureAwait(false);
            if (!readback.TryGetValue(actual, out var readbackValue) || Math.Abs(readbackValue - value) > 1e-6)
                throw new InvalidOperationException($"PHD2 algorithm parameter readback mismatch: {axis}.{actual}");
        }
    }

    private static async Task StartGuidingOrThrowAsync(PHD2Guider phd, bool forceCalibration,
        CancellationToken ct) {
        if (!await phd.StartGuiding(forceCalibration,
            new Progress<OpenAstroAra.Core.Model.ApplicationStatus>(), ct).ConfigureAwait(false))
            throw new InvalidOperationException("PHD2 did not restart guiding after auto-tune settings apply");
    }

    private async Task VerifyGuideRatesAsync(double expectedRa, double expectedDec, CancellationToken ct) {
        if (_telescope is null) return;
        var telescope = await _telescope.GetAsync(ct).ConfigureAwait(false);
        var capabilities = telescope?.Capabilities;
        if (capabilities?.GuideRateRightAscensionDegreesPerSecond is not { } actualRa
            || capabilities.GuideRateDeclinationDegreesPerSecond is not { } actualDec
            || Math.Abs(actualRa - expectedRa) > 1e-9
            || Math.Abs(actualDec - expectedDec) > 1e-9)
            throw new InvalidOperationException("Alpaca guide-rate readback mismatch");
    }

    private static async Task VerifySettingsReadbackAsync(PHD2Guider phd,
        GuidingParameterSet expected, bool expectedGuideOutput, CancellationToken ct) {
        var exposure = await phd.GetGuideExposureMillisecondsAsync(ct).ConfigureAwait(false);
        if (exposure != expected.ExposureMilliseconds)
            throw new InvalidOperationException("PHD2 exposure readback mismatch");
        var limits = await phd.GetGuideLimitsAsync(ct).ConfigureAwait(false);
        if (Math.Abs(limits.RaMaximumPulseMilliseconds - expected.RaMaximumPulseMilliseconds) > 1e-6
            || Math.Abs(limits.DecMaximumPulseMilliseconds - expected.DecMaximumPulseMilliseconds) > 1e-6)
            throw new InvalidOperationException("PHD2 guide-limit readback mismatch");
        var raAlgorithm = await phd.GetAlgorithmAsync("ra", ct).ConfigureAwait(false);
        var decAlgorithm = await phd.GetAlgorithmAsync("dec", ct).ConfigureAwait(false);
        if (!string.Equals(raAlgorithm, expected.RaAlgorithm, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(decAlgorithm, expected.DecAlgorithm, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PHD2 algorithm readback mismatch");
        var decMode = await phd.GetDecGuideModeAsync(ct).ConfigureAwait(false);
        if (!string.Equals(decMode, expected.DecGuideMode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PHD2 DEC-guide-mode readback mismatch");
        if (await phd.GetGuideOutputEnabledAsync(ct).ConfigureAwait(false) != expectedGuideOutput)
            throw new InvalidOperationException("PHD2 guide-output readback mismatch");
        await VerifyParamIfSupported(phd, "ra", "minMove", expected.RaMinimumMovePixels, ct).ConfigureAwait(false);
        await VerifyParamIfSupported(phd, "dec", "minMove", expected.DecMinimumMovePixels, ct).ConfigureAwait(false);
        await VerifyParamIfSupported(phd, "ra", "aggression", expected.RaAggressiveness, ct).ConfigureAwait(false);
        await VerifyParamIfSupported(phd, "dec", "aggression", expected.DecAggressiveness, ct).ConfigureAwait(false);
    }

    private static async Task VerifyParamIfSupported(PHD2Guider phd, string axis,
        string parameter, double expected, CancellationToken ct) {
        var values = await phd.GetAlgorithmParametersAsync(axis, ct).ConfigureAwait(false);
        var wanted = NormalizeParameterName(parameter);
        var actual = values.Keys.FirstOrDefault(name => NormalizeParameterName(name) == wanted);
        if (actual is null && parameter.Equals("aggression", StringComparison.OrdinalIgnoreCase))
            actual = values.Keys.FirstOrDefault(name => NormalizeParameterName(name) == "aggressiveness");
        if (actual is not null && (!values.TryGetValue(actual, out var value) || Math.Abs(value - expected) > 1e-6))
            throw new InvalidOperationException($"PHD2 algorithm parameter readback mismatch: {axis}.{actual}");
    }

    private GuidingAutoTuneSession GetSessionOrThrow() {
        lock (_gate) return _session ?? throw new InvalidOperationException("no auto-tune session");
    }

    private GuidingAutoTuneSession LoadSession(Guid sessionId) {
        if (sessionId == Guid.Empty) throw new InvalidOperationException("session id is required");
        lock (_gate) {
            if (_session?.Id == sessionId) return _session;
        }
        return _repository.Load(sessionId)
            ?? throw new InvalidOperationException("auto-tune session not found");
    }

    private void EnsureCurrentSession(Guid sessionId) {
        lock (_gate) {
            if (_session?.Id != sessionId)
                throw new InvalidOperationException("session is not the current auto-tune session");
        }
    }

    private void UpdateState(GuidingAutoTuneState state, double progress, string step, IReadOnlyList<string> warnings) {
        lock (_gate) {
            if (_session is null) return;
            _session = _session with { State = state, Progress = progress, CurrentStep = step,
                Warnings = warnings, UpdatedAtUtc = DateTimeOffset.UtcNow };
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "WebSocket phase publication is best-effort and must never interrupt persistence or rollback.")]
    private async Task SaveCurrentAsync(CancellationToken ct) {
        GuidingAutoTuneSession? current;
        lock (_gate) current = _session;
        if (current is not null) {
            await _repository.SaveAsync(current, ct).ConfigureAwait(false);
            try {
                await PublishStateAsync(WsEventCatalog.GuidingAutoTunePhaseChanged, CancellationToken.None)
                    .ConfigureAwait(false);
            } catch (Exception error) {
                PhasePublishFailureLog(_logger, error);
            }
        }
    }

    private Task SaveTelemetryAsync(Guid sessionId, string phase, GuidingTelemetryWindow window, CancellationToken ct) =>
        _profiles.GetPhd2Settings().AutoTunePersistTelemetry
            ? _repository.SaveTelemetryWindowAsync(sessionId, phase, window, ct)
            : Task.CompletedTask;

    private async Task PublishStateAsync(string eventType, CancellationToken ct) {
        var status = GetStatus();
        await PublishAsync(eventType, status, ct).ConfigureAwait(false);
    }

    private Task PublishWarningsAsync(IReadOnlyList<string> warnings, CancellationToken ct) =>
        PublishAsync(WsEventCatalog.GuidingAutoTuneWarning,
            new { session = GetStatus(), warnings }, ct);

    private async Task PublishAsync(string eventType, object value, CancellationToken ct) {
        if (_ws is null) return;
        var json = JsonSerializer.SerializeToDocument(value).RootElement.Clone();
        await _ws.PublishAsync(eventType, json, ct).ConfigureAwait(false);
    }

    private GuidingAutoTuneStatusDto ToDto(GuidingAutoTuneSession? session) {
        if (session is null) return new GuidingAutoTuneStatusDto(Guid.Empty, "idle", 0, "idle", null, null,
            _collector.Count, null, null, false, false, Array.Empty<string>(), null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        return new GuidingAutoTuneStatusDto(session.Id, session.State.ToString(), session.Progress, session.CurrentStep,
            session.BehaviorClass?.ToString(), session.BehaviorConfidence,
            session.Id == _session?.Id ? _collector.Count : session.CharacterizationTelemetry?.Samples.Count ?? 0,
            session.BaselineResult?.Score.Total ?? _baseline?.Score.Total,
            session.BestCandidate?.Score.Total, session.State == GuidingAutoTuneState.Proposed
                && session.BestCandidate is { Accepted: true, Metrics.CriticalRegression: false },
            session.Snapshot is not null, session.Warnings, session.Plan, session.BestCandidate, session.StartedAtUtc,
            session.UpdatedAtUtc, session.BaselineResult, session.CandidateResults);
    }

    private static GuidingTuneDepth ParseDepth(string value) => Enum.TryParse<GuidingTuneDepth>(value, true, out var result) ? result : GuidingTuneDepth.Standard;
    private static double FindParam(IReadOnlyDictionary<string, double> values, string name, double fallback) {
        var wanted = NormalizeParameterName(name);
        foreach (var pair in values)
            if (NormalizeParameterName(pair.Key) == wanted) return pair.Value;
        return fallback;
    }

    private static string NormalizeParameterName(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static GuidingGuideRateCandidate[]? GetGuideRateCandidatePairs(GuidingParameterSet current,
        GuidingAutoTuneStartRequestDto request) {
        if (!request.AllowGuideRateChanges
            || current.GuideRateRightAscensionDegreesPerSecond is not { } baseRa
            || current.GuideRateDeclinationDegreesPerSecond is not { } baseDec
            || baseRa <= 0 || baseDec <= 0)
            return null;
        return GuideRateFactors.Select(factor => new GuidingGuideRateCandidate(baseRa * factor, baseDec * factor)).ToArray();
    }

    private static double ResolveGuideRateArcsecPerSecond(GuidingParameterSet current,
        GuidingAutoTuneStartRequestDto request) =>
        current.GuideRateRightAscensionDegreesPerSecond is > 0
            ? current.GuideRateRightAscensionDegreesPerSecond.Value * 3600
            : request.GuideRateArcsecPerSecond;

    private static double MedianIntervalSeconds(GuidingTelemetryWindow window) {
        var intervals = window.Samples.Zip(window.Samples.Skip(1), (a, b) => (b.TimestampUtc - a.TimestampUtc).TotalSeconds)
            .Where(x => x > 0 && x < 30).OrderBy(x => x).ToArray();
        return intervals.Length == 0 ? 1 : intervals[intervals.Length / 2];
    }

    private static void ValidateOptionalPositive(double? value, string name) {
        if (value is { } number && (!double.IsFinite(number) || number <= 0))
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be finite and positive when supplied.");
    }

    private static double GetGuideScale(GuidingAutoTuneSession session, GuidingAutoTuneStartRequestDto request) =>
        request.GuidePixelScaleArcsecPerPixel
        ?? session.Characterization?.GuidePixelScaleArcsecPerPixel
        ?? session.CharacterizationTelemetry?.Samples.Select(s => s.GuidePixelScaleArcsecPerPixel)
            .FirstOrDefault(value => value is > 0)
        ?? 0;

    public void Dispose() {
        lock (_gate) {
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = null;
        }
    }
}
