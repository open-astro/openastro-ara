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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §59.2 Smart Focus — the one-frame runner (the payoff of the calibration slices #780/#781): when the
/// profile carries a usable calibration, an AF trigger reads the rig's defocus from ONE exposure via
/// <see cref="FocusInverseMap.PredictOffsetMagnitude"/> and moves straight to predicted focus — 2-3 shots
/// (30-90 s) instead of the 9-probe Classic V-curve (3-5 min). Classic remains the calibrator and the
/// §59.11 safety net; every Smart failure degrades to it, so the worst case is exactly today's behavior
/// plus up to three cheap shots.
///
/// The §59.11 fallback ladder implemented here:
///  * Not calibrated / calibration temp drift &gt; <see cref="CalibrationTempDeltaC"/> (§59.13) /
///    samples no longer rebuild a map → Classic, silently (mode is visible in `autofocus.started`).
///  * Shot 1 has &lt; <see cref="SmartMinStars"/> stars, or its features predict no magnitude
///    (starless / more defocused than anything calibrated) → `autofocus.fallback_classic` + Classic.
///  * Shot 2 worse than shot 1 (direction guess wrong — magnitude-only map, §59.2): reverse with HALF
///    the magnitude, shot 3. Still worse → restore the start position, `fallback_classic`, Classic.
///  * Shot 2 improved but missed the target: continue by ±20% of the move, shot 3, keep the better of
///    the two positions — three shots is the Smart budget (§59.3), never a fourth.
/// Direction guess: toward the calibrated best-focus position (the rig usually drifts around it).
/// Target: within <see cref="TargetHfrTolerancePct"/> percent of the calibration's in-focus HFR.
/// </summary>
public sealed partial class AutofocusSweepService {

    /// <summary>§59.3/§59.11 — fewer stars than this on the Smart shot means the feature medians are
    /// untrustworthy for a one-frame prediction; the run falls back to Classic (whose per-probe gate
    /// is the looser <see cref="MinStarsPerProbe"/>).</summary>
    internal const int SmartMinStars = 30;

    /// <summary>The Smart run's shot budget — its progress denominator on the run record.</summary>
    internal const int SmartMaxShots = 5;
    /// <summary>The vertex shot must beat the centre shot by this much to move focus there.</summary>
    internal const double VertexImprovementFactor = 0.97;
    /// <summary>A bracket shot must read at least this × the centre HFR to count as "clearly worse" —
    /// the bracket sits where the calibration says HFR has doubled, so 1.3 leaves room for seeing.</summary>
    internal const double BracketRiseFactor = 1.3;
    /// <summary>A bracket shot under this × the centre HFR means the centre is NOT the minimum.</summary>
    internal const double BracketDropFactor = 0.97;

    /// <summary>§59.13 `target_hfr_tolerance_pct` default — done when HFR is within this percentage
    /// above the calibration's fitted in-focus HFR.</summary>
    internal const double TargetHfrTolerancePct = 10.0;

    /// <summary>§59.13 `calibration_temp_delta_c` default — a calibration measured more than this many
    /// °C away from the current focuser temperature is stale (focus scale shifts thermally); recalibrate
    /// via Classic instead of trusting it. Unjudgeable when either reading is missing (the gate can't fire).</summary>
    internal const double CalibrationTempDeltaC = 8.0;

    // §59.3 phase-2 step 7 — the shot-3 trim when shot 2 improved but missed: ±20% of the applied move.
    private const double Shot3TrimFraction = 0.2;

    /// <summary>
    /// Try the Smart path. Returns <c>true</c> on success (focus reached, bookkeeping recorded),
    /// <c>false</c> when Smart ran and failed (§59.11 — the caller runs Classic; the start position is
    /// already restored and `autofocus.fallback_classic` published), and <c>null</c> when Smart never
    /// started (not calibrated / stale / unusable — the caller runs Classic silently).
    /// Runs INSIDE the sweep gate; cancellation restores the start position and propagates.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Smart-run boundary, same rationale as RunSweepCoreAsync: probe captures / focuser moves / the metric can throw device, HTTP, or math exceptions; any escape must degrade to a restored position + the Classic fallback so the Smart path is never worse than the old behavior. CA1031's log-and-recover boundary applies.")]
    private async Task<bool?> TrySmartFocusAsync(IProgress<ApplicationStatus> progress, DateTimeOffset started, CancellationToken token) {
        var calibration = _profiles.GetFocusCalibration();
        if (calibration is null) {
            return null; // never calibrated — Classic IS the calibrator (§59.1)
        }
        var info = _focuser.GetInfo();
        if (info is not { Connected: true }) {
            return null; // Classic's own guard reports the failure properly
        }

        if (calibration.FocuserTemperatureC is { } calTemp && double.IsFinite(info.Temperature)) {
            var drift = Math.Abs(info.Temperature - calTemp);
            if (drift > CalibrationTempDeltaC) {
                LogSmartSkippedStale(drift, CalibrationTempDeltaC);
                return null;
            }
        }

        var settings = _profiles.GetAutofocusSettings();
        // §59.4 — the declared optical design picks the map's magnitude key (donut diameter on obstructed
        // scopes, when this calibration's data confirms it) and which features may classify the side.
        var telescopeType = FocusFeatureProfile.Parse(settings.TelescopeType);

        var samples = new List<FocusCalibrationSample>(calibration.Samples.Count);
        foreach (var s in calibration.Samples) {
            samples.Add(s.ToSample());
        }
        var map = FocusInverseMap.Build(samples, telescopeType, calibration.InFocusHfr);
        if (map is null) {
            LogSmartSkipped("stored calibration samples no longer rebuild a usable inverse map");
            return null;
        }

        var startPosition = info.Position;
        var targetHfr = map.InFocusHfr * (1.0 + TargetHfrTolerancePct / 100.0);
        await PublishAutofocusEventAsync(WsEventCatalog.AutofocusStarted, new JsonObject { ["mode"] = "smart" }).ConfigureAwait(false);

        try {
            // Shot 1 — read the rig's current defocus where it stands. Published AFTER the prediction so
            // the §59.15 shot_complete event can carry predicted_offset + direction_source.
            var shot1 = await CaptureSmartShotAsync(1, startPosition, settings, progress, token).ConfigureAwait(false);
            if (!shot1.Trustworthy) {
                await PublishShotCompleteAsync(1, startPosition, shot1, null, null).ConfigureAwait(false);
                return await FallBackAsync(UntrustworthyReason(shot1, "the Smart shot"),
                    "too_few_stars", startPosition, restore: false).ConfigureAwait(false);
            }
            if (shot1.Hfr <= targetHfr) {
                // Reads as in focus — but one shot against a stored number is a claim, not a check (a
                // calibration from a lumpy sweep, or a lucky frame, says "in focus" just as readily).
                // Bracket it: one shot either side at the calibration's half-width, where the HFR should
                // have roughly doubled. Both clearly worse → the centre is the minimum, done in three
                // shots. A side that reads BETTER → the centre is not the minimum → the classic sweep.
                await PublishShotCompleteAsync(1, startPosition, shot1, null, null).ConfigureAwait(false);
                var bracket = BracketOffset(calibration.CurveHalfWidthSteps, settings);
                // Backlash discipline, same as the sweep: the − side is reached moving DOWN (the direction
                // every calibration sample was approached from), the + side moving up, and the centre is
                // re-entered from above through the sweep's overshoot so the focuser rests on the same
                // side of its backlash as the calibration's own best. A straight return would land the
                // optics a backlash short of 29463 while the counter said 29463 (2026-10-03).
                var minusPosition = await _focuser.MoveFocuser(startPosition - bracket, token).ConfigureAwait(false);
                var minus = await TakeSmartShotAsync(2, minusPosition, settings, progress, token).ConfigureAwait(false);
                var plusPosition = await _focuser.MoveFocuser(startPosition + bracket, token).ConfigureAwait(false);
                var plus = await TakeSmartShotAsync(3, plusPosition, settings, progress, token).ConfigureAwait(false);
                var verdict = BracketVerdict(shot1.Hfr, plus, minus);
                LogSmartBracket(bracket, shot1.Hfr, plus.Hfr, minus.Hfr, verdict ?? "confirmed");
                // Shot 4 — the V through the three shots has a vertex; when it sits off the centre, go
                // there and let the frame decide: the new position is kept only when it is clearly
                // sharper than the centre shot. A vertex outside the bracket is not a V at all.
                var vertexOffset = verdict is null ? ParabolaVertexOffset(bracket, minus.Hfr, shot1.Hfr, plus.Hfr) : null;
                if (verdict is null && (vertexOffset is null || Math.Abs(vertexOffset.Value) > bracket)) {
                    verdict = $"no minimum inside the bracket (vertex {vertexOffset?.ToString("0") ?? "undefined"})";
                }
                if (verdict is not null) {
                    var back = await LandFromAboveAsync(startPosition, settings, token).ConfigureAwait(false);
                    return await FallBackAsync($"the ±{bracket}-step bracket did not confirm focus: {verdict}",
                        "bracket_failed", back, restore: false).ConfigureAwait(false);
                }
                var vertexSteps = (int)Math.Round(vertexOffset!.Value);
                var finalShots = 3;
                FocusPoint? vertexShotTaken = null;
                int final;
                if (vertexSteps == 0) {
                    final = await LandFromAboveAsync(startPosition, settings, token).ConfigureAwait(false);
                } else {
                    var vertexPosition = await LandFromAboveAsync(startPosition + vertexSteps, settings, token).ConfigureAwait(false);
                    var vertexShot = await TakeSmartShotAsync(4, vertexPosition, settings, progress, token).ConfigureAwait(false);
                    vertexShotTaken = new FocusPoint(vertexPosition, vertexShot.Hfr, vertexShot.Features.StarCount);
                    finalShots = 4;
                    var accepted = vertexShot.Trustworthy && vertexShot.Hfr < shot1.Hfr * VertexImprovementFactor;
                    LogSmartVertex(vertexSteps, vertexShot.Hfr, shot1.Hfr, accepted ? "kept" : "centre kept");
                    final = accepted ? vertexPosition : await LandFromAboveAsync(startPosition, settings, token).ConfigureAwait(false);
                }
                // Shot 5, one confirmation frame AT the final position — the measured in-focus HFR and the
                // picture of the focused field, as the sweep does.
                var (confirmedHfr, confirmedStars) = await ConfirmFocusQuietlyAsync(settings, final, token).ConfigureAwait(false);
                // The V through THIS run's shots: the same fitter as the sweep, on the three to five points
                // just measured, so the drawn curve passes through the dots and bottoms where they do. The
                // calibration's model curve only stands in when those points will not fit.
                var shots = new List<FocusPoint> {
                    new(startPosition, shot1.Hfr, shot1.Features.StarCount),
                    new(minusPosition, minus.Hfr, minus.Features.StarCount),
                    new(plusPosition, plus.Hfr, plus.Features.StarCount),
                };
                if (vertexShotTaken is { } v) {
                    shots.Add(v);
                }
                if (confirmedHfr is { } ch && ch > 0) {
                    shots.Add(new FocusPoint(final, ch, confirmedStars ?? 0));
                }
                var shotFit = FocusCurveFit.FitBest(shots);
                if (shotFit is { IsUsable: true }) {
                    await RecordFitAsync(shotFit, shots).ConfigureAwait(false);
                } else {
                    _tracker?.SetModelCurve(final, map.InFocusHfr, bracket, final - 1.3 * bracket, final + 1.3 * bracket);
                }
                UpdateInFocusHfrQuietly(calibration, confirmedHfr);
                LogSmartComplete(final, confirmedHfr ?? shot1.Hfr, finalShots + 1);
                await RecordCompletedAsync("smart", final, confirmedHfr ?? shot1.Hfr, confirmedStars ?? shot1.Features.StarCount, started, finalShots + 1).ConfigureAwait(false);
                RecordAutofocusQuietly();
                return true;
            }
            var magnitude = map.PredictOffsetMagnitude(shot1.Features);
            if (magnitude is null or <= 0) {
                await PublishShotCompleteAsync(1, startPosition, shot1, null, null).ConfigureAwait(false);
                return await FallBackAsync("the frame is more defocused than anything calibrated (or predicts no move)",
                    "outside_calibrated_range", startPosition, restore: false).ConfigureAwait(false);
            }

            // §59.3/§59.4 direction: ask the side classifier to READ the direction from the frame's
            // learned arm signature; a confident verdict beats the heuristic. Unresolved (well-corrected
            // rig, ambiguous frame, `other` type, pre-skew calibration) → the §59.2 heuristic: toward the
            // calibrated best-focus position (drift wanders around it, so the far side is less likely).
            // Either way the shot-2/3 ladder verifies — a wrong direction costs one reversal, never more.
            int direction;
            string directionSource;
            var classifier = FocusSideClassifier.Build(samples, map.BestFocusOffset, telescopeType);
            var side = classifier?.Classify(shot1.Features, magnitude.Value) ?? FocusSideVerdict.Unresolved;
            if (side.Direction != 0) {
                direction = side.Direction;
                directionSource = "classifier";
                var qualifiedNames = string.Join(",", classifier!.QualifiedFeatureNames);
                LogSmartDirectionClassified(side.Direction, side.Confidence, qualifiedNames);
            } else {
                direction = map.BestFocusOffset >= startPosition ? 1 : -1;
                directionSource = "heuristic";
            }
            var move = (int)Math.Round(direction * magnitude.Value);
            if (move == 0) {
                move = direction; // a sub-step prediction still probes the guessed side
            }
            await PublishShotCompleteAsync(1, startPosition, shot1, move, directionSource).ConfigureAwait(false);

            // Shot 2 — at predicted focus.
            var position2 = await _focuser.MoveFocuser(startPosition + move, token).ConfigureAwait(false);
            var shot2 = await TakeSmartShotAsync(2, position2, settings, progress, token).ConfigureAwait(false);
            if (!shot2.Trustworthy) {
                // A starless/thin shot 2 (clouds, a bad move) has MedianHFR 0 and would "win" every raw
                // HFR comparison — never trust it as an improvement (review round-2 finding).
                return await FallBackAsync(UntrustworthyReason(shot2, "shot 2"),
                    "too_few_stars", startPosition, restore: settings.RestorePositionOnFailure).ConfigureAwait(false);
            }

            if (shot2.Hfr < shot1.Hfr) {
                if (shot2.Hfr <= targetHfr) {
                    LogSmartComplete(position2, shot2.Hfr, 2);
                await RecordCompletedAsync("smart", position2, shot2.Hfr, shot2.Features.StarCount, started, 2).ConfigureAwait(false);
                    RecordAutofocusQuietly();
                    return true;
                }
                // Improved but missed — one ±20% trim in the same direction, then keep the better position.
                var trim = (int)Math.Round(move * Shot3TrimFraction);
                if (trim == 0) {
                    trim = direction;
                }
                var position3 = await _focuser.MoveFocuser(position2 + trim, token).ConfigureAwait(false);
                var shot3 = await TakeSmartShotAsync(3, position3, settings, progress, token).ConfigureAwait(false);
                // An untrustworthy (thin-star) trim shot counts as "worse": shot 2's position is a
                // VERIFIED improvement, so keep that known-good result rather than falling back.
                if (!shot3.Trustworthy || shot3.Hfr > shot2.Hfr) {
                    await _focuser.MoveFocuser(position2, token).ConfigureAwait(false);
                    LogSmartComplete(position2, shot2.Hfr, 3);
                await RecordCompletedAsync("smart", position2, shot2.Hfr, shot2.Features.StarCount, started, 3).ConfigureAwait(false);
                } else {
                    LogSmartComplete(position3, shot3.Hfr, 3);
                await RecordCompletedAsync("smart", position3, shot3.Hfr, shot3.Features.StarCount, started, 3).ConfigureAwait(false);
                }
                RecordAutofocusQuietly();
                return true;
            }

            // Shot 2 worse — the direction guess was wrong. Reverse from the START with half magnitude.
            var reversed = await _focuser.MoveFocuser(startPosition - Math.Sign(move) * Math.Max(1, Math.Abs(move) / 2), token).ConfigureAwait(false);
            var reversedShot = await TakeSmartShotAsync(3, reversed, settings, progress, token).ConfigureAwait(false);
            // The reversed shot must be BOTH trustworthy and a real improvement to claim success.
            if (reversedShot.Trustworthy && reversedShot.Hfr < shot1.Hfr) {
                LogSmartComplete(reversed, reversedShot.Hfr, 3);
                await RecordCompletedAsync("smart", reversed, reversedShot.Hfr, reversedShot.Features.StarCount, started, 3).ConfigureAwait(false);
                RecordAutofocusQuietly();
                return true;
            }

            // Diverged — three shots, still worse than where we started (§59.11).
            // Restore honors the profile's RestorePositionOnFailure like every Classic restore path —
            // a user who wants the focuser left where a failed run stopped gets that here too (the
            // Classic fallback then simply centers its sweep on wherever the ladder ended).
            await NotifySmartFallbackQuietlyAsync(
                "Smart Focus diverged after 3 shots, so a full focus sweep ran instead. The sweep recalibrates automatically; if this repeats, check for passing clouds or a slipping focuser.").ConfigureAwait(false);
            return await FallBackAsync($"diverged after 3 shots (start HFR {shot1.Hfr:0.###}, best attempt {Math.Min(shot2.Hfr, reversedShot.Hfr):0.###})",
                "smart_focus_diverged", startPosition, restore: settings.RestorePositionOnFailure).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            await RestoreAsync(settings.RestorePositionOnFailure, startPosition, CancellationToken.None).ConfigureAwait(false);
            throw;
        } catch (Exception ex) {
            // Same boundary as RunSweepCoreAsync: a device/comms fault mid-Smart must degrade to a
            // restored position + the Classic fallback, never propagate out of RunAutofocusAsync
            // (review round-2 finding — MeridianFlipExecutor relies on the sweep's restore invariant).
            LogSmartErrored(ex);
            await RestoreAsync(settings.RestorePositionOnFailure, startPosition, CancellationToken.None).ConfigureAwait(false);
            await NotifySmartFallbackQuietlyAsync(
                "Smart Focus hit a device error mid-run, so a full focus sweep ran instead. See the daemon log for the fault.").ConfigureAwait(false);
            await PublishAutofocusEventAsync(WsEventCatalog.AutofocusFallbackClassic, new JsonObject {
                ["reason"] = "smart_focus_error",
            }).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>How far either side of a reads-as-focused position the bracket shots go: the
    /// calibration's half-width (where HFR doubles) when the last sweep measured one, else half the
    /// classic sweep's span. Never under one step.</summary>
    internal static int BracketOffset(double? curveHalfWidthSteps, AutofocusSettingsDto settings) {
        if (curveHalfWidthSteps is { } w && double.IsFinite(w) && w >= 1) {
            return (int)Math.Round(w);
        }
        return Math.Max(1, settings.StepSize * Math.Max(1, settings.Steps) / 2);
    }

    /// <summary>Null when both bracket shots confirm the centre is the minimum; otherwise why not.</summary>
    internal static string? BracketVerdict(double centreHfr, double plusHfr, bool plusTrustworthy, double minusHfr, bool minusTrustworthy) {
        if (!plusTrustworthy || !minusTrustworthy) {
            return "a bracket shot had too few stars";
        }
        if (plusHfr < centreHfr * BracketDropFactor) {
            return $"HFR is lower on the + side ({plusHfr:0.##} vs {centreHfr:0.##})";
        }
        if (minusHfr < centreHfr * BracketDropFactor) {
            return $"HFR is lower on the − side ({minusHfr:0.##} vs {centreHfr:0.##})";
        }
        if (plusHfr < centreHfr * BracketRiseFactor || minusHfr < centreHfr * BracketRiseFactor) {
            return $"no clear rise either side ({minusHfr:0.##} / {centreHfr:0.##} / {plusHfr:0.##})";
        }
        return null;
    }

    /// <summary>The stored in-focus HFR follows the best confirmation frame seen at best focus: the sweep's
    /// own confirmation can land on a fit that missed the true minimum by a few steps (1.29 at 29425 while
    /// the Smart run then read 1.00 at 29418, 2026-10-03), and a target that high lets a soft frame pass.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Post-success bookkeeping: a profile-store fault must not turn a completed Smart run into a failure.")]
    private void UpdateInFocusHfrQuietly(FocusCalibrationDto calibration, double? confirmedHfr) {
        if (confirmedHfr is not { } measured || !double.IsFinite(measured) || measured <= 0) {
            return;
        }
        if (calibration.InFocusHfr is { } stored && stored <= measured) {
            return;
        }
        try {
            _profiles.PutFocusCalibration(calibration with { InFocusHfr = Math.Round(measured, 4) });
            LogInFocusHfrLowered(calibration.InFocusHfr, measured);
        } catch (Exception ex) {
            LogCalibrationRecordFailed(ex);
        }
    }

    /// <summary>Every landing in the bracket is made from above — the direction the calibration's
    /// samples were approached from — through the sweep's one-step overshoot, so the focuser rests on
    /// the same side of its backlash as the calibration's best.</summary>
    private async Task<int> LandFromAboveAsync(int position, AutofocusSettingsDto settings, CancellationToken token) {
        await _focuser.MoveFocuser(position + settings.StepSize, token).ConfigureAwait(false);
        return await _focuser.MoveFocuser(position, token).ConfigureAwait(false);
    }

    /// <summary>The vertex of the parabola through (−bracket, minusHfr), (0, centreHfr), (+bracket, plusHfr),
    /// as a signed step offset from the centre; null when the three points do not curve upward.</summary>
    internal static double? ParabolaVertexOffset(int bracket, double minusHfr, double centreHfr, double plusHfr) {
        if (bracket <= 0 || !double.IsFinite(minusHfr) || !double.IsFinite(centreHfr) || !double.IsFinite(plusHfr)) {
            return null;
        }
        // y = a·x² + b·x + c with x in units of the bracket: c = centre, a + b = plus − c, a − b = minus − c.
        var a = (plusHfr + minusHfr - 2 * centreHfr) / 2;
        var b = (plusHfr - minusHfr) / 2;
        if (a <= 0) {
            return null;
        }
        return -b / (2 * a) * bracket;
    }

    private static string? BracketVerdict(double centreHfr, SmartShot plus, SmartShot minus) =>
        BracketVerdict(centreHfr, plus.Hfr, plus.Trustworthy, minus.Hfr, minus.Trustworthy);

    private readonly record struct SmartShot(double Hfr, FocusFeatureVector Features, bool NoiseFlooded = false) {
        /// <summary>Enough real stars to trust the shot's HFR: a noise-flooded frame fails this even
        /// though its blob count is huge.</summary>
        public bool Trustworthy => !NoiseFlooded && Features.StarCount >= SmartMinStars;
    }

    // Capture + publish, for shots 2/3 (no prediction fields). Shot 1 captures and publishes separately
    // so its event can carry predicted_offset + direction_source (computed between the two).
    private async Task<SmartShot> TakeSmartShotAsync(
            int shotIndex, int position, AutofocusSettingsDto settings,
            IProgress<ApplicationStatus> progress, CancellationToken token) {
        var shot = await CaptureSmartShotAsync(shotIndex, position, settings, progress, token).ConfigureAwait(false);
        await PublishShotCompleteAsync(shotIndex, position, shot, null, null).ConfigureAwait(false);
        return shot;
    }

    private async Task<SmartShot> CaptureSmartShotAsync(
            int shotIndex, int position, AutofocusSettingsDto settings,
            IProgress<ApplicationStatus> progress, CancellationToken token) {
        progress.Report(new ApplicationStatus {
            Status = $"Smart Focus: shot {shotIndex} at position {position}",
            Progress = shotIndex,
            MaxProgress = 3,
            ProgressType = ApplicationStatus.StatusProgressType.ValueOfMaxValue,
        });
        var frame = await _frames.CaptureForAnalysisAsync(settings.ExposureSeconds, settings.Binning, token).ConfigureAwait(false);
        var result = _metric(frame, token);
        var features = FocusFeatureExtractor.Extract(result);
        // The COMPARISON metric is the feature median (robust to the outliers a single frame carries),
        // not the detector's AverageHFR — Smart decisions ride on one frame per step, so the median's
        // outlier resistance matters more here than in the 9-probe sweep.
        var hfr = features.MedianHFR;
        LogSmartShot(shotIndex, position, hfr, features.StarCount);
        await RecordProbeAsync("smart", position, hfr, features.StarCount, kept: true, totalSteps: SmartMaxShots, frame).ConfigureAwait(false);
        return new SmartShot(hfr, features, result.NoiseFlooded);
    }

    private static string UntrustworthyReason(SmartShot shot, string which) => shot.NoiseFlooded
        ? $"{which} is noise, not stars ({shot.Features.StarCount} blobs) — check the camera's gain and offset"
        : $"only {shot.Features.StarCount} stars on {which} (need {SmartMinStars})";

    // §59.15 — the shot_complete event; shot 1 additionally carries the SIGNED predicted move and where
    // its direction came from ("classifier" | "heuristic") once a prediction exists.
    private Task PublishShotCompleteAsync(int shotIndex, int position, SmartShot shot,
            int? predictedOffset, string? directionSource) {
        var payload = new JsonObject {
            ["shot_index"] = shotIndex,
            ["position"] = position,
            ["hfr"] = double.IsFinite(shot.Hfr) ? Math.Round(shot.Hfr, 3) : 0.0,
            ["stars"] = shot.Features.StarCount,
        };
        if (predictedOffset is { } offset) {
            payload["predicted_offset"] = offset;
        }
        if (directionSource is not null) {
            payload["direction_source"] = directionSource;
        }
        return PublishAutofocusEventAsync(WsEventCatalog.AutofocusShotComplete, payload);
    }

    /// <summary>The §59.11 in-run failure exit: optionally restore the start position, announce the
    /// hand-off, and return <c>false</c> so the caller runs the Classic sweep.</summary>
    private async Task<bool?> FallBackAsync(string logReason, string wireReason, int startPosition, bool restore) {
        LogSmartFellBack(logReason);
        if (restore) {
            await RestoreAsync(true, startPosition, CancellationToken.None).ConfigureAwait(false);
        }
        await PublishAutofocusEventAsync(WsEventCatalog.AutofocusFallbackClassic, new JsonObject {
            ["reason"] = wireReason,
        }).ConfigureAwait(false);
        return false;
    }

    // §59.11 — only the SURPRISING fallbacks notify the user (a diverged ladder or a device fault);
    // condition-driven ones (thin stars, out-of-range defocus) stay log + WS-event only, since Classic
    // quietly completes the run and a notification per passing cloud would be noise. Best-effort like
    // every other post-hoc surfacing path.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Notification store faults must never mask or abort the AF run. Log-and-recover boundary.")]
    private async Task NotifySmartFallbackQuietlyAsync(string message) {
        if (_notifications is null) {
            return;
        }
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: DateTimeOffset.UtcNow,
                Severity: NotificationSeverity.Warning,
                Category: NotificationCategory.Equipment,
                Title: "Smart Focus fell back to a full sweep",
                Message: message,
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: null,
                RelatedEntityId: null), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogSmartNotifyFailed(ex);
        }
    }

    // Best-effort WS publish shared by the §59.15 lifecycle events; same AOT-safe construction and
    // log-and-recover boundary as the §59.10 collimation publish.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort; a broadcaster fault must never abort or fail the AF run. Log-and-recover boundary.")]
    private async Task PublishAutofocusEventAsync(string type, JsonObject payload) {
        if (_ws is null) {
            return;
        }
        try {
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(type, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogAutofocusPublishFailed(ex, type);
        }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Smart Focus skipped ({Reason}) — running the Classic sweep")]
    private partial void LogSmartSkipped(string reason);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Smart Focus skipped: calibration is thermally stale ({DriftC:0.#} °C drift > {LimitC} °C) — running the Classic sweep")]
    private partial void LogSmartSkippedStale(double driftC, double limitC);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Smart Focus shot {Shot}: position {Position} HFR {Hfr:0.###} ({Stars} stars)")]
    private partial void LogSmartShot(int shot, int position, double hfr, int stars);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Smart Focus direction {Direction:+#;-#} read from the frame (confidence {Confidence:0.##}, features: {Features}) — §59.3 side classifier")]
    private partial void LogSmartDirectionClassified(int direction, double confidence, string features);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Smart Focus complete: position {Position}, HFR {Hfr:0.###}, {Shots} shot(s)")]
    private partial void LogSmartComplete(int position, double hfr, int shots);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Smart Focus bracket ±{Bracket} steps: centre HFR {Centre:0.###}, + side {Plus:0.###}, − side {Minus:0.###} — {Verdict}")]
    private partial void LogSmartBracket(int bracket, double centre, double plus, double minus, string verdict);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Smart Focus vertex shot {Offset:+0;-0} steps from the centre: HFR {Hfr:0.###} vs centre {Centre:0.###} — {Outcome}")]
    private partial void LogSmartVertex(int offset, double hfr, double centre, string outcome);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Autofocus: stored in-focus HFR lowered from {Previous} to {Measured:0.###} (this run's confirmation frame)")]
    private partial void LogInFocusHfrLowered(double? previous, double measured);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Smart Focus fell back to the Classic sweep: {Reason}")]
    private partial void LogSmartFellBack(string reason);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Smart Focus errored — restoring and falling back to the Classic sweep")]
    private partial void LogSmartErrored(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Smart Focus: failed to post the fallback notification — the AF run continues")]
    private partial void LogSmartNotifyFailed(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Autofocus: failed to broadcast the {EventType} WS event — the AF run continues")]
    private partial void LogAutofocusPublishFailed(Exception ex, string eventType);
}
