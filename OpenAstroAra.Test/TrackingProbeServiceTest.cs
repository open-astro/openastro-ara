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
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Sequencer.SequenceItem.Telescope;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1264 — the daemon side of the tracking probe: the pass rule over a scripted ladder, the
    /// interpolated ceiling, the stored <c>tracking_calibration</c>, the divergence notification and the live
    /// events; plus the profile-store round trip of the new section.</summary>
    [TestFixture]
    public class TrackingProbeServiceTest {

        private sealed class ScriptedFrames : IAnalysisFrameSource {
            public List<double> Exposures { get; } = new();
            public Task<AnalysisFrame> CaptureForAnalysisAsync(double exposureSec, int binning, CancellationToken ct) {
                Exposures.Add(exposureSec);
                return Task.FromResult(new AnalysisFrame(new ushort[4], 2, 2, DateTimeOffset.UtcNow));
            }
        }

        private sealed class RecordingWs : IWsBroadcaster {
            public ConcurrentQueue<(string Type, string Payload)> Events { get; } = new();
            public long CurrentSequence => Events.Count;
            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
                Events.Enqueue((eventType, payload.GetRawText()));
                return Task.CompletedTask;
            }
        }

        /// <summary>A notification center that only records what was posted.</summary>
        private static (INotificationService Service, List<NotificationDto> Posted) RecordingNotifications() {
            var posted = new List<NotificationDto>();
            var mock = new Mock<INotificationService>();
            mock.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Returns((NotificationDto d, CancellationToken _) => { posted.Add(d); return Task.FromResult(d); });
            return (mock.Object, posted);
        }

        /// <summary>A metric keyed on the exposure: (stars, roundness, fwhm) per rung.</summary>
        private static Func<AnalysisFrame, CancellationToken, FocusFeatureVector> Metric(ScriptedFrames frames, Func<double, (int Stars, double Roundness, double Fwhm)> perExposure) =>
            (frame, ct) => {
                var (stars, roundness, fwhm) = perExposure(frames.Exposures[^1]);
                return new FocusFeatureVector(stars, fwhm / 2, fwhm, roundness, 10, 0, 0, 0, 0, 0);
            };

        private static readonly double[] DefaultLadder = { 10.0, 30, 60, 120 };
        private static readonly bool[] PassPassFail = { true, true, false };
        private static readonly string[] ThreeRungEvents = {
            "tracking_probe.started", "tracking_probe.rung_complete", "tracking_probe.rung_complete", "tracking_probe.rung_complete", "tracking_probe.completed" };

        private static TrackingProbeRequest Request(double? prior = null, params double[] rungs) =>
            new(rungs.Length > 0 ? rungs : DefaultLadder, 300, prior, "Veil");

        private static readonly OpticsSettingsDto RedCat = new(250, 1.0, 4144, 2822, 3.76);

        [Test]
        public void Eccentricity_comes_from_roundness() {
            Assert.That(TrackingProbeService.Eccentricity(1.0), Is.EqualTo(0));
            Assert.That(TrackingProbeService.Eccentricity(0.8), Is.EqualTo(0.6).Within(1e-9));
            Assert.That(TrackingProbeService.Eccentricity(0), Is.EqualTo(1));
            Assert.That(TrackingProbeService.Eccentricity(double.NaN), Is.NaN);
        }

        [Test]
        public void The_pixel_scale_follows_the_optics() {
            Assert.That(TrackingProbeService.PixelScaleArcsec(RedCat), Is.EqualTo(206.265 * 3.76 / 250).Within(1e-9));
            Assert.That(TrackingProbeService.PixelScaleArcsec(RedCat with { FocalLengthMm = 0 }), Is.Null);
        }

        [Test]
        public void Decide_interpolates_one_step_and_needs_two_usable_rungs() {
            static TrackingProbeRungDto R(double s, bool usable, bool passed) => new(s, usable ? 0.3 : null, usable ? 3.0 : null, usable ? 50 : 2, usable, passed);
            Assert.That(TrackingProbeService.Decide(new[] { R(10, true, true), R(30, true, true), R(60, true, false) }), Is.EqualTo(45), "midpoint between the last pass and the first fail");
            Assert.That(TrackingProbeService.Decide(new[] { R(10, true, true), R(30, true, true) }), Is.EqualTo(30), "every rung passed: the longest");
            Assert.That(TrackingProbeService.Decide(new[] { R(10, true, false), R(30, true, false) }), Is.EqualTo(10), "nothing passed: the lowest rung");
            Assert.That(TrackingProbeService.Decide(new[] { R(10, true, false) }), Is.EqualTo(10), "a trailing first rung IS the ceiling, not an inconclusive probe");
            Assert.That(TrackingProbeService.Decide(new[] { R(10, true, true), R(30, false, false) }), Is.Null, "one usable passing rung is no measurement");
        }

        [Test]
        public async Task A_ladder_that_trails_at_60_s_stops_there_stores_the_calibration_and_publishes() {
            var frames = new ScriptedFrames();
            var store = new InMemoryProfileStore();
            store.PutOpticsSettings(RedCat);
            var ws = new RecordingWs();
            var clock = new DateTimeOffset(2026, 10, 11, 2, 0, 0, TimeSpan.Zero);
            var svc = new TrackingProbeService(store, frames, metric: Metric(frames, s => s switch {
                10 => (80, 0.95, 3.0),
                30 => (80, 0.90, 3.1),
                60 => (80, 0.70, 3.9), // ecc 0.71, FWHM 1.3× → fails
                _ => throw new InvalidOperationException("the ladder must stop at the first failing rung"),
            }), ws: ws, clock: () => clock);

            var result = await svc.ProbeAsync(Request(), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(frames.Exposures, Is.EqualTo(new[] { 10.0, 30, 60 }), "120 s was never taken");
            Assert.That(result.CeilingSeconds, Is.EqualTo(45));
            Assert.That(result.UsableRungs, Is.EqualTo(3));
            var cal = store.GetTrackingCalibration();
            Assert.That(cal, Is.Not.Null);
            Assert.That(cal!.CeilingSeconds, Is.EqualTo(45));
            Assert.That(cal.MeasuredUtc, Is.EqualTo(clock));
            Assert.That(cal.Source, Is.EqualTo("measured"));
            Assert.That(cal.PixelScaleArcsec, Is.EqualTo(206.265 * 3.76 / 250).Within(1e-9));
            Assert.That(cal.SeeingFwhmArcsec, Is.EqualTo(3.0 * 206.265 * 3.76 / 250).Within(1e-9), "the first rung's FWHM in arcseconds");
            Assert.That(cal.Rungs.Select(r => r.Passed), Is.EqualTo(PassPassFail));
            Assert.That(ws.Events.Select(e => e.Type), Is.EqualTo(ThreeRungEvents));
            Assert.That(ws.Events.Last().Payload, Does.Contain("\"ceiling_seconds\":45"));
        }

        [Test]
        public async Task An_inconclusive_ladder_keeps_the_prior_and_records_it_as_what_the_run_used() {
            var frames = new ScriptedFrames();
            var store = new InMemoryProfileStore();
            var ws = new RecordingWs();
            var svc = new TrackingProbeService(store, frames, metric: Metric(frames, s => s == 10 ? (80, 0.95, 3.0) : (3, 0.9, 3.0)), ws: ws);

            var result = await svc.ProbeAsync(Request(prior: 25), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(result.CeilingSeconds, Is.Null);
            Assert.That(result.UsableRungs, Is.EqualTo(1));
            Assert.That(result.Summary, Does.Contain("25"));
            var cal = store.GetTrackingCalibration();
            Assert.That(cal, Is.Not.Null);
            Assert.That(cal!.Source, Is.EqualTo("prior"));
            Assert.That(cal.CeilingSeconds, Is.EqualTo(25));
            Assert.That(ws.Events.Last().Payload, Does.Contain("\"source\":\"prior\""));

            // Without a prior there is nothing to record: GET /tracking/calibration stays 404.
            var bare = new InMemoryProfileStore();
            var frames2 = new ScriptedFrames();
            await new TrackingProbeService(bare, frames2, metric: Metric(frames2, s => s == 10 ? (80, 0.95, 3.0) : (3, 0.9, 3.0)))
                .ProbeAsync(Request(), new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(bare.GetTrackingCalibration(), Is.Null);
        }

        [Test]
        public async Task A_first_rung_that_already_trails_is_the_ceiling_not_an_inconclusive_probe() {
            // Review of #1318: otherwise a mount trailing at 10 s keeps its planned 300 s subs and the
            // guard walks down one rung per two trailed frames.
            var frames = new ScriptedFrames();
            var store = new InMemoryProfileStore();
            var svc = new TrackingProbeService(store, frames, metric: Metric(frames, _ => (80, 0.5, 3.0)));

            var result = await svc.ProbeAsync(Request(), new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(frames.Exposures, Is.EqualTo(OnlyTen), "stopped at the first failing rung");
            Assert.That(result.CeilingSeconds, Is.EqualTo(10));
            Assert.That(result.Summary, Does.Contain("shortest rung"));
            Assert.That(store.GetTrackingCalibration()!.Source, Is.EqualTo("measured"));
        }

        private static readonly double[] OnlyTen = { 10.0 };

        [Test]
        public async Task The_request_tolerance_decides_what_trails() {
            // 30 s rung at eccentricity 0.55 (roundness 0.835): trailed at the shipped 0.5 bar, round at a 0.6 bar.
            var frames = new ScriptedFrames();
            var svc = new TrackingProbeService(new InMemoryProfileStore(), frames, metric: Metric(frames, s => s switch {
                10 => (80, 0.95, 3.0),
                30 => (80, 0.835, 3.1),
                _ => (80, 0.5, 4.5), // ecc 0.87: trails at any bar
            }));
            var strict = await svc.ProbeAsync(new TrackingProbeRequest(DefaultLadder, 300, null, "Veil"), new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(strict.CeilingSeconds, Is.EqualTo(20), "10 passes, 30 trails at 0.5: the midpoint");
            var lenient = await svc.ProbeAsync(new TrackingProbeRequest(DefaultLadder, 300, null, "Veil", null, 0.6), new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(lenient.CeilingSeconds, Is.EqualTo(45), "10 and 30 pass at 0.6, 60 trails: the midpoint");
        }

        [Test]
        public async Task The_stored_ceiling_is_the_prior_unless_the_body_carries_one() {
            var store = new InMemoryProfileStore();
            var svc = new TrackingProbeService(store, new ScriptedFrames());
            Assert.That(await svc.ResolvePriorAsync(null, CancellationToken.None), Is.Null, "never probed, no spec sheet");
            store.PutTrackingCalibration(Calibration());
            Assert.That(await svc.ResolvePriorAsync(null, CancellationToken.None), Is.EqualTo(45), "last night's measurement");
            Assert.That(await svc.ResolvePriorAsync(20, CancellationToken.None), Is.EqualTo(20), "the body's spec-sheet prior wins");
        }

        [Test]
        public async Task A_ceiling_far_off_the_prior_raises_one_notification() {
            var frames = new ScriptedFrames();
            var (notifications, posted) = RecordingNotifications();
            var good = Metric(frames, _ => (80, 0.95, 3.0));
            var svc = new TrackingProbeService(new InMemoryProfileStore(), frames, metric: good, notifications: notifications);

            // Everything passes up to 120 s against a 20 s spec: 6× better → a (friendly) notification.
            await svc.ProbeAsync(Request(prior: 20), new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(posted, Has.Count.EqualTo(1));
            Assert.That(posted[0].Severity, Is.EqualTo(NotificationSeverity.Info));

            // 45 s against a 40 s spec: within 2× → quiet.
            var frames2 = new ScriptedFrames();
            var svc2 = new TrackingProbeService(new InMemoryProfileStore(), frames2, metric: Metric(frames2, s => s <= 30 ? (80, 0.95, 3.0) : (80, 0.5, 3.0)), notifications: notifications);
            await svc2.ProbeAsync(Request(prior: 40), new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(posted, Has.Count.EqualTo(1));

            // 10 s against a 120 s spec: 12× worse → a warning.
            var frames3 = new ScriptedFrames();
            var svc3 = new TrackingProbeService(new InMemoryProfileStore(), frames3, metric: Metric(frames3, s => s <= 10 ? (80, 0.95, 3.0) : (80, 0.5, 3.0)), notifications: notifications);
            await svc3.ProbeAsync(Request(prior: 120), new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(posted, Has.Count.EqualTo(2));
            Assert.That(posted[1].Severity, Is.EqualTo(NotificationSeverity.Warning));
            Assert.That(posted[1].Message, Does.Contain("from the prior"), "no source named: the generic wording");

            // The wording names where the prior came from when the instruction says.
            var frames4 = new ScriptedFrames();
            var svc4 = new TrackingProbeService(new InMemoryProfileStore(), frames4, metric: Metric(frames4, s => s <= 10 ? (80, 0.95, 3.0) : (80, 0.5, 3.0)), notifications: notifications);
            await svc4.ProbeAsync(new TrackingProbeRequest(DefaultLadder, 300, 120, "Veil", "the last probe"), new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(posted[2].Message, Does.Contain("from the last probe"));
            Assert.That(posted[2].Title, Does.Not.Contain("spec"));
        }

        [Test]
        public async Task The_frame_quality_feed_sequences_readings_and_tags_through_the_repository() {
            var feed = new FrameQualityFeed();
            Assert.That(feed.LatestLight, Is.Null);
            var id = Guid.NewGuid();
            feed.Record(id, 0.42, 3.1, 120);
            feed.Record(Guid.NewGuid(), 0.2, 2.9, 120);
            Assert.That(feed.LatestLight!.Sequence, Is.EqualTo(2));
            Assert.That(feed.LatestLight.Eccentricity, Is.EqualTo(0.2));
            await feed.FlagFrameAsync(id, "trailed", CancellationToken.None); // no repository: best-effort no-op
            await feed.NotifyAsync("t", "m", CancellationToken.None);
        }

        // ── profile section round trip (mirrors FocusCalibrationStoreTest) ──

        private static TrackingCalibrationDto Calibration() => new(
            CeilingSeconds: 45, MeasuredUtc: new DateTimeOffset(2026, 10, 11, 2, 0, 0, TimeSpan.Zero),
            PolarAlignErrorArcmin: 4.2, PixelScaleArcsec: 3.1, SeeingFwhmArcsec: 2.8, PriorSeconds: 20, Source: "measured",
            Rungs: new[] { new TrackingProbeRungDto(10, 0.3, 3.0, 80, true, true), new TrackingProbeRungDto(30, 0.7, 3.9, 80, true, false) });

        [Test]
        [Category("IO")]
        public void Tracking_calibration_round_trips_through_profile_json_and_clears() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-tracking-cal-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                var store = new FileProfileStore(dir);
                Assert.That(store.GetTrackingCalibration(), Is.Null, "fresh profile: never measured");
                var cal = Calibration();
                store.PutTrackingCalibration(cal);
                var reopened = new FileProfileStore(dir);
                var loaded = reopened.GetTrackingCalibration();
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.CeilingSeconds, Is.EqualTo(45));
                Assert.That(loaded.Rungs, Is.EqualTo(cal.Rungs).AsCollection);
                Assert.That(loaded.PolarAlignErrorArcmin, Is.EqualTo(4.2));

                reopened.PutTrackingCalibration(null);
                Assert.That(new FileProfileStore(dir).GetTrackingCalibration(), Is.Null, "the clear survives a restart");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void A_probe_leaf_costs_its_own_ladder_in_the_run_eta_not_the_nominal_15_s() {
            var root = new OpenAstroAra.Sequencer.Container.SequentialContainer();
            root.Add(new TrackingProbe { MaxExposure = 120 });
            Assert.That(RunEtaEstimator.EstimateTotalSeconds(root), Is.EqualTo(10 + 30 + 60 + 120 + 4 * 5));
        }

        [Test]
        public void The_snapshot_carries_the_section_and_apply_clears_it_for_a_never_probed_profile() {
            var store = new InMemoryProfileStore();
            store.PutTrackingCalibration(Calibration());
            var snap = ProfileStoreSnapshot.Capture(store);
            Assert.That(snap.TrackingCalibration, Is.Not.Null);

            var other = new InMemoryProfileStore();
            other.PutTrackingCalibration(Calibration());
            ProfileStoreSnapshot.Apply(other, snap with { TrackingCalibration = null });
            Assert.That(other.GetTrackingCalibration(), Is.Null, "switching to a never-probed profile clears the live store");
        }
    }
}
