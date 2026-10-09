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
using OpenAstroAra.Core.Interfaces;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1311 — the polar alignment residual from guiding. Each guided run's first minutes of clean
/// guiding are fitted for Dec drift (<see cref="PaResidualEstimator"/>; the sampling rules are
/// <see cref="PaResidualTracker"/>'s), published as <c>guider.pa_residual</c>, kept on
/// <c>GET /equipment/guider</c> for a reconnecting client and logged with the imaging session.
/// </summary>
public sealed partial class GuiderService {

    /// <summary>Where a finished measurement is logged; null in benches.</summary>
    public IPaResidualLog? PaResidualLog { get; set; }

    /// <summary>The Polar Align log, for the comparison figure; null in benches.</summary>
    public IPolarAlignmentLog? PolarAlignmentLog { get; set; }

    /// <summary>The imaging session running now (one only), stamped on the logged row.</summary>
    public Func<Guid?>? ActiveRunSession { get; set; }

    /// <summary>The mount's Dec (°) and hour angle (h, −12…12) now, or null when unknown.</summary>
    public Func<(double DecDeg, double HourAngleHours)?>? MountPointing { get; set; }

    // Test knobs: the frame time the fit uses (the guide step's receipt time by default) and the
    // wall clock stamped on the result.
    internal Func<IGuideStep, double> PaResidualStepTime { get; set; } = step => step.Time;
    internal Func<DateTimeOffset> PaResidualClock { get; set; } = () => DateTimeOffset.UtcNow;

    // A Polar Align result older than this is not tonight's alignment.
    private static readonly TimeSpan AlignComparisonWindow = TimeSpan.FromHours(12);

    private readonly object _paGate = new();
    private PaResidualTracker _paTracker = new();
    private PaResidualDto? _paCurrent;  // what GET shows; null = nothing measured yet
    private PaResidualDto? _paLastDone; // shown again when a later run drops its measurement
    private long _paGeneration;         // bumped per run, so a late async finish can't land on a newer run
    private double? _paDecRatePxPerSec;
    private string? _paUnavailable;     // this run can't be measured (reason), decided at its start

    /// <summary>Swaps the tracker (tests shorten the sample window).</summary>
    internal void UsePaResidualTracker(PaResidualTracker tracker) {
        lock (_paGate) {
            _paTracker = tracker;
        }
    }

    internal PaResidualDto? PaResidualSnapshot() {
        lock (_paGate) {
            return _paCurrent;
        }
    }

    private void FeedPaResidualStep(PHD2Guider guider, IGuideStep step) {
        PaResidualAction action;
        lock (_paGate) {
            action = _paTracker.OnStep(PaResidualStepTime(step), step.DECDistanceRaw, step.DECDuration);
        }
        HandlePaResidualAction(guider, action);
    }

    private void FeedPaResidualMarker(PHD2Guider guider, string? kind) {
        PaResidualAction action;
        lock (_paGate) {
            action = _paTracker.OnMarker(kind);
        }
        HandlePaResidualAction(guider, action);
    }

    // The guider went away: a measurement in flight is dropped (the last result stays shown).
    private void ResetPaResidual() {
        PaResidualAction action;
        lock (_paGate) {
            action = _paTracker.Reset();
            _paGeneration++;
        }
        if (action == PaResidualAction.Cancelled) {
            ShowLastPaResidual();
        }
    }

    private void HandlePaResidualAction(PHD2Guider guider, PaResidualAction action) {
        switch (action) {
            case PaResidualAction.Started:
                BeginPaResidual(guider);
                break;
            case PaResidualAction.Progress:
                PublishPaProgress();
                break;
            case PaResidualAction.Complete:
                _ = FinishPaResidualAsync(guider);
                break;
            case PaResidualAction.Cancelled:
                // A run already shown as unavailable keeps saying why, however long it lasted.
                if (!PaRunUnavailable()) {
                    ShowLastPaResidual();
                }
                break;
        }
    }

    private bool PaRunUnavailable() {
        lock (_paGate) {
            return _paUnavailable is not null;
        }
    }

    private void BeginPaResidual(PHD2Guider guider) {
        // Lock-position shift (comet tracking) drifts the star in Dec on purpose: not a PA signal.
        var unavailable = guider.ShiftEnabled ? "lock_shift" : null;
        long generation;
        PaResidualDto current;
        lock (_paGate) {
            generation = ++_paGeneration;
            _paDecRatePxPerSec = null;
            _paUnavailable = unavailable;
            current = new PaResidualDto(
                Id: Guid.NewGuid(),
                Status: unavailable is null ? "measuring" : "unavailable",
                StartedUtc: PaResidualClock(),
                CompletedUtc: null,
                SampleSeconds: 0,
                TargetSeconds: _paTracker.TargetSeconds,
                Frames: 0,
                Reason: unavailable);
            _paCurrent = current;
        }
        PublishPaResidual(current);
        if (unavailable is null) {
            _ = PrepareRunAsync(guider, generation);
        }
    }

    // Off the listener thread, once per run: an adaptive-optics unit makes the run unmeasurable
    // (its Dec corrections are AO steps, not mount pulses), and the calibration's Dec rate is read
    // for the fit. The finish retries the rate once if this attempt came back empty.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fire-and-forget off the guider's listener thread: a fault is logged and the finish retries.")]
    private async Task PrepareRunAsync(PHD2Guider guider, long generation) {
        try {
            if (await guider.HasConnectedAoAsync().ConfigureAwait(false) == true) {
                MarkPaRunUnavailable(generation, "ao");
                return;
            }
            var rate = await guider.GetDecGuideRateAsync().ConfigureAwait(false);
            lock (_paGate) {
                if (generation == _paGeneration) {
                    _paDecRatePxPerSec = rate;
                }
            }
        } catch (Exception ex) {
            LogPaResidualFailed(ex);
        }
    }

