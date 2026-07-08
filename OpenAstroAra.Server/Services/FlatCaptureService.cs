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
/// <see cref="IFlatCaptureExecutor"/>). One flat set: light the panel (when a CoverCalibrator is
/// connected — a manual/EL panel user proceeds without one), probe exposures through the §59
/// <see cref="IAnalysisFrameSource"/> until the frame mean lands within tolerance of the target
/// ADU (probes are instrument readings — never persisted), then capture the requested saved FLAT
/// frames through the real <see cref="IImagingMediator"/> pipeline (§72 FITS + catalog row, same
/// as TakeExposure). The panel light is restored off on every exit path.
/// </summary>
public sealed partial class FlatCaptureService : IFlatCaptureExecutor {

    /// <summary>Probe iterations before declaring non-convergence (each multiplies exposure by target/mean).</summary>
    internal const int MaxProbeIterations = 8;

    /// <summary>Bounded wait for the panel light to report on before probing (2 s state-poll cadence upstream).</summary>
    private static readonly TimeSpan PanelOnTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<FlatCaptureService> _logger;
    private readonly IAnalysisFrameSource? _frames;
    private readonly IImagingMediator? _imaging;
    private readonly IFlatDeviceService? _flatDevice;

    // Test seam: the panel-on poll sleeps through this (tests assert without real waits).
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public FlatCaptureService(
        ILogger<FlatCaptureService>? logger = null,
        IAnalysisFrameSource? frames = null,
        IImagingMediator? imaging = null,
        IFlatDeviceService? flatDevice = null) {
        _logger = logger ?? NullLogger<FlatCaptureService>.Instance;
        _frames = frames;
        _imaging = imaging;
        _flatDevice = flatDevice;
    }

    /// <inheritdoc/>
    public async Task<bool> CaptureFlatSetAsync(FlatSetRequest request, IProgress<ApplicationStatus> progress, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TargetAdu <= 0 || request.FrameCount < 1
            || request.MinExposureSec <= 0 || request.MaxExposureSec < request.MinExposureSec) {
            LogBadRequest(request.TargetAdu, request.FrameCount, request.MinExposureSec, request.MaxExposureSec);
            return false;
        }
        if (_frames is null || _imaging is null) {
            LogNotWired();
            return false;
        }

