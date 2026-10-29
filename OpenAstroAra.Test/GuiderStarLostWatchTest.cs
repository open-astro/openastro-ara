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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.Server.Contracts;
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
    /// §42.2 "Guider (PHD2): loses guide star" against the fake guider: a star lost for longer than the
    /// grace period pauses the active runs through the on_guider_lost reaction, and the first guide step
    /// afterwards resumes exactly those runs. A star that comes back inside the grace does nothing.
    /// </summary>
    [TestFixture]
    [Category("IO")] // loopback socket against the fake guider
    public class GuiderStarLostWatchTest {

        private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

        private sealed class Rig : IDisposable {
            public readonly Mock<ISequencerService> Sequencer = new();
            public readonly List<NotificationDto> Notifications = new();
            public readonly List<string> WsTopics = new();
            public readonly Guid RunId = Guid.NewGuid();
            public readonly Mock<IProfileStore> Profiles = new();
            public readonly Mock<ICenteringService> Centering = new();
            public readonly List<string> FaultActions = new();
            public int Restarts;
            public bool RestartSucceeds = true;
            public bool RestartStartsButFails; // PHD2 guides but the settle timed out: StartGuiding returns false
            public Func<Task>? OnRestart; // the fake's side of a restart: app state Guiding, StartGuiding, a guide step
            public GuiderService Service = null!;

            public Rig(bool runActive) {
                var profiles = Profiles;
                profiles.Setup(p => p.GetSafetyPolicies()).Returns(new SafetyPoliciesDto(
                    OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                    MeridianFlipAuto: true, MeridianPauseMin: 2, MeridianRecenter: true, MeridianRecalGuider: false,
                    OnAltitudeLimit: "pause", ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                    GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: false));
                Sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(runActive ? new List<Guid> { RunId } : new List<Guid>());
                Sequencer.Setup(s => s.PauseActiveRunsAsync(true, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(runActive ? new List<Guid> { RunId } : new List<Guid>());
                Sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => ids.Count);
                // Until the user acts, the star-lost pause is still the daemon's.
                Sequencer.Setup(s => s.DaemonPausedRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => (IReadOnlyList<Guid>) ids.ToList());
                Sequencer.Setup(s => s.GetActiveTargetCoordinatesAsync(RunId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Target);
                Centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>?>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PlateSolveResult());
                var faultLog = new Mock<IFaultLogService>();
                faultLog.Setup(f => f.RecordActionAsync(It.IsAny<EquipmentFaultEvent>(), It.IsAny<string>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
                    .Callback<EquipmentFaultEvent, string, DateTimeOffset?, CancellationToken>((_, a, _, _) => { lock (FaultActions) { FaultActions.Add(a); } })
                    .Returns(Task.CompletedTask);
                var notifications = new Mock<INotificationService>();
                notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                    .Callback<NotificationDto, CancellationToken>((n, _) => { lock (Notifications) { Notifications.Add(n); } })
                    .Returns(Task.CompletedTask);
                var ws = new Mock<IWsBroadcaster>();
                ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<System.Text.Json.JsonElement>(), It.IsAny<CancellationToken>()))
                    .Callback<string, System.Text.Json.JsonElement, CancellationToken>((t, _, _) => { lock (WsTopics) { WsTopics.Add(t); } })
                    .Returns(Task.CompletedTask);
                Service = new GuiderService(
                    new HeadlessProfileService(),
                    new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(),
                        Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
                    NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(), ws.Object,
                    profiles.Object, () => Sequencer.Object, notifications.Object, faultLog.Object) {
                    StarLostGrace = Grace,
                    StarLostRecoverAfter = Grace,
                    StarLostProbeInterval = Grace / 2,
                    StarLostRetryBackoff = new[] { Grace / 2 },
                    StarLostRecenterTimeout = TimeSpan.FromSeconds(5),
                };
                Service.Recenter = new TargetRecenter(() => Centering.Object, profiles.Object);
                Service.RestartGuidingOverride = async _ => {
                    Interlocked.Increment(ref Restarts);
                    if ((RestartSucceeds || RestartStartsButFails) && OnRestart is { } hook) {
                        await hook().ConfigureAwait(false);
                    }
                    return RestartSucceeds && !RestartStartsButFails;
                };
            }

            public static readonly Coordinates Target = new(ra: 83.8, dec: 0.0, Epoch.J2000, Coordinates.RAType.Degrees);
            public int Centered() => Centering.Invocations.Count(i => i.Method.Name == nameof(ICenteringService.CenterOnTarget));

            public int Paused() => Sequencer.Invocations.Count(i => i.Method.Name == nameof(ISequencerService.PauseActiveRunsAsync));
            public int Resumed() => Sequencer.Invocations.Count(i => i.Method.Name == nameof(ISequencerService.ResumeRunsAsync));
            public void Dispose() => Service.Dispose();
        }

        // Longer than the recovery's own bounds: stopping PHD2 before a re-center waits up to 10 s for the
        // stop_capture reply and up to 10 s more for the Stopped state (PHD2Guider.StopCapture). A 10 s wait
        // gave up while a loaded CI runner was still inside those bounds (2026-10-28, three PRs in a row);
        // a passing run returns as soon as the condition holds, so only a real failure waits this long.
        private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 30_000) {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline) {
                if (condition()) {
                    return true;
                }
                await Task.Delay(25).ConfigureAwait(false);
            }
            return condition();
        }

        private static async Task<FakeGuider> GuidingFakeAsync(Rig rig) {
            var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Guiding"));
            fake.OnRpc("get_app_state", JsonValue.Create("Guiding"));
            fake.OnRpc("get_connected", JsonValue.Create(true));
            // The guider's Stop button waits for the app state to read Stopped, and a stop from LostLock
            // sends GuidingStopped (the marker the recovery must swallow exactly once); loop reports
            // Looping. The fake follows real PHD2 on all three.
            // The events go out BEFORE the answer, as real PHD2 has emitted them by the time the stop is
            // done: sent from a fire-and-forget task after it, a late AppState(Stopped) could overtake the
            // restart's AppState(Guiding) on the event stream (the re-center is instant here), leave the
            // client reading Stopped, and hang the restart's wait — the CI flake of 2026-10-28. A write
            // only waits for the socket buffer, never for the client to handle the event.
            fake.OnRpc("stop_capture", req => {
                fake.BroadcastAsync(PhdEvents.GuidingStopped()).GetAwaiter().GetResult();
                fake.BroadcastAsync(PhdEvents.AppState("Stopped")).GetAwaiter().GetResult();
                fake.OnRpc("get_app_state", JsonValue.Create("Stopped"));
                return JsonValue.Create(0);
            });
            fake.OnRpc("loop", req => {
                fake.BroadcastAsync(PhdEvents.AppState("Looping")).GetAwaiter().GetResult();
                fake.OnRpc("get_app_state", JsonValue.Create("Looping"));
                return JsonValue.Create(0);
            });
            // A restart that worked: PHD2 is guiding again and its first guide step lands while the
            // recovery is still in flight (the in-flight guard must not read it as a relock).
            rig.OnRestart = async () => {
                var guider = rig.Service.RequireConnectedGuider();
                fake.OnRpc("get_app_state", JsonValue.Create("Guiding"));
                await fake.BroadcastAsync(PhdEvents.AppState("Guiding")).ConfigureAwait(false);
                await fake.BroadcastAsync(PhdEvents.StartGuiding()).ConfigureAwait(false);
                await fake.BroadcastAsync(PhdEvents.GuideStep(0.02, 0.02)).ConfigureAwait(false);
                // Real PHD2's settle wait is long enough for its events to land; the fake makes sure.
                await WaitUntilAsync(() => guider.AppState?.State == "Guiding").ConfigureAwait(false);
            };
            fake.OnRpc("get_star_centroids", _ => new JsonArray());
            await rig.Service.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Service.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected).ConfigureAwait(false),
                Is.True, "the guider never reached Connected against the fake");
            // A guide step: the watch only concerns a star lost from a GUIDING session.
            await fake.BroadcastAsync(PhdEvents.GuideStep(0.05, 0.05)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Service.GetAsync(CancellationToken.None).Result?.Runtime.RmsTotal is not null).ConfigureAwait(false),
                Is.True, "the guide step never landed");
            return fake;
        }

        [Test]
        public async Task The_watch_stands_down_while_the_guiding_assistant_runs_and_returns_after_it() {
            // The assistant's "Guiding" has corrections off and loses its star on purpose now and then;
            // a run must not be paused for it (2026-10-18 ga-side). After Done the watch works as before.
            using var rig = new Rig(runActive: true);
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            fake.OnRpc("guiding_assistant_start", _ => new JsonObject { ["state"] = "measuring", ["duration_s"] = 60 });
            await rig.Service.RequireConnectedGuider().StartGuidingAssistantAsync(60, measureBacklash: false, CancellationToken.None).ConfigureAwait(false);

            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            await Task.Delay(Grace * 4).ConfigureAwait(false);
            Assert.That(rig.Paused(), Is.EqualTo(0), "a star lost during the assistant pauses nothing");

            await fake.BroadcastAsync(new JsonObject { ["Event"] = "GuidingAssistantDone", ["state"] = "done" }).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.GuideStep(0.05, 0.05)).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True, "after the assistant the watch pauses the run again");
        }

        [Test]
        public async Task A_star_lost_past_the_grace_pauses_the_run_and_the_next_guide_step_resumes_it() {
            using var rig = new Rig(runActive: true);
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);

            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True, "the run was not paused after the grace");
            rig.Sequencer.Verify(s => s.PauseActiveRunsAsync(true, It.IsAny<CancellationToken>()), Times.Once, "a star-lost pause is adoptable by a later daemon pause");
            Assert.That(await WaitUntilAsync(() => rig.Notifications.Count == 1).ConfigureAwait(false), Is.True);
            Assert.That(rig.Notifications[0].Title, Is.EqualTo("Guide star lost — sequence paused"));
            Assert.That(rig.WsTopics, Does.Contain(OpenAstroAra.Server.Contracts.WsEvents.WsEventCatalog.GuiderFaultActionTaken));

            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.1)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the first guide step must resume the paused run");
            rig.Sequencer.Verify(s => s.ResumeRunsAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == rig.RunId), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(await WaitUntilAsync(() => rig.Notifications.Count == 2).ConfigureAwait(false), Is.True);
            Assert.That(rig.Notifications[1].Title, Does.StartWith("Guide star back"));

            // A second episode in the same session reacts again: clouds pass more than once a night.
            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 2).ConfigureAwait(false), Is.True, "the watch is not one-shot per connect");
        }

        [Test]
        public async Task A_star_that_comes_back_inside_the_grace_does_nothing() {
            using var rig = new Rig(runActive: true);
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);

            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            await Task.Delay(Grace / 3).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.1)).ConfigureAwait(false);
            await Task.Delay(Grace * 3).ConfigureAwait(false);

            Assert.That(rig.Paused(), Is.Zero, "a brief loss is PHD2's business");
            Assert.That(rig.Resumed(), Is.Zero);
            Assert.That(rig.Notifications, Is.Empty);
        }

        [Test]
        public async Task A_star_lost_while_merely_looping_does_not_pause_the_run() {
            // PHD2 also sends StarLost while looping with a selected star, before any guiding.
            using var rig = new Rig(runActive: true);
            var fake = FakeGuider.Start();
            await using var _ = fake;
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Looping"));
            fake.OnRpc("get_app_state", JsonValue.Create("Looping"));
            fake.OnRpc("get_connected", JsonValue.Create(true));
            await rig.Service.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Service.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.True);

            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            await Task.Delay(Grace * 3).ConfigureAwait(false);

            Assert.That(rig.Paused(), Is.Zero, "no guiding session was lost");
            Assert.That(rig.Notifications, Is.Empty);
        }

        [Test]
        public async Task With_no_run_active_a_lost_star_is_recorded_quietly_and_recovery_says_nothing() {
            using var rig = new Rig(runActive: false);
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);

            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True);
            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.1)).ConfigureAwait(false);
            await Task.Delay(Grace).ConfigureAwait(false);

            Assert.That(rig.Resumed(), Is.Zero, "nothing was parked, nothing to resume");
            Assert.That(rig.Notifications, Is.Empty, "no sequence was affected: the guider chip already shows the state");
        }

        // ── §42.2 long outage: the recovery ───────────────────────────────────────────────────────

        private static JsonArray Stars(double snr = 25) => new(new JsonObject { ["x"] = 400.0, ["y"] = 300.0, ["snr"] = snr, ["mass"] = 900.0, ["hfd"] = 2.4 });

        private static async Task<FakeGuider> LostStarPastTheGraceAsync(Rig rig) {
            var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True, "the run was not paused after the grace");
            return fake;
        }

        [Test]
        public async Task A_star_still_lost_when_stars_are_back_on_the_frame_is_recovered_by_re_centering_and_the_run_resumes() {
            using var rig = new Rig(runActive: true);
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the recovery never resumed the run");

            Assert.That(fake.ReceivedMethods, Does.Contain("stop_capture"), "the guider was stopped before the slew");
            Assert.That(rig.Centered(), Is.EqualTo(1), "one re-center on the active target");
            rig.Centering.Verify(c => c.CenterOnTarget(It.Is<Coordinates>(x => x.RADegrees == Rig.Target.RADegrees && x.Dec == Rig.Target.Dec),
                It.IsAny<IProgress<PlateSolveProgress>?>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(rig.Restarts, Is.EqualTo(1), "guiding restarted once, after the re-center");
            rig.Sequencer.Verify(s => s.ResumeRunsAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == rig.RunId), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(await WaitUntilAsync(() => rig.Notifications.Count == 2).ConfigureAwait(false), Is.True);
            Assert.That(rig.Notifications[1].Title, Does.StartWith("Guide star lost for").And.Contains("re-centered, run resumed"));
            Assert.That(rig.Notifications[1].Severity, Is.EqualTo(NotificationSeverity.Info));
            Assert.That(await WaitUntilAsync(() => rig.FaultActions.Contains("recentered")).ConfigureAwait(false), Is.True, "the fault row carries the action");

            // Guide steps from the restarted guider are not a second relock: nothing resumes twice.
            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.1)).ConfigureAwait(false);
            await Task.Delay(Grace).ConfigureAwait(false);
            Assert.That(rig.Resumed(), Is.EqualTo(1));
            Assert.That(rig.Notifications, Has.Count.EqualTo(2));

            // A second cloud later in the night is a new episode: it escalates again.
            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 2).ConfigureAwait(false), Is.True);
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 2).ConfigureAwait(false), Is.True, "the second episode recovered too");
            Assert.That(rig.Centered(), Is.EqualTo(2));
        }

        [Test]
        public async Task While_the_guide_frame_stays_starless_nothing_moves_and_the_probe_keeps_asking() {
            using var rig = new Rig(runActive: true);
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => fake.ReceivedMethods.Count(m => m == "get_star_centroids") >= 3).ConfigureAwait(false), Is.True, "the probe repeats while cloudy");
            Assert.That(fake.ReceivedMethods, Does.Not.Contain("stop_capture"), "a cloudy sky is not a reason to stop the guider");
            Assert.That(rig.Centered(), Is.Zero);
            Assert.That(rig.Resumed(), Is.Zero);
            Assert.That(rig.Notifications, Has.Count.EqualTo(1), "only the pause was announced");

            // The guider's own relock still resumes, as before.
            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.1)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True);
            var probes = fake.ReceivedMethods.Count(m => m == "get_star_centroids");
            await Task.Delay(Grace * 3).ConfigureAwait(false);
            Assert.That(fake.ReceivedMethods.Count(m => m == "get_star_centroids"), Is.EqualTo(probes), "the relock disarmed the probe");
            Assert.That(rig.Centered(), Is.Zero, "a relock never re-centers");
        }

        [Test]
        public async Task A_star_back_before_the_recovery_wait_resumes_the_old_way_without_a_re_center() {
            using var rig = new Rig(runActive: true);
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.1)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True);
            await Task.Delay(Grace * 3).ConfigureAwait(false);

            Assert.That(rig.Centered(), Is.Zero);
            Assert.That(rig.Restarts, Is.Zero);
            Assert.That(fake.ReceivedMethods, Does.Not.Contain("get_star_centroids"), "no probe ran: the star was back in time");
            Assert.That(rig.Notifications[1].Title, Does.StartWith("Guide star back"));
        }

        [Test]
        public async Task A_failed_re_center_keeps_the_run_paused_warns_once_and_a_later_attempt_resumes() {
            using var rig = new Rig(runActive: true);
            var solves = 0;
            rig.Centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>?>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new PlateSolveResult { Success = ++solves >= 3 });
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the third attempt resumed the run");

            Assert.That(solves, Is.EqualTo(3), "two failed solves, then one that worked");
            Assert.That(rig.Restarts, Is.EqualTo(1), "guiding is only restarted after a successful re-center");
            Assert.That(fake.ReceivedMethods, Does.Contain("loop"), "between attempts the guider loops so the probe has frames");
            Assert.That(await WaitUntilAsync(() => rig.Notifications.Count == 3).ConfigureAwait(false), Is.True);
            Assert.That(rig.Notifications[1].Title, Does.Contain("re-center failed"));
            Assert.That(rig.Notifications[1].Severity, Is.EqualTo(NotificationSeverity.Warning));
            Assert.That(rig.Notifications[2].Title, Does.Contain("run resumed"));
            Assert.That(rig.Notifications.Count(n => n.Title.Contains("re-center failed", StringComparison.Ordinal)), Is.EqualTo(1), "one warning per episode, not one per retry");
            Assert.That(rig.FaultActions, Does.Contain("recenter_failed"));
        }

        [Test]
        public async Task A_restart_that_guides_but_does_not_settle_is_left_guiding_and_its_relock_resumes_the_run() {
            // Common after cloud: PHD2 starts guiding, the settle times out, StartGuiding returns false.
            // Putting it back to looping would stop guiding, and that guiding_stopped would read as the
            // user's and end the retry loop with the run paused all night (review round 5).
            using var rig = new Rig(runActive: true) { RestartStartsButFails = true };
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            Assert.That(await WaitUntilAsync(() => rig.Notifications.Count >= 2).ConfigureAwait(false), Is.True);
            Assert.That(rig.Notifications[1].Title, Does.Contain("re-center failed"));
            Assert.That(rig.Restarts, Is.EqualTo(1));
            Assert.That(fake.ReceivedMethods, Does.Not.Contain("loop"), "a guiding PHD2 is left guiding");

            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.1)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "its guide step is a relock on the re-centered target");
            Assert.That(await WaitUntilAsync(() => rig.Notifications.Count == 3).ConfigureAwait(false), Is.True);
            Assert.That(rig.Notifications[2].Title, Does.StartWith("Guide star back"));
            Assert.That(rig.Centered(), Is.EqualTo(1));
        }

        [Test]
        public async Task Once_the_user_has_acted_on_the_pause_the_recovery_hands_the_episode_over() {
            // The user presses Resume between the probe and the attempt: the re-check before the stop
            // sees the run released and nothing of the rig is touched.
            using var rig = new Rig(runActive: true);
            var asked = 0;
            rig.Sequencer.Setup(s => s.DaemonPausedRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => ++asked == 1 ? (IReadOnlyList<Guid>) ids.ToList() : Array.Empty<Guid>());
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            Assert.That(await WaitUntilAsync(() => fake.ReceivedMethods.Contains("get_star_centroids")).ConfigureAwait(false), Is.True);
            await Task.Delay(Grace * 3).ConfigureAwait(false);

            Assert.That(fake.ReceivedMethods, Does.Not.Contain("stop_capture"), "the user's run is not the daemon's to stop guiding on");
            Assert.That(rig.Centered(), Is.Zero);
            Assert.That(rig.Resumed(), Is.Zero);
            Assert.That(rig.Notifications, Has.Count.EqualTo(1));
            Assert.That(fake.ReceivedMethods.Count(m => m == "get_star_centroids"), Is.EqualTo(1), "the episode ended: no more probes");
        }

        [Test]
        public async Task Once_the_user_has_acted_the_probe_stops_even_under_cloud() {
            // A stopped-by-hand guider answers the probe with an error, read as "cloudy": without this
            // check the probe would repeat every minute until a disconnect.
            using var rig = new Rig(runActive: true);
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            rig.Sequencer.Setup(s => s.DaemonPausedRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Guid>());

            await Task.Delay(Grace * 4).ConfigureAwait(false);

            Assert.That(fake.ReceivedMethods, Does.Not.Contain("get_star_centroids"), "the episode closed before the first probe");
            Assert.That(rig.Resumed(), Is.Zero);
            Assert.That(rig.Notifications, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task While_the_safety_engine_holds_the_rig_the_recovery_waits() {
            using var rig = new Rig(runActive: true);
            var holding = true;
            rig.Service.SafetyHoldsRig = () => holding;
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            await Task.Delay(Grace * 4).ConfigureAwait(false);
            Assert.That(fake.ReceivedMethods, Does.Not.Contain("get_star_centroids"), "not even a probe while safety owns the rig");
            Assert.That(rig.Centered(), Is.Zero);

            holding = false;
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "once safety lets go the recovery runs");
        }

        [Test]
        public async Task With_no_way_to_re_center_the_run_stays_paused_and_the_notice_says_why() {
            using var rig = new Rig(runActive: true);
            rig.Service.Recenter = null;
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            Assert.That(await WaitUntilAsync(() => rig.Notifications.Count == 2).ConfigureAwait(false), Is.True);
            Assert.That(rig.Notifications[1].Title, Does.Contain("cannot re-center"));
            Assert.That(rig.Notifications[1].Message, Does.Contain("no plate solver"));
            await Task.Delay(Grace * 3).ConfigureAwait(false);
            Assert.That(rig.Resumed(), Is.Zero);
            Assert.That(rig.Restarts, Is.Zero);
            Assert.That(fake.ReceivedMethods.Count(m => m == "get_star_centroids"), Is.EqualTo(1), "given up: no more probes this episode");
            Assert.That(rig.FaultActions, Does.Contain("recenter_unavailable"));
        }

        [Test]
        public async Task A_target_that_has_set_below_the_horizon_limit_is_not_slewed_to() {
            using var rig = new Rig(runActive: true);
            // A near-pole site: the dec-0° target hugs the horizon, so a +20° floor makes it too low.
            rig.Profiles.Setup(p => p.GetSiteSettings()).Returns(new SiteSettingsDto("pole", 89.9, 0, 0, "UTC", false, 20, 4, 2.0, "astronomical"));
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            Assert.That(await WaitUntilAsync(() => rig.Notifications.Count >= 2).ConfigureAwait(false), Is.True);
            Assert.That(rig.Notifications[1].Title, Does.Contain("target is low"));
            Assert.That(rig.Centered(), Is.Zero, "no slew to a target below the limit");
            // The guider was stopped for the attempt, so it is restarted on the give-up; the fake relocks
            // at once and the old path resumes the run, with its own notice after the give-up's.
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "a relock after the give-up still resumes the run");
            Assert.That(await WaitUntilAsync(() => rig.Notifications.Count == 3).ConfigureAwait(false), Is.True);
            Assert.That(rig.Notifications[2].Title, Does.StartWith("Guide star back"));
        }

        [Test]
        public async Task A_second_episode_after_a_given_up_one_starts_clean_and_recovers() {
            // Episode 1 gives up (no way to re-center); the star comes back on its own. Episode 2 under
            // the next cloud must probe and recover: the give-up belonged to the previous cloud.
            using var rig = new Rig(runActive: true);
            var recenter = rig.Service.Recenter;
            rig.Service.Recenter = null;
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());
            Assert.That(await WaitUntilAsync(() => rig.Notifications.Count == 2).ConfigureAwait(false), Is.True);
            Assert.That(rig.Notifications[1].Title, Does.Contain("cannot re-center"));
            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.1)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the relock still resumes a given-up episode");

            rig.Service.Recenter = recenter;
            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 2).ConfigureAwait(false), Is.True);
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 2).ConfigureAwait(false), Is.True, "the second episode probed and recovered");
            Assert.That(rig.Centered(), Is.EqualTo(1), "re-centered once, in the second episode");
        }

        [Test]
        public async Task A_second_episode_after_a_failed_attempt_warns_again_from_a_fresh_backoff() {
            using var rig = new Rig(runActive: true);
            var solves = 0;
            rig.Centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>?>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new PlateSolveResult { Success = ++solves % 2 == 0 }); // fail, succeed, fail, succeed
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True);

            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 2).ConfigureAwait(false), Is.True);
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 2).ConfigureAwait(false), Is.True);

            Assert.That(rig.Notifications.Count(n => n.Title.Contains("re-center failed", StringComparison.Ordinal)), Is.EqualTo(2), "one warning per episode: the latch resets with the episode");
        }

        [Test]
        public async Task A_stop_by_the_user_during_the_wait_ends_the_probing() {
            using var rig = new Rig(runActive: true);
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            await fake.BroadcastAsync(PhdEvents.GuidingStopped()).ConfigureAwait(false);
            await Task.Delay(Grace * 4).ConfigureAwait(false);

            Assert.That(fake.ReceivedMethods, Does.Not.Contain("get_star_centroids"), "the user stopped guiding: the pause is theirs");
            Assert.That(rig.Centered(), Is.Zero);
            Assert.That(rig.Resumed(), Is.Zero);
        }

        [Test]
        public async Task The_guider_remembers_when_guiding_stopped_until_it_guides_again() {
            // IGuiderMediator.GuidingStoppedUtc — the Dither's long-gap input, set from PHD2's own events.
            using var rig = new Rig(runActive: true);
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            Assert.That(rig.Service.GuidingStoppedUtc, Is.Null, "guiding: no stop on record");

            await fake.BroadcastAsync(PhdEvents.GuidingStopped()).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Service.GuidingStoppedUtc is not null).ConfigureAwait(false), Is.True);
            var stopped = rig.Service.GuidingStoppedUtc!.Value;
            Assert.That(stopped, Is.EqualTo(DateTimeOffset.UtcNow).Within(TimeSpan.FromSeconds(5)));

            await fake.BroadcastAsync(PhdEvents.GuidingStopped()).ConfigureAwait(false);
            await Task.Delay(50).ConfigureAwait(false);
            Assert.That(rig.Service.GuidingStoppedUtc, Is.EqualTo(stopped), "a second stop keeps the first time: the gap runs from the real stop");

            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.1)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Service.GuidingStoppedUtc is null).ConfigureAwait(false), Is.True, "a guide step clears it");

            await fake.BroadcastAsync(PhdEvents.GuidingStopped()).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Service.GuidingStoppedUtc is not null).ConfigureAwait(false), Is.True);
            await fake.BroadcastAsync(PhdEvents.StartGuiding()).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Service.GuidingStoppedUtc is null).ConfigureAwait(false), Is.True, "guiding_started clears it");
        }

        [Test]
        public async Task A_disconnect_during_the_wait_disarms_the_recovery() {
            using var rig = new Rig(runActive: true);
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            await rig.Service.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(Grace * 4).ConfigureAwait(false);

            Assert.That(rig.Centered(), Is.Zero);
            Assert.That(rig.Resumed(), Is.Zero);
        }
    }
}
