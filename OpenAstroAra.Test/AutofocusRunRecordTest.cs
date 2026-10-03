#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Equipment.MyFocuser;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §59.12 — the autofocus run record (<see cref="AutofocusRunTracker"/>) and the §59.15 Classic-sweep
    /// event stream (<c>step_complete</c> / <c>curve_fit</c> / <c>completed</c> / <c>failed</c>) that the
    /// Setup tab's Focusing pane renders, plus the tracker-driven cancel.
    /// </summary>
    [TestFixture]
    public class AutofocusRunRecordTest {

        private const int StartPosition = 10_000;
        private static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>();

        private static AutofocusSettingsDto Settings(int steps = 4, int stepSize = 100, bool restore = true) => new(
            Method: "hfr_v_curve", Steps: steps, StepSize: stepSize, ExposureSeconds: 2, Binning: 1,
            AfFilter: "L", RunAfterFilterChange: false, TriggerTempDeltaC: 1.0, TriggerHfrDriftPct: 10,
            EveryNHours: 0, AbortSequenceOnAfFailure: false, RestorePositionOnFailure: restore);

        private static Mock<IProfileStore> Profiles(AutofocusSettingsDto settings) {
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetAutofocusSettings()).Returns(settings);
            profiles.Setup(p => p.GetFocusCalibration()).Returns((FocusCalibrationDto?)null);
            return profiles;
        }

        private static (Mock<IFocuserMediator> Mock, List<int> Moves) Focuser() {
            var moves = new List<int>();
            var position = StartPosition;
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(() => new FocuserInfo { Connected = true, Position = position, Temperature = 12.5 });
            focuser.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((p, _) => { position = p; moves.Add(p); return Task.FromResult(p); });
            return (focuser, moves);
        }

        private static Mock<IAnalysisFrameSource> Frames() {
            var frames = new Mock<IAnalysisFrameSource>();
            frames.Setup(f => f.CaptureForAnalysisAsync(It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AnalysisFrame(new ushort[16], 4, 4, DateTimeOffset.UnixEpoch));
            return frames;
        }

        private static StarDetectionResult Result(double hfr, int stars) =>
            new() { AverageHFR = hfr, DetectedStars = stars, StarList = Array.Empty<DetectedStar>() };

        private static (Mock<IWsBroadcaster> Ws, List<(string Type, JsonElement Payload)> Events) CapturingWs() {
            var events = new List<(string, JsonElement)>();
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Returns<string, JsonElement, CancellationToken>((t, p, _) => { events.Add((t, p.Clone())); return Task.CompletedTask; });
            return (ws, events);
        }

        /// <summary>A sweep whose V-curve bottoms at <paramref name="best"/>, wired to a tracker + event capture.</summary>
        private static (AutofocusSweepService Svc, AutofocusRunTracker Tracker, List<(string Type, JsonElement Payload)> Events, List<int> Moves) Build(
                double best, Func<int, StarDetectionResult>? metricOverride = null, bool restore = true) {
            var (focuser, moves) = Focuser();
            int Current() => moves.Count == 0 ? StartPosition : moves[^1];
            var tracker = new AutofocusRunTracker();
            var (ws, events) = CapturingWs();
            var svc = new AutofocusSweepService(
                Profiles(Settings(restore: restore)).Object, focuser.Object, Frames().Object,
                metric: (_, _) => {
                    if (metricOverride is not null) {
                        return metricOverride(Current());
                    }
                    var delta = (Current() - best) / 100.0;
                    return Result(1.5 + 0.2 * delta * delta, 42);
                },
                coarseMetric: (_, _) => 2.0 + Math.Abs(Current() - best) / 50.0,
                ws: ws.Object, tracker: tracker);
            return (svc, tracker, events, moves);
        }

        [Test]
        public void Tracker_starts_idle_with_nothing_to_show() {
            var snap = new AutofocusRunTracker().Snapshot();
            Assert.That(snap.State, Is.EqualTo("idle"));
            Assert.That(snap.Probes, Is.Empty);
            Assert.That(snap.Fit, Is.Null);
            Assert.That(snap.HasFrame, Is.False);
            Assert.That(new AutofocusRunTracker().TryCancel(), Is.False, "nothing to cancel");
        }

        [Test]
        public async Task A_completed_sweep_records_probes_fit_and_the_measured_final_focus() {
            var (svc, tracker, events, _) = Build(best: StartPosition - 150);
            using var _ = svc;
            tracker.StampNextTrigger("manual");

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.True);
            var snap = tracker.Snapshot();
            Assert.That(snap.State, Is.EqualTo("complete"));
            Assert.That(snap.Mode, Is.EqualTo("classic"));
            Assert.That(snap.Trigger, Is.EqualTo("manual"));
            Assert.That(snap.StartPosition, Is.EqualTo(StartPosition));
            Assert.That(snap.Filter, Is.Null, "no filter wheel in this bench");
            Assert.That(snap.FocuserTemperatureC, Is.EqualTo(12.5));
            // 9 fine probes (2·4+1), all kept, plus the coarse probes that centred the sweep.
            var fine = snap.Probes.Where(p => p.Phase == "fine").ToList();
            Assert.That(fine, Has.Count.EqualTo(9));
            Assert.That(fine, Has.All.Property("Kept").True);
            Assert.That(snap.Probes.Count(p => p.Phase == "coarse"), Is.GreaterThanOrEqualTo(2));
            Assert.That(snap.CompletedSteps, Is.EqualTo(9));
            Assert.That(snap.TotalSteps, Is.EqualTo(9));
            Assert.That(snap.SweepAttempt, Is.EqualTo(1));
            // The fit, with a sampled curve the client can draw.
            Assert.That(snap.Fit, Is.Not.Null);
            Assert.That(snap.Fit!.Algorithm, Is.EqualTo("parabolic"));
            Assert.That(snap.Fit.BestPosition, Is.EqualTo(StartPosition - 150).Within(30));
            Assert.That(snap.Fit.Curve, Has.Count.EqualTo(64));
            Assert.That(snap.Fit.Curve.Min(c => c.Hfr), Is.GreaterThan(0));
            // Final focus is MEASURED at best (the confirmation frame), not the fit's prediction.
            Assert.That(snap.FinalPosition, Is.EqualTo(StartPosition - 150).Within(30));
            Assert.That(snap.FinalHfr, Is.EqualTo(1.5).Within(0.1));
            Assert.That(snap.FinalStars, Is.EqualTo(42));
            Assert.That(snap.DurationSeconds, Is.Not.Null);
            Assert.That(snap.HasFrame, Is.False, "4×4 bench frames are too small to render");

            // §59.15 stream: started, one step_complete per probe, one curve_fit, one completed.
            Assert.That(events.Count(e => e.Type == WsEventCatalog.AutofocusStarted), Is.EqualTo(1));
            var steps = events.Where(e => e.Type == WsEventCatalog.AutofocusStepComplete).ToList();
            Assert.That(steps, Has.Count.EqualTo(snap.Probes.Count));
            Assert.That(steps.Count(s => s.Payload.GetProperty("phase").GetString() == "fine"), Is.EqualTo(9));
            Assert.That(steps.Last(s => s.Payload.GetProperty("phase").GetString() == "fine").Payload.GetProperty("total_steps").GetInt32(), Is.EqualTo(9));
            var fits = events.Where(e => e.Type == WsEventCatalog.AutofocusCurveFit).ToList();
            Assert.That(fits, Has.Count.EqualTo(1));
            Assert.That(fits[0].Payload.GetProperty("algorithm").GetString(), Is.EqualTo("parabolic"));
            Assert.That(fits[0].Payload.GetProperty("within_range").GetBoolean(), Is.True);
            var completed = events.Where(e => e.Type == WsEventCatalog.AutofocusCompleted).ToList();
            Assert.That(completed, Has.Count.EqualTo(1));
            Assert.That(completed[0].Payload.GetProperty("mode").GetString(), Is.EqualTo("classic"));
            Assert.That(completed[0].Payload.GetProperty("final_position").GetInt32(), Is.EqualTo(snap.FinalPosition));
            Assert.That(completed[0].Payload.GetProperty("final_hfr").GetDouble(), Is.EqualTo(1.5).Within(0.1));
            Assert.That(events.Any(e => e.Type == WsEventCatalog.AutofocusFailed), Is.False);
        }

        [Test]
        public async Task A_run_without_a_stamp_is_a_sequence_run() {
            var (svc, tracker, _, _) = Build(best: StartPosition - 150);
            using var _ = svc;
            await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);
            Assert.That(tracker.Snapshot().Trigger, Is.EqualTo("sequence"));
        }

        [Test]
        public async Task A_starless_sky_fails_the_run_with_the_reason_and_the_restored_position() {
            // Nothing measurable anywhere: the coarse pass gives up before the sweep.
            var (focuser, moves) = Focuser();
            var tracker = new AutofocusRunTracker();
            var (ws, events) = CapturingWs();
            using var svc = new AutofocusSweepService(
                Profiles(Settings()).Object, focuser.Object, Frames().Object,
                metric: (_, _) => Result(0, 0),
                coarseMetric: (_, _) => double.PositiveInfinity,
                ws: ws.Object, tracker: tracker);

            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.False);
            var snap = tracker.Snapshot();
            Assert.That(snap.State, Is.EqualTo("failed"));
            Assert.That(snap.Reason, Does.Contain("no measurable stars"));
            Assert.That(snap.RestoredPosition, Is.EqualTo(StartPosition));
            Assert.That(moves[^1], Is.EqualTo(StartPosition), "restore-on-failure moved the focuser home");
            Assert.That(snap.Probes.Where(p => p.Phase == "coarse"), Has.All.Property("Kept").False);
            var failed = events.Where(e => e.Type == WsEventCatalog.AutofocusFailed).ToList();
            Assert.That(failed, Has.Count.EqualTo(1));
            Assert.That(failed[0].Payload.GetProperty("reason").GetString(), Does.Contain("no measurable stars"));
            Assert.That(failed[0].Payload.GetProperty("restored_position").GetInt32(), Is.EqualTo(StartPosition));
            Assert.That(events.Any(e => e.Type == WsEventCatalog.AutofocusCompleted), Is.False);
        }

        [Test]
        public async Task A_disconnected_focuser_is_a_rejected_run_in_the_record() {
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(new FocuserInfo { Connected = false, Position = 0 });
            var tracker = new AutofocusRunTracker();
            using var svc = new AutofocusSweepService(
                Profiles(Settings()).Object, focuser.Object, Frames().Object,
                metric: (_, _) => Result(1, 10), tracker: tracker);

            Assert.That(await svc.RunAutofocusAsync(NoProgress, CancellationToken.None), Is.False);
            var snap = tracker.Snapshot();
            Assert.That(snap.State, Is.EqualTo("failed"));
            Assert.That(snap.Reason, Does.Contain("not connected"));
            Assert.That(snap.RestoredPosition, Is.Null);
        }

        [Test]
        public async Task Cancel_through_the_tracker_ends_the_run_as_cancelled_and_returns_false() {
            AutofocusRunTracker? trackerRef = null;
            var probes = 0;
            var (svc, tracker, events, moves) = Build(best: StartPosition - 150, metricOverride: position => {
                // The third probe is where the user hits Cancel.
                if (++probes == 3) {
                    trackerRef!.TryCancel();
                }
                var delta = (position - (StartPosition - 150)) / 100.0;
                return Result(1.5 + 0.2 * delta * delta, 42);
            });
            trackerRef = tracker;
            using var _ = svc;

            // The CALLER's token is never cancelled — only the tracker's run source — so the sweep must
            // report false rather than throw (a sequence step fails per its policy; the job reads the record).
            var ok = await svc.RunAutofocusAsync(NoProgress, CancellationToken.None);

            Assert.That(ok, Is.False);
            var snap = tracker.Snapshot();
            Assert.That(snap.State, Is.EqualTo("cancelled"));
            Assert.That(snap.Reason, Is.EqualTo("cancelled"));
            Assert.That(snap.RestoredPosition, Is.EqualTo(StartPosition));
            Assert.That(moves[^1], Is.EqualTo(StartPosition));
            var failed = events.Where(e => e.Type == WsEventCatalog.AutofocusFailed).ToList();
            Assert.That(failed, Has.Count.EqualTo(1));
            Assert.That(failed[0].Payload.GetProperty("reason").GetString(), Is.EqualTo("cancelled"));
            Assert.That(tracker.TryCancel(), Is.False, "nothing left to cancel");
        }

        [Test]
        public async Task A_cancelled_caller_token_still_propagates_and_records_cancelled() {
            using var cts = new CancellationTokenSource();
            var probes = 0;
            var (svc, tracker, _, _) = Build(best: StartPosition - 150, metricOverride: position => {
                if (++probes == 2) {
                    cts.Cancel();
                }
                return Result(1.5, 42);
            });
            using var _ = svc;

            await Assert.ThrowsAsync<OperationCanceledException>(() => svc.RunAutofocusAsync(NoProgress, cts.Token));
            Assert.That(tracker.Snapshot().State, Is.EqualTo("cancelled"));
        }

        [Test]
        public void Curve_fit_result_samples_its_model_across_the_requested_range() {
            var points = new List<FocusPoint>();
            for (var x = 9_600; x <= 10_400; x += 100) {
                var d = (x - 10_000) / 100.0;
                points.Add(new FocusPoint(x, 1.5 + 0.2 * d * d, 40));
            }
            var fit = FocusCurveFit.FitParabolic(points)!;
            var curve = fit.Sample(9_600, 10_400, 9);
            Assert.That(curve, Has.Count.EqualTo(9));
            Assert.That(curve[0].Position, Is.EqualTo(9_600));
            Assert.That(curve[^1].Position, Is.EqualTo(10_400));
            Assert.That(curve[4].Hfr, Is.EqualTo(1.5).Within(0.01), "the vertex sits at the sampled minimum");
            Assert.That(curve[0].Hfr, Is.EqualTo(1.5 + 0.2 * 16).Within(0.01));
            Assert.That(fit.Sample(10_000, 10_000), Is.Empty, "a degenerate range samples nothing");
        }

        [Test]
        public void Tracker_frame_round_trips_with_a_sequence() {
            var tracker = new AutofocusRunTracker();
            Assert.That(tracker.GetFrame(), Is.Null);
            tracker.SetFrame(new byte[] { 1, 2, 3 }, 1234, 1.42);
            var frame = tracker.GetFrame();
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame!.Value.Seq, Is.EqualTo(1));
            Assert.That(frame.Value.Jpeg.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            var snap = tracker.Snapshot();
            Assert.That(snap.HasFrame, Is.True);
            Assert.That(snap.FramePosition, Is.EqualTo(1234));
            Assert.That(snap.FrameHfr, Is.EqualTo(1.42));
        }
    }
}