        var panelLit = await TryLightPanelAsync(request.Brightness, token).ConfigureAwait(false);
        try {
            var exposure = await ProbeExposureAsync(request, token).ConfigureAwait(false);
            if (exposure is not double converged) {
                return false;
            }
            for (var n = 1; n <= request.FrameCount; n++) {
                token.ThrowIfCancellationRequested();
                var sequence = new CaptureSequence(
                    converged, ImageTypes.FLAT, filterType: null,
                    binning: new OpenAstroAra.Core.Model.Equipment.BinningMode(1, 1), exposureCount: 1) {
                    Gain = request.Gain,
                    Offset = request.Offset,
                };
                _ = await _imaging.CaptureImage(sequence, token, progress, "Flats").ConfigureAwait(false);
                progress?.Report(new ApplicationStatus {
                    Status = $"Flat {n}/{request.FrameCount} at {converged:0.###}s",
                });
            }
            LogSetComplete(request.FrameCount, converged);
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

    /// <summary>
    /// The §48.3 auto-exposure loop: start at 1 s (clamped to the bounds), scale by
    /// target/mean each probe, stop when within tolerance. Null = no convergence (bounds
    /// pinned while still out of tolerance, no light reaching the sensor, or the iteration
    /// budget ran out) — the reason is logged.
    /// </summary>
    private async Task<double?> ProbeExposureAsync(FlatSetRequest request, CancellationToken token) {
        var tolerance = Math.Abs(request.TargetAdu * request.TolerancePct / 100.0);
        var exposure = Math.Clamp(1.0, request.MinExposureSec, request.MaxExposureSec);
        double mean = 0;
        for (var i = 0; i < MaxProbeIterations; i++) {
            token.ThrowIfCancellationRequested();
            var frame = await _frames!.CaptureForAnalysisAsync(exposure, binning: 1, token).ConfigureAwait(false);
            mean = MeanAdu(frame.Pixels.Span);
            LogProbe(i + 1, exposure, mean);
            if (Math.Abs(mean - request.TargetAdu) <= tolerance) {
                LogConverged(exposure, mean, request.TargetAdu);
                return exposure;
            }
            if (mean <= 0) {
                // A pitch-black probe cannot steer the loop — scaling by target/0 is meaningless.
                LogNoLight(exposure);
                return null;
            }
            var next = Math.Clamp(exposure * (request.TargetAdu / mean), request.MinExposureSec, request.MaxExposureSec);
            if (Math.Abs(next - exposure) < 1e-9) {
                // Pinned at a bound and still out of tolerance: the panel is too bright (min
                // bound) or too dim (max bound) for this target — an exposure change can't fix it.
                LogBoundsPinned(exposure, mean, request.TargetAdu);
                return null;
            }
            exposure = next;
        }
        LogNoConvergence(MaxProbeIterations, exposure, mean, request.TargetAdu);
        return null;
    }

    /// <summary>
    /// Request the panel light at the set's brightness and wait (bounded) for the runtime to
    /// report it on. False when no panel is connected — the set proceeds anyway (manual panels
    /// and sky sources are legitimate §48 setups; a genuinely dark scene fails the probe loop
    /// honestly instead).
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Panel boundary: ApplyFlatPanelAsync throws for a disconnected/absent panel and arbitrary driver faults; the flat set must degrade to probing the scene as-is (manual panel / sky), never fault the sequence step over an optional device. CA1031's log-and-recover boundary applies.")]
    private async Task<bool> TryLightPanelAsync(int brightness, CancellationToken token) {
        if (_flatDevice is null) {
            return false;
        }
        try {
            _ = await _flatDevice.ApplyFlatPanelAsync(
                new FlatPanelRequestDto(LightOn: true, Brightness: brightness > 0 ? brightness : null),
                idempotencyKey: null, token).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogPanelUnavailable(ex);
            return false;
        }
        // The apply runs on a fire-and-forget worker with a 2 s state poll — wait (bounded) for
        // the light to actually read on so the first probe isn't of a half-lit panel.
        var deadline = DateTimeOffset.UtcNow + PanelOnTimeout;
        while (DateTimeOffset.UtcNow < deadline) {
            token.ThrowIfCancellationRequested();
            try {
                var dto = await _flatDevice.GetAsync(token).ConfigureAwait(false);
                if (dto?.Runtime.LightOn == true) {
                    LogPanelLit(dto.Runtime.Brightness);
                    return true;
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                LogPanelUnavailable(ex);
                return false;
            }
            await Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
        }
        // The apply was accepted but the light never read on — proceed and let the probe loop
        // decide (some calibrators mis-report state; a dark probe fails honestly).
        LogPanelLightTimeout(PanelOnTimeout.TotalSeconds);
        return true;
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Flat probe {Iteration}: {ExposureSec}s -> mean {MeanAdu} ADU.")]
    private partial void LogProbe(int iteration, double exposureSec, double meanAdu);

    [LoggerMessage(Level = LogLevel.Information, Message = "Flat exposure converged at {ExposureSec}s (mean {MeanAdu} ADU, target {TargetAdu}).")]
    private partial void LogConverged(double exposureSec, double meanAdu, double targetAdu);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flat probe at {ExposureSec}s read a zero-mean frame — no light is reaching the sensor (panel off/blocked?).")]
    private partial void LogNoLight(double exposureSec);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flat probe pinned at the {ExposureSec}s exposure bound with mean {MeanAdu} ADU (target {TargetAdu}) — adjust panel brightness or the exposure bounds.")]
    private partial void LogBoundsPinned(double exposureSec, double meanAdu, double targetAdu);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flat probe did not converge in {Iterations} iterations (last {ExposureSec}s -> mean {MeanAdu} ADU, target {TargetAdu}).")]
    private partial void LogNoConvergence(int iterations, double exposureSec, double meanAdu, double targetAdu);

    [LoggerMessage(Level = LogLevel.Information, Message = "Flat set complete: {FrameCount} frames at {ExposureSec}s.")]
    private partial void LogSetComplete(int frameCount, double exposureSec);

    [LoggerMessage(Level = LogLevel.Error, Message = "Flat set failed.")]
    private partial void LogSetFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Flat panel not available — probing the scene as-is (manual panel or sky).")]
    private partial void LogPanelUnavailable(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Flat panel lit at brightness {Brightness}.")]
    private partial void LogPanelLit(int brightness);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flat panel light did not report on within {Seconds}s — proceeding; the probe loop will fail honestly if the panel is dark.")]
    private partial void LogPanelLightTimeout(double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flat panel light-off after the set failed (best-effort).")]
    private partial void LogPanelOffFailed(Exception ex);
}
