#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1589 — Dec running away under correction (the error growing while the Dec pulses sit at their
    /// maximum, as a wrong Dec direction after a meridian flip does) stops guiding and publishes a
    /// guider fault, so the shared reaction runs on_guider_lost (pause by default) and notifies. Guiding
    /// that is started again and settles recovers the fault, which resumes the paused runs. Normal
    /// guiding, a dither, a short gust and a steady drift at the pulse limit never trip it.
    /// </summary>
    [TestFixture]
    [Category("IO")] // loopback socket against the fake guider
    public class GuiderDecRunawayTest {

        private const double MaxPulseMs = 2500;   // the guider's MaxDecDuration cap
        private const double DecRatePxPerSec = 4; // the calibration's Dec rate
        private const double Aggressiveness = 0.7;

        private sealed class Rig : IDisposable {
            public readonly Mock<ISequencerService> Sequencer = new();
            public readonly List<NotificationDto> Notifications = new();
            public readonly List<string> WsTopics = new();
            public readonly Guid RunId = Guid.NewGuid();
            public readonly HeadlessProfileService Profile = new();
            public readonly List<EquipmentFaultEvent> Faults = new();
            public readonly List<EquipmentFaultEvent> Recovered = new();
            public readonly FaultReactionService Reaction;
            public GuiderService Service = null!;

            public Rig(string onGuiderLost = "pause_and_retry", bool runActive = true, int settleTime = 0) {
                // The profile default (10 s) would make the resume wait 10 s.
                Profile.ActiveProfile.GuiderSettings.SettleTime = settleTime;
                var profiles = new Mock<IProfileStore>();
                profiles.Setup(p => p.GetSafetyPolicies()).Returns(new SafetyPoliciesDto(
                    OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                    MeridianFlipAuto: true, MeridianPauseMin: 2, MeridianRecenter: true, MeridianRecalGuider: false,
                    OnAltitudeLimit: "pause", ParkIfNoMoreTargets: true, OnGuiderLost: onGuiderLost,
                    GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: false));
                var running = runActive ? new List<Guid> { RunId } : new List<Guid>();
                Sequencer.Setup(s => s.SkipActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(running.Count);
                Sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(running.Count);
                Sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(running);
                Sequencer.Setup(s => s.PauseActiveRunsAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(running);
                Sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => ids.Count);
                Sequencer.Setup(s => s.ActiveRunIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(running);
                Sequencer.Setup(s => s.DaemonPausedRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => (IReadOnlyList<Guid>)ids.ToList());
                var notifications = new Mock<INotificationService>();
                notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                    .Callback<NotificationDto, CancellationToken>((n, _) => { lock (Notifications) { Notifications.Add(n); } })
                    .Returns(Task.CompletedTask);
                var ws = new Mock<IWsBroadcaster>();
                ws.Setup(w => w.PublishAsync(It.IsAny<WsEvent>(), It.IsAny<System.Text.Json.JsonElement>(), It.IsAny<CancellationToken>()))
                    .Callback<WsEvent, System.Text.Json.JsonElement, CancellationToken>((t, _, _) => { lock (WsTopics) { WsTopics.Add(t); } })
                    .Returns(Task.CompletedTask);
                var hub = new EquipmentFaultHub(ws.Object);
                hub.Subscribe(f => { lock (Faults) { Faults.Add(f); } });
                hub.SubscribeRecovered(f => { lock (Recovered) { Recovered.Add(f); } });
                Reaction = new FaultReactionService(hub, reconnector: null, profiles.Object, () => Sequencer.Object,
                    telescope: null, notifications.Object, ws.Object, guiderEpisodes: () => Service);
                Reaction.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                Service = new GuiderService(
                    Profile,
                    new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(),
                        Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
                    NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(), ws.Object,
                    profiles.Object, () => Sequencer.Object, notifications.Object, hub);
            }

            public int Paused() => Sequencer.Invocations.Count(i => i.Method.Name == nameof(ISequencerService.PauseActiveRunsAsync));
            public int Skipped() => Sequencer.Invocations.Count(i => i.Method.Name == nameof(ISequencerService.SkipActiveRunsAsync));
            public int Aborted() => Sequencer.Invocations.Count(i => i.Method.Name == nameof(ISequencerService.AbortActiveRunsAsync));
            public int Resumed() => Sequencer.Invocations.Count(i => i.Method.Name == nameof(ISequencerService.ResumeRunsAsync));
            public int StepsSeen() {
                lock (WsTopics) {
                    return WsTopics.Count(t => t == WsEventCatalog.GuiderStep.Token);
                }
            }
            public List<EquipmentFaultEvent> RunawayFaults() {
                lock (Faults) {
                    return Faults.Where(f => f.Details == "dec_runaway").ToList();
                }
            }
            public List<string> NotificationTitles() {
                lock (Notifications) {
                    return Notifications.Select(n => n.Title).ToList();
                }
            }

            public void Dispose() {
                Service.Dispose();
                Reaction.Dispose();
            }
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 30_000, int pollMs = 25) {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline) {
                if (condition()) {
                    return true;
                }
                await Task.Delay(pollMs).ConfigureAwait(false);
            }
            return condition();
        }

        // Connected and guiding, with the stop_capture of real PHD2 (GuidingStopped, then Stopped).
        private static async Task<FakeGuider> GuidingFakeAsync(Rig rig) {
            var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Guiding"));
            fake.OnRpc("get_app_state", JsonValue.Create("Guiding"));
            fake.OnRpc("get_connected", JsonValue.Create(true));
            fake.OnRpc("stop_capture", _ => {
                fake.BroadcastAsync(PhdEvents.GuidingStopped()).GetAwaiter().GetResult();
                fake.BroadcastAsync(PhdEvents.AppState("Stopped")).GetAwaiter().GetResult();
                fake.OnRpc("get_app_state", JsonValue.Create("Stopped"));
                return JsonValue.Create(0);
            });
            await rig.Service.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Service.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected).ConfigureAwait(false),
                Is.True, "the guider never reached Connected against the fake");
            await StepAsync(rig, fake, dec: 0.05, decMs: 0).ConfigureAwait(false);
            return fake;
        }

        // PHD2 starts guiding (again): the app state reads Guiding and StartGuiding is sent.
        private static async Task StartGuidingAsync(FakeGuider fake) {
            fake.OnRpc("get_app_state", JsonValue.Create("Guiding"));
            await fake.BroadcastAsync(PhdEvents.AppState("Guiding")).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.StartGuiding()).ConfigureAwait(false);
        }

        // One guide step, sent and handled: the daemon publishes guider.step after it has fed the watch.
        // A positive Dec offset gets a South pulse, PHD2's way; a pulse at the cap is flagged DecLimited.
        private static async Task StepAsync(Rig rig, FakeGuider fake, double dec, double decMs, double ra = 0, double raMs = 0) {
            var before = rig.StepsSeen();
            await fake.BroadcastAsync(PhdEvents.GuideStep(ra, dec, decDurationMs: decMs, decDirection: dec >= 0 ? "South" : "North",
                raDurationMs: raMs, raDirection: raMs > 0 ? "West" : null, decLimited: decMs >= MaxPulseMs)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.StepsSeen() > before, pollMs: 2).ConfigureAwait(false), Is.True, "the guide step never landed");
        }

        // Dec walks off 0.5 px a frame with every Dec pulse at the maximum: a runaway on the frame the
        // streak completes, and the frames after it are ignored.
        private static async Task RunAwayAsync(Rig rig, FakeGuider fake) {
            for (var n = 1; n <= GuideAxisRunaway.Steps + 2; n++) {
                await StepAsync(rig, fake, dec: 0.5 * n, decMs: MaxPulseMs).ConfigureAwait(false);
            }
        }

        private static int StopCaptures(FakeGuider fake) => fake.ReceivedMethods.Count(m => m == "stop_capture");

        private static void AssertNoRunaway(Rig rig, FakeGuider fake) {
            Assert.That(rig.RunawayFaults(), Is.Empty, "no Dec runaway fault");
            Assert.That(StopCaptures(fake), Is.Zero, "guiding was not stopped");
            Assert.That(rig.Paused(), Is.Zero, "no run was paused");
        }

        [Test]
        public async Task A_dec_runaway_stops_guiding_pauses_the_run_and_notifies() {
            using var rig = new Rig();
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);

            // Dec walks off 0.5 px a frame with every Dec pulse at the maximum, and never comes back.
            for (var n = 1; n <= GuideAxisRunaway.Steps + 2; n++) {
                await StepAsync(rig, fake, dec: 0.5 * n, decMs: MaxPulseMs).ConfigureAwait(false);
            }

            Assert.That(await WaitUntilAsync(() => StopCaptures(fake) == 1).ConfigureAwait(false), Is.True, "the runaway stops guiding");
            var fault = rig.RunawayFaults().Single();
            Assert.That(fault.DeviceType, Is.EqualTo(DeviceType.Guider));
            Assert.That(fault.Kind, Is.EqualTo(EquipmentFaultKind.TrackingLost));
            lock (rig.Faults) {
                Assert.That(rig.Faults, Has.Count.EqualTo(1), "one fault, and only the runaway's");
            }
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True, "on_guider_lost pauses the run");
            rig.Sequencer.Verify(s => s.PauseActiveRunsAsync(true, It.IsAny<CancellationToken>()), Times.Once,
                "an adoptable pause: the frame in progress stops too (#1891)");
            Assert.That(await WaitUntilAsync(() => rig.NotificationTitles().Count == 1).ConfigureAwait(false), Is.True);
            Assert.That(rig.NotificationTitles()[0], Is.EqualTo("Dec guiding ran away — sequence paused"));
            lock (rig.Notifications) {
                Assert.That(rig.Notifications[0].Message, Does.Contain("Calibrate the guider again"));
            }
        }

        [Test]
        public async Task A_forced_wrong_dec_direction_after_a_flip_is_stopped_during_the_settle() {
            using var rig = new Rig();
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            // The flip: guiding stops, the mount changes side, guiding starts again and settles.
            await fake.BroadcastAsync(PhdEvents.GuidingStopped()).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);

            // The calibration's Dec direction is now wrong: each pulse, proportional to the error and capped
            // at the maximum, pushes the star further away. Every frame is still inside the settle.
            var dec = 0.3;
            int? trippedAt = null;
            for (var n = 1; n <= GuideAxisRunaway.Steps + 2 && trippedAt is null; n++) {
                var pulse = Math.Min(MaxPulseMs, Aggressiveness * dec / (DecRatePxPerSec / 1000));
                await fake.BroadcastAsync(PhdEvents.Settling(distance: dec, time: n * 2, settleTime: 10)).ConfigureAwait(false);
                await StepAsync(rig, fake, dec, pulse).ConfigureAwait(false);
                if (rig.RunawayFaults().Count > 0) {
                    trippedAt = n;
                }
                dec += pulse * DecRatePxPerSec / 1000;
            }

            Assert.That(trippedAt, Is.Not.Null, $"no trip within {GuideAxisRunaway.Steps + 2} settle frames of the growth starting");
            Assert.That(trippedAt, Is.LessThanOrEqualTo(GuideAxisRunaway.Steps + 2));
            Assert.That(dec, Is.LessThan(60), "stopped while the error is tens of pixels, not a runaway across the sky");
            Assert.That(await WaitUntilAsync(() => StopCaptures(fake) == 1).ConfigureAwait(false), Is.True, "the runaway stops guiding");
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True);
        }

        [Test]
        public async Task A_settled_guided_run_after_the_runaway_resumes_the_paused_run() {
            using var rig = new Rig();
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);
            for (var n = 1; n <= GuideAxisRunaway.Steps; n++) {
                await StepAsync(rig, fake, dec: 0.5 * n, decMs: MaxPulseMs).ConfigureAwait(false);
            }
            Assert.That(await WaitUntilAsync(() => StopCaptures(fake) == 1 && rig.NotificationTitles().Count == 1).ConfigureAwait(false), Is.True);

            // Steps before guiding starts again neither trip a second time nor recover: guiding is stopped.
            for (var n = 1; n <= GuideAxisRunaway.Steps + 2; n++) {
                await StepAsync(rig, fake, dec: 0.5 * n, decMs: MaxPulseMs).ConfigureAwait(false);
            }
            for (var n = 0; n < 3; n++) {
                await StepAsync(rig, fake, dec: 0.1, decMs: 20).ConfigureAwait(false);
            }
            await Task.Delay(300).ConfigureAwait(false);
            Assert.That(rig.RunawayFaults(), Has.Count.EqualTo(1), "one trip per guided run");
            Assert.That(StopCaptures(fake), Is.EqualTo(1));
            Assert.That(rig.Resumed(), Is.Zero, "settled steps before guiding starts again do not resume the run");
            lock (rig.Recovered) {
                Assert.That(rig.Recovered, Is.Empty);
            }

            // The user calibrates again and starts guiding: once guiding has settled, the paused run resumes.
            await StartGuidingAsync(fake).ConfigureAwait(false);
            await StepAsync(rig, fake, dec: 0.1, decMs: 20).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "settled guiding resumes the paused run");
            rig.Sequencer.Verify(s => s.ResumeRunsAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == rig.RunId), It.IsAny<CancellationToken>()), Times.Once);
            lock (rig.Recovered) {
                Assert.That(rig.Recovered.Single().Details, Is.EqualTo("dec_runaway"));
            }
            Assert.That(await WaitUntilAsync(() => rig.NotificationTitles().Count == 2).ConfigureAwait(false), Is.True);
            Assert.That(rig.NotificationTitles()[1], Is.EqualTo("Guiding restored — sequence resumed"));
        }

        [Test]
        public async Task Normal_noisy_guiding_never_trips_the_dec_runaway_watch() {
            using var rig = new Rig();
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);

            // A corrected Dec axis: seeing noise and a small drift, the guider correcting 70 % of each error,
            // and one wind gust (1.8 px) that the next frames take back.
            // Box-Muller over a tiny LCG (System.Random trips CA5394): the same noise on every run.
            var state = 1589u;
            double Next() {
                state = (state * 1664525u) + 1013904223u;
                return (state >> 8) / (double)(1 << 24);
            }
            double Gaussian() => Math.Sqrt(-2.0 * Math.Log(1.0 - Next())) * Math.Cos(2.0 * Math.PI * (1.0 - Next()));
            var star = 0.1;
            for (var n = 1; n <= 300; n++) {
                if (n == 150) {
                    star = 1.8;
                }
                var reported = n is 1 or 150 ? star : star + 0.3 * Gaussian();
                var pulse = Math.Min(MaxPulseMs, Aggressiveness * Math.Abs(reported) / (DecRatePxPerSec / 1000));
                await StepAsync(rig, fake, reported, pulse).ConfigureAwait(false);
                star += -Math.Sign(reported) * pulse * DecRatePxPerSec / 1000 + 0.02 * 2;
            }

            AssertNoRunaway(rig, fake);
        }

        [Test]
        public async Task A_dither_and_its_settle_do_not_trip_the_dec_runaway_watch() {
            using var rig = new Rig();
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);

            // A strong Dec drift held at the pulse limit, creeping up but nowhere near three times its start...
            foreach (var dec in new[] { 1.0, 1.0, 1.05, 1.05, 1.1, 1.1, 1.15, 1.15, 1.2 }) {
                await StepAsync(rig, fake, dec, MaxPulseMs).ConfigureAwait(false);
            }
            // ...then a 5 px dither: the jump grows the error at the limit, and the settle takes it back.
            await fake.BroadcastAsync(PhdEvents.GuidingDithered(0, 5)).ConfigureAwait(false);
            foreach (var (dec, pulse) in new[] { (5.0, 2500.0), (4.0, 2500.0), (3.0, 2500.0), (2.2, 2000.0), (1.6, 1500.0), (1.2, 1000.0), (0.8, 700.0) }) {
                await fake.BroadcastAsync(PhdEvents.Settling(distance: dec, time: 2, settleTime: 10)).ConfigureAwait(false);
                await StepAsync(rig, fake, dec, pulse).ConfigureAwait(false);
            }
            await fake.BroadcastAsync(PhdEvents.SettleDone()).ConfigureAwait(false);
            for (var n = 0; n < GuideAxisRunaway.Steps; n++) {
                await StepAsync(rig, fake, 0.3, 50).ConfigureAwait(false);
            }

            AssertNoRunaway(rig, fake);
        }

        [Test]
        public async Task A_short_pulse_limited_burst_that_recovers_is_not_a_runaway() {
            using var rig = new Rig();
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);

            // Ordinary guiding first: the error wanders and the pulses follow it.
            foreach (var dec in new[] { 0.2, -0.15, 0.25, -0.1, 0.18 }) {
                await StepAsync(rig, fake, dec, Aggressiveness * Math.Abs(dec) / (DecRatePxPerSec / 1000)).ConfigureAwait(false);
            }
            // Five frames of a gust at the pulse limit, then the guider catches it.
            foreach (var (dec, pulse) in new[] { (0.5, 2500.0), (1.0, 2500.0), (1.5, 2500.0), (2.0, 2500.0), (2.5, 2500.0),
                    (1.8, 2500.0), (1.0, 2000.0), (0.5, 1000.0), (0.3, 500.0) }) {
                await StepAsync(rig, fake, dec, pulse).ConfigureAwait(false);
            }
            for (var n = 0; n < 2 * GuideAxisRunaway.Steps; n++) {
                var dec = n % 2 == 0 ? 0.2 : -0.15;
                await StepAsync(rig, fake, dec, Aggressiveness * Math.Abs(dec) / (DecRatePxPerSec / 1000)).ConfigureAwait(false);
            }

            AssertNoRunaway(rig, fake);
        }

        [Test]
        public async Task A_steady_drift_at_the_pulse_limit_is_not_a_runaway() {
            using var rig = new Rig();
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);

            for (var n = 0; n < 3 * GuideAxisRunaway.Steps; n++) {
                await StepAsync(rig, fake, 2.0, MaxPulseMs).ConfigureAwait(false);
            }

            AssertNoRunaway(rig, fake);
        }

        // #1589 review — Dec backlash after a centre or flip: the first corrections land in the backlash, so
        // the error creeps up with proportional pulses that are each the largest of the run so far, but far
        // below the guider's Max Dec Duration. PHD2 never reports them Dec-limited, so it is not a runaway.
        [Test]
        public async Task Growing_dec_pulses_below_the_dec_limit_are_not_a_runaway() {
            using var rig = new Rig();
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);

            for (var n = 1; n <= GuideAxisRunaway.Steps + 5; n++) {
                var dec = 0.1 + 0.1 * n; // 0.2, 0.3 ... 1.6 px: the pulses are all lost in the backlash
                await StepAsync(rig, fake, dec, Aggressiveness * dec / (DecRatePxPerSec / 1000)).ConfigureAwait(false);
            }

            AssertNoRunaway(rig, fake);
        }

        [Test]
        public async Task An_RA_runaway_is_not_the_dec_runaway_watch() {
            // RA running away is the mount not tracking: the mount's own tracking-lost fault covers it.
            using var rig = new Rig();
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);

            for (var n = 1; n <= GuideAxisRunaway.Steps + 2; n++) {
                await StepAsync(rig, fake, dec: 0.1, decMs: 20, ra: 0.5 * n, raMs: MaxPulseMs).ConfigureAwait(false);
            }

            AssertNoRunaway(rig, fake);
        }

        // #1589 review — skip_target ends the reaction at once. The next target starts guiding on the same
        // calibration, which never settles (the profile's 10 s settle) and runs away again: that runaway is
        // skipped and notified too, not stopped silently under the first fault.
        [TestCase("skip_target")]
        [TestCase("abort_sequence")]
        public async Task After_a_skip_or_abort_a_second_runaway_on_the_next_target_reacts_again(string onGuiderLost) {
            using var rig = new Rig(onGuiderLost, settleTime: 10);
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);
            await RunAwayAsync(rig, fake).ConfigureAwait(false);
            int Acted() => onGuiderLost == "skip_target" ? rig.Skipped() : rig.Aborted();
            Assert.That(await WaitUntilAsync(() => StopCaptures(fake) == 1 && Acted() == 1 && rig.NotificationTitles().Count == 1).ConfigureAwait(false),
                Is.True, "the first runaway stops guiding and acts");

            await StartGuidingAsync(fake).ConfigureAwait(false);
            await RunAwayAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => StopCaptures(fake) == 2).ConfigureAwait(false), Is.True, "the second runaway stops guiding");
            Assert.That(await WaitUntilAsync(() => Acted() == 2).ConfigureAwait(false), Is.True,
                $"the second runaway runs on_guider_lost again: faults={rig.RunawayFaults().Count}");
            Assert.That(rig.RunawayFaults(), Has.Count.EqualTo(2), "a fault of its own");
            Assert.That(await WaitUntilAsync(() => rig.NotificationTitles().Count == 2).ConfigureAwait(false), Is.True, "and a notice of its own");
            lock (rig.Recovered) {
                Assert.That(rig.Recovered.Single(), Is.SameAs(rig.RunawayFaults()[0]), "the first fault is closed for the second");
            }
            Assert.That(rig.Paused(), Is.Zero);
        }

        [Test]
        public async Task With_no_run_going_a_second_runaway_after_a_restart_notifies_again() {
            using var rig = new Rig(runActive: false, settleTime: 10);
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);
            await RunAwayAsync(rig, fake).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.NotificationTitles().Count == 1).ConfigureAwait(false), Is.True);
            Assert.That(rig.NotificationTitles()[0], Is.EqualTo("Dec guiding ran away"), "notify only: no run was going");

            await StartGuidingAsync(fake).ConfigureAwait(false);
            await RunAwayAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => StopCaptures(fake) == 2 && rig.NotificationTitles().Count == 2).ConfigureAwait(false), Is.True,
                $"the second runaway notifies again: faults={rig.RunawayFaults().Count}");
            Assert.That(rig.RunawayFaults(), Has.Count.EqualTo(2));
        }

        [Test]
        public async Task While_the_run_stays_paused_a_second_runaway_only_stops_guiding_again() {
            using var rig = new Rig(settleTime: 10);
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);
            await RunAwayAsync(rig, fake).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1 && rig.NotificationTitles().Count == 1).ConfigureAwait(false), Is.True);

            // The user starts guiding by hand without calibrating again; the run is still paused.
            await StartGuidingAsync(fake).ConfigureAwait(false);
            await RunAwayAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => StopCaptures(fake) == 2).ConfigureAwait(false), Is.True, "guiding is stopped again");
            await Task.Delay(300).ConfigureAwait(false);
            Assert.That(rig.RunawayFaults(), Has.Count.EqualTo(1), "the paused episode still holds the first fault");
            Assert.That(rig.Paused(), Is.EqualTo(1));
            Assert.That(rig.Resumed(), Is.Zero, "the paused run is not resumed");
            Assert.That(rig.NotificationTitles(), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task A_run_resumed_by_hand_is_paused_again_by_a_second_runaway() {
            using var rig = new Rig(settleTime: 10);
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);
            await RunAwayAsync(rig, fake).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1 && rig.NotificationTitles().Count == 1).ConfigureAwait(false), Is.True);

            // The user presses Resume without calibrating again: the run leaves the daemon's pause (a daemon
            // resume of it now resumes nothing), and a dither starts guiding again on the same calibration.
            rig.Sequencer.Setup(s => s.DaemonPausedRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Guid>());
            rig.Sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
            await StartGuidingAsync(fake).ConfigureAwait(false);
            await RunAwayAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => StopCaptures(fake) == 2).ConfigureAwait(false), Is.True, "guiding is stopped again");
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 2).ConfigureAwait(false), Is.True,
                $"the resumed run is paused again: faults={rig.RunawayFaults().Count}");
            var faults = rig.RunawayFaults();
            Assert.That(faults, Has.Count.EqualTo(2), "a new fault for the second runaway");
            lock (rig.Recovered) {
                Assert.That(rig.Recovered.Single(), Is.SameAs(faults[0]), "the first fault is closed for the second");
            }
            Assert.That(await WaitUntilAsync(() => rig.NotificationTitles().Count == 2).ConfigureAwait(false), Is.True);
            Assert.That(rig.NotificationTitles()[1], Is.EqualTo("Dec guiding ran away — sequence paused"));
        }

        // #1589 review — a lost star's episode is still holding the run paused when Dec runs away: the runaway
        // fault joins that episode, so no episode holds it. Guiding is started again on the same calibration
        // and runs away again: the old runaway fault is closed and the second gets a fault of its own.
        [Test]
        public async Task A_runaway_that_joined_a_running_guider_episode_leaves_the_next_runaway_a_fault_of_its_own() {
            using var rig = new Rig(settleTime: 10);
            rig.Service.StarLostGrace = TimeSpan.FromMilliseconds(50);
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True, "the lost star's episode pauses the run");

            await RunAwayAsync(rig, fake).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => StopCaptures(fake) == 1).ConfigureAwait(false), Is.True, "the first runaway stops guiding");
            Assert.That(rig.RunawayFaults(), Has.Count.EqualTo(1));
            Assert.That(rig.Paused(), Is.EqualTo(1), "the runaway joined the lost star's episode: no second pause");

            await StartGuidingAsync(fake).ConfigureAwait(false);
            await RunAwayAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => StopCaptures(fake) == 2).ConfigureAwait(false), Is.True, "the second runaway stops guiding");
            Assert.That(await WaitUntilAsync(() => rig.RunawayFaults().Count == 2).ConfigureAwait(false), Is.True,
                "the second runaway gets a fault of its own: no episode held the first");
            lock (rig.Recovered) {
                Assert.That(rig.Recovered, Has.Member(rig.RunawayFaults()[0]), "the first runaway fault is closed for the second");
            }
        }

        // #1589 review — a paused guider's frames are not guiding: a Dec-limited growing streak sent while
        // PHD2 is paused does not trip the watch.
        [Test]
        public async Task A_dec_limited_growing_streak_while_the_guider_is_paused_does_not_trip() {
            using var rig = new Rig();
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.Paused()).ConfigureAwait(false);

            await RunAwayAsync(rig, fake).ConfigureAwait(false);

            AssertNoRunaway(rig, fake);
        }

        // #1589 review — a resume starts the streak afresh: frames before the pause and frames after the
        // resume do not add up to a runaway.
        [Test]
        public async Task A_growing_streak_across_a_pause_and_resume_does_not_trip() {
            using var rig = new Rig();
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);

            for (var n = 1; n <= 5; n++) {
                await StepAsync(rig, fake, dec: 0.5 * n, decMs: MaxPulseMs).ConfigureAwait(false);
            }
            await fake.BroadcastAsync(PhdEvents.Paused()).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.Resumed()).ConfigureAwait(false);
            for (var n = 6; n <= GuideAxisRunaway.Steps + 2; n++) {
                await StepAsync(rig, fake, dec: 0.5 * n, decMs: MaxPulseMs).ConfigureAwait(false);
            }

            AssertNoRunaway(rig, fake);
        }

        // Connected again to the same fake (PHD2 still guiding); no guide step, so a streak is not broken.
        private static async Task ReconnectAsync(Rig rig, FakeGuider fake) {
            await rig.Service.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Service.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected).ConfigureAwait(false),
                Is.True, "the guider never reached Connected again");
        }

        // #1589 review — a user disconnect drops the open runaway fault without Recovered: settled guiding
        // after the reconnect has no old fault to recover.
        [Test]
        public async Task A_user_disconnect_drops_the_open_runaway_fault_without_recovering_it() {
            using var rig = new Rig();
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);
            await RunAwayAsync(rig, fake).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => StopCaptures(fake) == 1 && rig.Paused() == 1).ConfigureAwait(false), Is.True);

            await rig.Service.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            await ReconnectAsync(rig, fake).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);
            await StepAsync(rig, fake, dec: 0.1, decMs: 20).ConfigureAwait(false);
            await StepAsync(rig, fake, dec: 0.1, decMs: 20).ConfigureAwait(false);

            lock (rig.Recovered) {
                Assert.That(rig.Recovered, Is.Empty, "the user took the guider back: the fault is dropped, not recovered");
            }
            Assert.That(rig.Resumed(), Is.Zero, "the run stays paused");
        }

        // #1589 review — a new guider connection watches afresh: frames before a reconnect and frames after it
        // do not add up to a runaway.
        [Test]
        public async Task A_growing_streak_across_a_guider_reconnect_does_not_trip() {
            using var rig = new Rig();
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await StartGuidingAsync(fake).ConfigureAwait(false);

            for (var n = 1; n <= 5; n++) {
                await StepAsync(rig, fake, dec: 0.5 * n, decMs: MaxPulseMs).ConfigureAwait(false);
            }
            await rig.Service.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            await ReconnectAsync(rig, fake).ConfigureAwait(false);
            for (var n = 6; n <= GuideAxisRunaway.Steps + 2; n++) {
                await StepAsync(rig, fake, dec: 0.5 * n, decMs: MaxPulseMs).ConfigureAwait(false);
            }

            AssertNoRunaway(rig, fake);
        }
    }
}