    private void MarkPaRunUnavailable(long generation, string reason) {
        PaResidualDto? current;
        lock (_paGate) {
            if (generation != _paGeneration || _paCurrent is null) {
                return;
            }
            _paUnavailable = reason;
            current = _paCurrent = _paCurrent with { Status = "unavailable", Reason = reason };
        }
        PublishPaResidual(current);
    }

    private void PublishPaProgress() {
        PaResidualDto? current;
        lock (_paGate) {
            if (_paCurrent is not { Status: "measuring" } measuring) {
                return;
            }
            current = _paCurrent = measuring with {
                SampleSeconds = Math.Round(_paTracker.Estimator.SampleSeconds, 1),
                Frames = _paTracker.Estimator.Frames,
            };
        }
        PublishPaResidual(current);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fire-and-forget off the guider's listener thread: a log/publish fault must never reach the socket reader.")]
    private async Task FinishPaResidualAsync(PHD2Guider guider) {
        try {
            long generation;
            double? rate;
            string? unavailable;
            lock (_paGate) {
                generation = _paGeneration;
                rate = _paDecRatePxPerSec;
                unavailable = _paUnavailable;
            }
            if (unavailable is not null) {
                return; // already shown as unavailable when the run started
            }
            rate ??= await guider.GetDecGuideRateAsync().ConfigureAwait(false);
            var pixelScale = guider.PixelScale;

            PaResidualFit? fit = null;
            PaResidualDto started;
            lock (_paGate) {
                if (generation != _paGeneration || _paCurrent is null) {
                    return; // a newer run (or a disconnect) superseded this one
                }
                started = _paCurrent;
                if (rate is double r && pixelScale > 0) {
                    fit = _paTracker.Estimator.Fit(r, pixelScale);
                }
            }

            var now = PaResidualClock();
            var reason = rate is null ? "no_calibration" : !(pixelScale > 0) ? "no_pixel_scale" : fit is null ? "no_fit" : null;
            PaResidualDto result;
            if (fit is not PaResidualFit f) {
                result = started with { Status = "unavailable", CompletedUtc = now, Reason = reason };
            } else {
                var pointing = MountPointing?.Invoke();
                var align = PolarAlignmentLog is null
                    ? null
                    : await PolarAlignmentLog.GetLatestMeasuredAsync(now - AlignComparisonWindow, CancellationToken.None).ConfigureAwait(false);
                result = started with {
                    Status = "done",
                    CompletedUtc = now,
                    SampleSeconds = Math.Round(f.SampleSeconds, 1),
                    Frames = f.Frames,
                    DriftArcsecPerMin = f.DriftArcsecPerMin,
                    PaErrorMinArcmin = f.PaErrorMinArcmin,
                    UncertaintyArcmin = f.UncertaintyArcmin,
                    Reliable = f.UncertaintyArcmin <= Math.Max(1.0, 0.5 * f.PaErrorMinArcmin),
                    HourAngleHours = pointing?.HourAngleHours,
                    DecDeg = pointing?.DecDeg,
                    AlignErrorArcmin = align?.FinalErrorArcmin,
                    AlignEndedUtc = align?.EndedAt,
                    SessionId = ActiveRunSession?.Invoke(),
                };
                LogPaResidualMeasured(f.PaErrorMinArcmin, f.UncertaintyArcmin, f.DriftArcsecPerMin, f.SampleSeconds, f.Frames);
            }

            lock (_paGate) {
                if (generation != _paGeneration) {
                    return;
                }
                _paCurrent = result;
                if (result.Status == "done") {
                    _paLastDone = result;
                }
            }
            PublishPaResidual(result);
            if (result.Status == "done" && PaResidualLog is not null) {
                await PaResidualLog.InsertAsync(result, CancellationToken.None).ConfigureAwait(false);
            }
        } catch (Exception ex) {
            LogPaResidualFailed(ex);
        }
    }

    // A dropped measurement shows the previous result again, or nothing.
    private void ShowLastPaResidual() {
        PaResidualDto? last;
        lock (_paGate) {
            last = _paCurrent = _paLastDone;
        }
        if (last is null) {
            _ = PublishPaResidualAsync(new JsonObject { ["status"] = "idle" }.ToJsonString());
        } else {
            PublishPaResidual(last);
        }
    }

    private void PublishPaResidual(PaResidualDto dto) =>
        _ = PublishPaResidualAsync(JsonSerializer.Serialize(dto, AraJsonSerializerContext.Default.PaResidualDto));

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort: a broadcaster fault must never reach the guider's socket listener. Log-and-recover boundary.")]
    private async Task PublishPaResidualAsync(string json) {
        if (_ws is null) {
            return;
        }
        try {
            using var doc = JsonDocument.Parse(json);
            await _ws.PublishAsync(WsEventCatalog.GuiderPaResidual, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogPaResidualFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "PA residual from guiding: ≥ {ErrorArcmin:F2}′ ± {UncertaintyArcmin:F2}′ (Dec drift {DriftArcsecPerMin:F3}″/min over {Seconds:F0} s, {Frames} frames)")]
    private partial void LogPaResidualMeasured(double errorArcmin, double uncertaintyArcmin, double driftArcsecPerMin, double seconds, int frames);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PA residual measurement failed")]
    private partial void LogPaResidualFailed(Exception ex);
}
