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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.Sequencer.SequenceItem.FlatDevice;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §48.3 — the daemon half of <see cref="FlatPanelFlats"/> (serves
/// <see cref="IFlatCaptureExecutor"/>). One flat set: close the cover and light the panel (when a
/// CoverCalibrator is connected — a manual/EL panel user proceeds without one), solve the exposure
/// and panel brightness that put the frame at the target level (<see cref="FlatExposureSolver"/>;
/// probes are instrument readings at the flats' own gain, never persisted), remember the answer
/// for the next set of this filter, then capture the requested saved FLAT frames through the real
/// <see cref="IImagingMediator"/> pipeline (§72 FITS + catalog row, same as TakeExposure). The panel
/// light is restored off on every exit path; the cover stays closed over the optics.
/// </summary>
public sealed partial class FlatCaptureService : IFlatCaptureExecutor {

    /// <summary>Bounded wait for the panel light to report on before probing (2 s state-poll cadence upstream).</summary>
    private static readonly TimeSpan PanelOnTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The same wait when the cover has to close first (motorised covers take ~20–60 s).</summary>
    private static readonly TimeSpan CoverCloseTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Bounded wait for a brightness change to report applied (EL panels warm to a level).</summary>
    private static readonly TimeSpan BrightnessSettleTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A probe at or above this share of full scale is clipped or past the linear region.</summary>
    internal const double SaturationFraction = 0.9;

    private readonly ILogger<FlatCaptureService> _logger;
    private readonly IAnalysisFrameSource? _frames;
    private readonly IImagingMediator? _imaging;
    private readonly IFlatDeviceService? _flatDevice;
    private readonly IFlatTrainingStore? _training;
    // The filter in the light path keys the remembered solution (SwitchFilter ran before this set).
    private readonly Func<IFilterWheelMediator?>? _filterWheel;

    // Test seam: the panel-on poll sleeps through this (tests assert without real waits).
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public FlatCaptureService(
        ILogger<FlatCaptureService>? logger = null,
        IAnalysisFrameSource? frames = null,
        IImagingMediator? imaging = null,
        IFlatDeviceService? flatDevice = null,
        IFlatTrainingStore? training = null,
        Func<IFilterWheelMediator?>? filterWheel = null) {
        _logger = logger ?? NullLogger<FlatCaptureService>.Instance;
        _frames = frames;
        _imaging = imaging;
        _flatDevice = flatDevice;
        _training = training;
        _filterWheel = filterWheel;
    }

    // Thrown from a probe whose pixels exceed the camera's reported MaxADU: the driver reports the
    // sensor's native range but delivers scaled 16-bit data, so the solve restarts against 65535.
    private sealed class FullScaleMismatchException : Exception {
        public FullScaleMismatchException() { }
        public FullScaleMismatchException(string message) : base(message) { }
        public FullScaleMismatchException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <inheritdoc/>
    public async Task<bool> CaptureFlatSetAsync(FlatSetRequest request, IProgress<ApplicationStatus> progress, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TargetAdu < 0 || (request.TargetAdu == 0 && !(request.TargetPercent > 0 && request.TargetPercent < 100))
            || request.FrameCount < 1 || request.MinExposureSec <= 0 || request.MaxExposureSec < request.MinExposureSec) {
            LogBadRequest(request.TargetAdu, request.FrameCount, request.MinExposureSec, request.MaxExposureSec);
            return false;
        }
        if (_frames is null || _imaging is null) {
            LogNotWired();
            return false;
        }

        var filter = InstalledFilter();
        var trained = await ReadTrainingAsync(filter, request, token).ConfigureAwait(false);
        var device = await PanelInfoAsync(token).ConfigureAwait(false);
        var maxBrightness = device?.Runtime is { HasCalibrator: true, MaxBrightness: > 1 } r ? r.MaxBrightness : 0;
        // Start where this filter landed last time, else mid-range; an explicit request wins.
        int? startBrightness = request.Brightness > 0 ? request.Brightness
            : maxBrightness > 0 ? trained?.Brightness ?? Math.Max(1, maxBrightness / 2)
            : null;
        if (startBrightness is int sb && maxBrightness > 0) {
            startBrightness = Math.Clamp(sb, 1, maxBrightness);
        }
        var closeCover = device?.Runtime is { HasCover: true, CoverOpen: true };
        var panelLit = await TryLightPanelAsync(startBrightness ?? 0, closeCover, token).ConfigureAwait(false);
        try {
            var fullScale = _frames.MaxAdu is > 255 and <= ushort.MaxValue ? _frames.MaxAdu : ushort.MaxValue;
            FlatSolveResult solved;
            try {
                solved = await SolveAsync(request, fullScale, trained, panelLit && maxBrightness > 0 ? maxBrightness : 0,
                    startBrightness, filter, progress, token).ConfigureAwait(false);
            } catch (FullScaleMismatchException) {
                LogFullScaleMismatch(fullScale);
                fullScale = ushort.MaxValue;
                solved = await SolveAsync(request, fullScale, trained, panelLit && maxBrightness > 0 ? maxBrightness : 0,
                    startBrightness, filter, progress, token).ConfigureAwait(false);
            }
            if (solved.ExposureSec is not double exposure) {
                LogSolveFailed(filter, solved.Failure ?? "unknown", solved.Probes, solved.Level);
                return false;
            }
            var target = TargetLevel(request, fullScale);
            LogSolved(filter, exposure, solved.Brightness, solved.Level, target, solved.Probes);
            await WriteTrainingAsync(filter, request,
                new FlatTraining(exposure, maxBrightness > 0 ? solved.Brightness : null, solved.Level, target), token).ConfigureAwait(false);
            for (var n = 1; n <= request.FrameCount; n++) {
                token.ThrowIfCancellationRequested();
                var sequence = new CaptureSequence(
                    exposure, ImageTypes.FLAT, filterType: null,
                    binning: new OpenAstroAra.Core.Model.Equipment.BinningMode(1, 1), exposureCount: 1) {
                    Gain = request.Gain,
                    Offset = request.Offset,
                };
                _ = await _imaging.CaptureImage(sequence, token, progress, "Flats").ConfigureAwait(false);
                progress?.Report(new ApplicationStatus {
                    Status = $"Flat {n}/{request.FrameCount} at {exposure:0.###}s",
                });
            }
            LogSetComplete(request.FrameCount, exposure);
            return true;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogSetFailed(ex);
            return false;
        } finally {
            if (panelLit) {
                TurnPanelOffBestEffort();
            }
        }
    }

    internal static double TargetLevel(FlatSetRequest request, int fullScale) =>
        request.TargetAdu > 0 ? request.TargetAdu : fullScale * request.TargetPercent / 100.0;

    private async Task<FlatSolveResult> SolveAsync(FlatSetRequest request, int fullScale, FlatTraining? trained,
            int maxBrightness, int? startBrightness, string filter, IProgress<ApplicationStatus>? progress, CancellationToken token) {
        var target = TargetLevel(request, fullScale);
        var tolerance = Math.Abs(target * request.TolerancePct / 100.0);
        var limits = new FlatSolveLimits(
            TargetLevel: target,
            ToleranceLevel: tolerance,
            MinExposureSec: request.MinExposureSec,
            MaxExposureSec: request.MaxExposureSec,
            SaturationLevel: Math.Max(fullScale * SaturationFraction, Math.Min(fullScale, target + (2 * tolerance))),
            MaxBrightness: maxBrightness);
        // Last time's exposure, rescaled if the target moved since; else a 1 s first look.
        var start = trained is { ExposureSec: > 0, Target: > 0 } t ? t.ExposureSec * (target / t.Target) : 1.0;
        int? gain = request.Gain >= 0 ? request.Gain : null;
        int? offset = request.Offset >= 0 ? request.Offset : null;
        int? panelAt = startBrightness;
        var probeNo = 0;
        async Task<double> Probe(double exposureSec, int? brightness, CancellationToken ct) {
            if (maxBrightness > 0 && brightness is int want && want != panelAt) {
                await SetBrightnessAsync(want, ct).ConfigureAwait(false);
                panelAt = want;
            }
            var frame = await _frames!.CaptureForAnalysisAsync(exposureSec, binning: 1, gain, offset, ct).ConfigureAwait(false);
            var level = FlatLevel.Measure(frame.Pixels.Span, frame.Width, frame.Height, frame.BayerPattern);
            if (level.Brightest > fullScale) {
                throw new FullScaleMismatchException();
            }
            probeNo++;
            LogProbe(probeNo, exposureSec, panelAt, level.Level, target);
            progress?.Report(new ApplicationStatus {
                Status = $"Flats{(filter.Length > 0 ? $" ({filter})" : "")}: measuring the panel, probe {probeNo}",
            });
            return level.Level;
        }
        return await FlatExposureSolver.SolveAsync(limits, start, maxBrightness > 0 ? startBrightness : null, Probe, token).ConfigureAwait(false);
    }

    private string InstalledFilter() {
        var info = _filterWheel?.Invoke()?.GetInfo();
        return info is { Connected: true, IsMoving: false } ? info.SelectedFilter?.Name ?? "" : "";
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The remembered solution only seeds the first probe; a catalog fault must degrade to solving from scratch, never fail the flat set. Logged.")]
    private async Task<FlatTraining?> ReadTrainingAsync(string filter, FlatSetRequest request, CancellationToken token) {
        if (_training is null) {
            return null;
        }
        try {
            return await _training.GetAsync(filter, request.Gain, request.Offset, 1, token).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogTrainingIoFailed(ex);
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Remembering the solution is an optimisation; a catalog fault must not fail flats that already solved. Logged.")]
    private async Task WriteTrainingAsync(string filter, FlatSetRequest request, FlatTraining training, CancellationToken token) {
        if (_training is null) {
            return;
        }
        try {
            await _training.PutAsync(filter, request.Gain, request.Offset, 1, training, token).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogTrainingIoFailed(ex);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Panel boundary: an absent or faulting panel means probing the scene as-is (manual panel / sky), never a failed flat set. Logged.")]
    private async Task<FlatDeviceDto?> PanelInfoAsync(CancellationToken token) {
        if (_flatDevice is null) {
            return null;
        }
        try {
            return await _flatDevice.GetAsync(token).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogPanelUnavailable(ex);
            return null;
        }
    }

    /// <summary>
    /// Close the cover when it's open (flats need the panel over the optics, and a run that
    /// imaged all night left it open), light the panel at <paramref name="brightness"/> and wait
    /// (bounded) for the runtime to report it on and closed. False when no panel is connected — the
    /// set proceeds anyway (manual panels and sky sources are legitimate §48 setups; a genuinely
    /// dark scene fails the solve honestly instead).
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Panel boundary: ApplyFlatPanelAsync throws for a disconnected/absent panel and arbitrary driver faults; the flat set must degrade to probing the scene as-is (manual panel / sky), never fault the sequence step over an optional device. CA1031's log-and-recover boundary applies.")]
    private async Task<bool> TryLightPanelAsync(int brightness, bool closeCover, CancellationToken token) {
        if (_flatDevice is null) {
            return false;
        }
        try {
            _ = await _flatDevice.ApplyFlatPanelAsync(
                new FlatPanelRequestDto(OpenCover: closeCover ? false : null, LightOn: true, Brightness: brightness > 0 ? brightness : null),
                idempotencyKey: null, token).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogPanelUnavailable(ex);
            return false;
        }
        if (closeCover) {
            LogClosingCover();
        }
        // The apply runs on a fire-and-forget worker with a 2 s state poll — wait (bounded) for
        // the cover to close and the light to actually read on so the first probe isn't of a
        // half-lit panel or the sky.
        var timeout = closeCover ? CoverCloseTimeout : PanelOnTimeout;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline) {
            token.ThrowIfCancellationRequested();
            try {
                var dto = await _flatDevice.GetAsync(token).ConfigureAwait(false);
                if (dto?.Runtime is { LightOn: true, CoverOpen: false }) {
                    LogPanelLit(dto.Runtime.Brightness);
                    return true;
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                LogPanelUnavailable(ex);
                return false;
            }
            await Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
        }
        // The apply was accepted but the light never read on — proceed and let the solve
        // decide (some calibrators mis-report state; a dark probe fails honestly).
        LogPanelLightTimeout(timeout.TotalSeconds);
        return true;
    }

    // Change the lit panel's level and wait (bounded) until it reports it, warmed up.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Panel boundary: a level read/apply fault degrades to probing at whatever the panel shows; the next probe's slope read absorbs it. Logged.")]
    private async Task SetBrightnessAsync(int brightness, CancellationToken token) {
        try {
            _ = await _flatDevice!.ApplyFlatPanelAsync(new FlatPanelRequestDto(Brightness: brightness), idempotencyKey: null, token).ConfigureAwait(false);
            var deadline = DateTimeOffset.UtcNow + BrightnessSettleTimeout;
            while (DateTimeOffset.UtcNow < deadline) {
                var dto = await _flatDevice.GetAsync(token).ConfigureAwait(false);
                if (dto?.Runtime is { LightWarming: false } rt && rt.Brightness == brightness) {
                    return;
                }
                await Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            }
            LogBrightnessTimeout(brightness, BrightnessSettleTimeout.TotalSeconds);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogPanelUnavailable(ex);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort cleanup on every exit path (incl. cancellation): a panel-off failure must never mask the set's real outcome or fault the finally. CA1031's log-and-recover boundary applies.")]
    private void TurnPanelOffBestEffort() {
        try {
            _ = _flatDevice?.ApplyFlatPanelAsync(
                new FlatPanelRequestDto(LightOn: false), idempotencyKey: null, CancellationToken.None);
        } catch (Exception ex) {
            LogPanelOffFailed(ex);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CaptureSkyFlatSetAsync(SkyFlatSetRequest request, IProgress<ApplicationStatus> progress, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TargetAdu <= 0 || request.FrameCount < 1
            || request.MinExposureSec <= 0 || request.MaxExposureSec < request.MinExposureSec
            || request.StopAtMinAdu < 0 || request.StopAtMaxAdu <= request.TargetAdu || request.StopAtMinAdu >= request.TargetAdu) {
            LogBadSkyRequest(request.TargetAdu, request.FrameCount, request.StopAtMinAdu, request.StopAtMaxAdu);
            return false;
        }
        if (_frames is null || _imaging is null) {
            LogNotWired();
            return false;
        }

        try {
            var tolerance = Math.Abs(request.TargetAdu * request.TolerancePct / 100.0);
            var exposure = Math.Clamp(1.0, request.MinExposureSec, request.MaxExposureSec);
            for (var n = 1; n <= request.FrameCount; n++) {
                token.ThrowIfCancellationRequested();
                // Re-probe before EVERY saved frame — twilight brightness drifts minute to
                // minute, so yesterday's exposure is already wrong by the next frame.
                var settled = false;
                for (var attempt = 0; attempt < SkyProbeAttemptsPerFrame; attempt++) {
                    var frame = await _frames.CaptureForAnalysisAsync(exposure, binning: 1,
                        request.Gain >= 0 ? request.Gain : null, request.Offset >= 0 ? request.Offset : null, token).ConfigureAwait(false);
                    var mean = MeanAdu(frame.Pixels.Span);
                    LogSkyProbe(n, exposure, mean);
                    if (mean >= request.StopAtMaxAdu && exposure <= request.MinExposureSec) {
                        // §48.4 bail: the sky over-exposes even at the shortest exposure.
                        LogSkyTooBright(mean, request.StopAtMaxAdu, n - 1);
                        return false;
                    }
                    if (mean <= request.StopAtMinAdu && exposure >= request.MaxExposureSec) {
                        // §48.4 bail: the sky under-exposes even at the longest exposure.
                        LogSkyTooDark(mean, request.StopAtMinAdu, n - 1);
                        return false;
                    }
                    if (mean <= 0) {
                        LogNoLight(exposure);
                        return false;
                    }
                    if (Math.Abs(mean - request.TargetAdu) <= tolerance) {
                        settled = true;
                        break;
                    }
                    var next = Math.Clamp(exposure * (request.TargetAdu / mean), request.MinExposureSec, request.MaxExposureSec);
                    if (Math.Abs(next - exposure) < 1e-9) {
                        // Pinned at a bound but still inside the stop window: capture anyway —
                        // an off-target-but-usable twilight frame beats losing the whole set.
                        LogSkyPinned(exposure, mean, request.TargetAdu);
                        settled = true;
                        break;
                    }
                    exposure = next;
                }
                if (!settled) {
                    // The sky is drifting faster than the probe can chase — capture at the last
                    // exposure rather than burning the remaining twilight probing.
                    LogSkyChasing(n, exposure);
                }
                var sequence = new CaptureSequence(
                    exposure, ImageTypes.FLAT, filterType: null,
                    binning: new OpenAstroAra.Core.Model.Equipment.BinningMode(1, 1), exposureCount: 1) {
                    Gain = request.Gain,
                    Offset = request.Offset,
                };
                _ = await _imaging.CaptureImage(sequence, token, progress, "Sky flats").ConfigureAwait(false);
                progress?.Report(new ApplicationStatus {
                    Status = $"Sky flat {n}/{request.FrameCount} at {exposure:0.###}s",
                });
            }
            LogSkySetComplete(request.FrameCount);
            return true;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogSetFailed(ex);
            return false;
        }
    }

    /// <summary>Probe retries per saved sky frame before capturing at the best-known exposure.</summary>
    internal const int SkyProbeAttemptsPerFrame = 4;

    internal static double MeanAdu(ReadOnlySpan<ushort> pixels) {
        if (pixels.IsEmpty) {
            return 0;
        }
        long sum = 0;
        foreach (var p in pixels) {
            sum += p;
        }
        return (double)sum / pixels.Length;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flat set rejected: target ADU {TargetAdu}, frame count {FrameCount}, exposure bounds [{MinSec}, {MaxSec}].")]
    private partial void LogBadRequest(double targetAdu, int frameCount, double minSec, double maxSec);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flat set unavailable: the camera analysis/capture path is not wired on this daemon.")]
    private partial void LogNotWired();

    [LoggerMessage(Level = LogLevel.Information, Message = "Flat probe {Iteration}: {ExposureSec}s at panel {Brightness} -> level {Level} ADU (target {Target}).")]
    private partial void LogProbe(int iteration, double exposureSec, int? brightness, double level, double target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Flats for '{Filter}' solved: {ExposureSec}s at panel {Brightness} -> level {Level} ADU (target {Target}) in {Probes} probes.")]
    private partial void LogSolved(string filter, double exposureSec, int? brightness, double level, double target, int probes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flats for '{Filter}' could not be solved: {Reason} ({Probes} probes, last level {Level} ADU).")]
    private partial void LogSolveFailed(string filter, string reason, int probes, double level);

    [LoggerMessage(Level = LogLevel.Information, Message = "A flat probe read above the camera's reported MaxADU {MaxAdu}: the driver delivers scaled 16-bit data, solving against 65535.")]
    private partial void LogFullScaleMismatch(int maxAdu);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Remembered flat exposures could not be read or written (best-effort).")]
    private partial void LogTrainingIoFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Closing the flat panel cover before flats.")]
    private partial void LogClosingCover();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flat panel did not report brightness {Brightness} within {Seconds}s — probing anyway.")]
    private partial void LogBrightnessTimeout(int brightness, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flat probe at {ExposureSec}s read a zero-mean frame — no light is reaching the sensor (panel off/blocked?).")]
    private partial void LogNoLight(double exposureSec);

    [LoggerMessage(Level = LogLevel.Information, Message = "Flat set complete: {FrameCount} frames at {ExposureSec}s.")]
    private partial void LogSetComplete(int frameCount, double exposureSec);

    [LoggerMessage(Level = LogLevel.Error, Message = "Flat set failed.")]
    private partial void LogSetFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Flat panel not available — probing the scene as-is (manual panel or sky).")]
    private partial void LogPanelUnavailable(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Flat panel lit at brightness {Brightness}.")]
    private partial void LogPanelLit(int brightness);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flat panel light did not report on (and closed) within {Seconds}s — proceeding; the solve fails honestly if the panel is dark.")]
    private partial void LogPanelLightTimeout(double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flat panel light-off after the set failed (best-effort).")]
    private partial void LogPanelOffFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sky-flat set rejected: target ADU {TargetAdu} must sit between stop bounds [{StopMin}, {StopMax}]; frame count {FrameCount}.")]
    private partial void LogBadSkyRequest(double targetAdu, int frameCount, double stopMin, double stopMax);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sky-flat probe (frame {Frame}): {ExposureSec}s -> mean {MeanAdu} ADU.")]
    private partial void LogSkyProbe(int frame, double exposureSec, double meanAdu);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sky flats stopped: the sky reads {MeanAdu} ADU (over the {StopMax} ceiling) at the minimum exposure — dawn is too bright. {Captured} frames captured.")]
    private partial void LogSkyTooBright(double meanAdu, double stopMax, int captured);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sky flats stopped: the sky reads {MeanAdu} ADU (under the {StopMin} floor) at the maximum exposure — the sky is too dark. {Captured} frames captured.")]
    private partial void LogSkyTooDark(double meanAdu, double stopMin, int captured);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sky-flat probe pinned at {ExposureSec}s (mean {MeanAdu} ADU, target {TargetAdu}) but inside the stop window — capturing anyway.")]
    private partial void LogSkyPinned(double exposureSec, double meanAdu, double targetAdu);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sky brightness is drifting faster than the probe converges (frame {Frame}) — capturing at {ExposureSec}s rather than burning twilight.")]
    private partial void LogSkyChasing(int frame, double exposureSec);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sky-flat set complete: {FrameCount} frames.")]
    private partial void LogSkySetComplete(int frameCount);
}
