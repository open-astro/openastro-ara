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
    /// #2169 — the guider link goes down during a guided light (rig night 2026-11-21: the guider was killed
    /// mid-frame). The pause stops the light at once (not saved, not counted), and when the §63.3
    /// auto-reconnect brings the guider back with guiding stopped, guiding is started again and settles
    /// before the run resumes and takes the frame again. Against the fake guider and the real shared reaction.
    /// </summary>
    [TestFixture]
    [Category("IO")] // loopback socket against the fake guider
    public class GuiderLinkLostGuidingTest {

        private const string RestoredTitle = "Guiding restored — sequence resumed";
        private const string StillTryingTitle = "Guiding not back yet — still trying, sequence stays paused";
        private const string CameraStillTryingTitle = "Guide camera not back yet — still trying, sequence stays paused";
        private static readonly string[] RestartThenResume = ["restart", "resume"];
        private static readonly string[] RestartRestartResume = ["restart", "restart", "resume"];
        private static readonly string[] CheckThenRestartThenResume = ["camera checked", "restart", "resume"];
        private static readonly string[] ThreeRestartsThenResume = ["restart", "restart", "restart", "resume"];

        private sealed class Rig : IDisposable {
            public readonly Mock<ISequencerService> Sequencer = new();
            public readonly List<NotificationDto> Notifications = new();
            public readonly List<string> Steps = new(); // "restart" and "resume", in the order they happened
            public readonly List<EquipmentFaultEvent> Faults = new(); // every fault the guider published
            public readonly List<EquipmentFaultEvent> Recovered = new(); // every fault the guider recovered
            public readonly Guid RunId = Guid.NewGuid();
            public readonly HeadlessProfileService Profile = new();
            public readonly FaultReactionService Reaction;
            public readonly Mock<IAlpacaManagementClient> Alpaca = new();
            public Func<int, bool> RestartSucceeds = _ => true; // whether restart n (from 1) is taken by the guider
            public Func<double, Task>? OnRestart; // the fake's side of a restart: app state Guiding, a guide step of that distance
            public Func<int, bool> Settles = _ => true; // whether restart n (from 1) brings guiding back and settled
            public bool GuidesUnsettled; // a restart that does not settle still guides, far from the lock position
            public GuiderService Service = null!;

            public Rig() {
                Profile.ActiveProfile.GuiderSettings.SettleTime = 0;
                var profiles = new Mock<IProfileStore>();
                profiles.Setup(p => p.GetSafetyPolicies()).Returns(new SafetyPoliciesDto(
                    OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                    MeridianFlipAuto: true, MeridianPauseMin: 2, MeridianRecenter: true, MeridianRecalGuider: false,
                    OnAltitudeLimit: "pause", ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                    GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: false));
                var run = new List<Guid> { RunId };
                Sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(run);
                Sequencer.Setup(s => s.PauseActiveRunsAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(run);
                Sequencer.Setup(s => s.PauseActiveRunsAsync(false, true, It.IsAny<CancellationToken>())).ReturnsAsync(run);
                Sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                    .Callback(() => { lock (Steps) { Steps.Add("resume"); } })
                    .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => ids.Count);
                Sequencer.Setup(s => s.ActiveRunIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(run);
                Sequencer.Setup(s => s.DaemonPausedRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => (IReadOnlyList<Guid>)ids.ToList());
                var notifications = new Mock<INotificationService>();
                notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                    .Callback<NotificationDto, CancellationToken>((n, _) => { lock (Notifications) { Notifications.Add(n); } })
                    .Returns(Task.CompletedTask);
                var ws = new Mock<IWsBroadcaster>();
                ws.Setup(w => w.PublishAsync(It.IsAny<OpenAstroAra.Server.Contracts.WsEvents.WsEvent>(), It.IsAny<System.Text.Json.JsonElement>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);
                var hub = new EquipmentFaultHub(ws.Object);
                hub.Subscribe(f => { lock (Faults) { Faults.Add(f); } });
                hub.SubscribeRecovered(f => { lock (Recovered) { Recovered.Add(f); } });
                Reaction = new FaultReactionService(hub, reconnector: null, profiles.Object, () => Sequencer.Object,
                    telescope: null, notifications.Object, ws.Object, guiderEpisodes: () => Service);
                // No fixed waits: the retries come quickly here.
                Reaction.GuiderRetryBackoff = [TimeSpan.FromMilliseconds(50)];
                Reaction.ReleasePollInterval = TimeSpan.FromMilliseconds(50);
                Reaction.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                Service = new GuiderService(
                    Profile,
                    new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(),
                        Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
                    NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(), ws.Object,
                    profiles.Object, () => Sequencer.Object, notifications.Object, hub, alpaca: Alpaca.Object) {
                    // Generous: a test that wants the timeout sets its own, so suite load never fails a good restore.
                    GuidingRestoreSettleTimeout = TimeSpan.FromSeconds(30),
                    GuidingRestoreReconcileWait = TimeSpan.FromSeconds(30),
                    GuideCameraRetryBackoff = [TimeSpan.FromMilliseconds(50)],
                };
                Service.RestartGuidingOverride = async _ => {
                    int n;
                    lock (Steps) { Steps.Add("restart"); n = Steps.Count(s => s == "restart"); }
                    // A refused start still leaves PHD2 guiding and settled, so only its answer can fail the restore.
                    if (OnRestart is { } hook) {
                        if (Settles(n)) {
                            await hook(0.02).ConfigureAwait(false);
                        } else if (GuidesUnsettled) {
                            await hook(5.0).ConfigureAwait(false);
                        }
                    }
                    return RestartSucceeds(n);
                };
            }

            public int Restarts() { lock (Steps) { return Steps.Count(s => s == "restart"); } }
            // From the resume's own callback: Moq lists an invocation before its callback runs.
            public int Resumed() { lock (Steps) { return Steps.Count(s => s == "resume"); } }
            public int Paused() => Sequencer.Invocations.Count(i => i.Method.Name == nameof(ISequencerService.PauseActiveRunsAsync));
            public int FramePauses() => Sequencer.Invocations.Count(i => i.Method.Name == nameof(ISequencerService.PauseActiveRunsAsync)
                && i.Arguments.Count == 3 && (bool)i.Arguments[1]!);
            public int Titled(string title) { lock (Notifications) { return Notifications.Count(n => n.Title == title); } }
            public bool WasRecovered(string details) { lock (Recovered) { return Recovered.Any(f => f.Details == details); } }

            public void Dispose() {
                Service.Dispose();
                Reaction.Dispose();
            }
        }

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

        // Connected to the fake; guiding = PHD2 reads Guiding and has sent a guide step.
        private static async Task<FakeGuider> ConnectedFakeAsync(Rig rig, bool guiding) {
            var fake = FakeGuider.Start();
            var state = guiding ? "Guiding" : "Stopped";
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState(state));
            fake.OnRpc("get_app_state", JsonValue.Create(state));
            fake.OnRpc("get_connected", JsonValue.Create(true));
            // A guide frame Ara takes to check a dropped camera comes back (a test that keeps the camera out overrides it).
            fake.OnRpc("capture_single_frame", request => {
                _ = Task.Run(() => fake.BroadcastAsync(SingleFrameComplete(success: true)));
                return JsonValue.Create(0);
            });
            rig.OnRestart = async distance => {
                var guider = rig.Service.RequireConnectedGuider();
                fake.OnRpc("get_app_state", JsonValue.Create("Guiding"));
                await fake.BroadcastAsync(PhdEvents.AppState("Guiding")).ConfigureAwait(false);
                await fake.BroadcastAsync(PhdEvents.StartGuiding()).ConfigureAwait(false);
                await fake.BroadcastAsync(PhdEvents.GuideStep(distance, distance)).ConfigureAwait(false);
                await WaitUntilAsync(() => guider.AppState?.State == "Guiding").ConfigureAwait(false);
            };
            await rig.Service.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Service.GetAsync(CancellationToken.None).Result?.State == EquipmentConnectionState.Connected).ConfigureAwait(false),
                Is.True, "the guider never reached Connected against the fake");
            if (guiding) {
                await fake.BroadcastAsync(PhdEvents.GuideStep(0.05, 0.05)).ConfigureAwait(false);
                Assert.That(await WaitUntilAsync(() => rig.Service.GetAsync(CancellationToken.None).Result?.Runtime.RmsTotal is not null).ConfigureAwait(false),
                    Is.True, "the guide step never landed");
            }
            return fake;
        }

        // The guider process dies; the §63.3 auto-reconnect connects again, and PHD2 is back with guiding stopped.
        private static async Task DropAndReconnectStoppedAsync(Rig rig, FakeGuider fake) {
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_app_state", JsonValue.Create("Stopped"));
            var dropped = rig.Service.RequireConnectedGuider();
            Assert.That(fake.DropConnections(), Is.GreaterThan(0), "expected a live connection to drop");
            object? Current() {
                try {
                    return rig.Service.RequireConnectedGuider();
                } catch (InvalidOperationException) {
                    return null;
                }
            }
            Assert.That(await WaitUntilAsync(() => Current() is { } now && !ReferenceEquals(now, dropped)).ConfigureAwait(false),
                Is.True, "the auto-reconnect never connected a new guider");
        }

        [Test]
        public async Task A_link_lost_while_guiding_pauses_the_run_and_stops_the_light_in_progress() {
            using var rig = new Rig();
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True, "the run was not paused");
            rig.Sequencer.Verify(s => s.PauseActiveRunsAsync(false, true, It.IsAny<CancellationToken>()), Times.Once,
                "guiding was on: the pause stops the light in progress, and is not adoptable");
        }

        [Test]
        public async Task After_a_guided_link_loss_the_reconnect_restarts_guiding_and_it_settles_before_the_run_resumes() {
            using var rig = new Rig();
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the run never resumed");
            lock (rig.Steps) {
                Assert.That(rig.Steps, Is.EqualTo(RestartThenResume), "guiding restarts and settles first, then the run resumes");
            }
            Assert.That(await WaitUntilAsync(() => rig.Titled(RestoredTitle) == 1).ConfigureAwait(false), Is.True);
            Assert.That(rig.Titled(StillTryingTitle), Is.Zero);
        }

        [Test]
        public async Task A_link_lost_while_not_guiding_lets_the_frame_finish_and_the_reconnect_starts_no_guiding() {
            using var rig = new Rig();
            await using var fake = await ConnectedFakeAsync(rig, guiding: false).ConfigureAwait(false);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the run resumes on the reconnect, as before");
            Assert.That(rig.FramePauses(), Is.Zero, "an unguided run's frame is not stopped");
            rig.Sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(rig.Restarts(), Is.Zero, "guiding was off: the reconnect starts none");
        }

        [Test]
        public async Task A_guiding_restart_that_fails_twice_is_tried_again_until_it_works_and_the_user_is_told_once() {
            // #2169 rig check — Ara never gives up after one try: the run stays paused while the start fails
            // (camera still gone, cloud) and resumes once guiding is back and settled.
            using var rig = new Rig { RestartSucceeds = n => n >= 3, Settles = n => n >= 3 };
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the run never resumed after the third try");
            lock (rig.Steps) {
                Assert.That(rig.Steps, Is.EqualTo(ThreeRestartsThenResume), "two failed starts, a third that works, then the resume");
            }
            Assert.That(await WaitUntilAsync(() => rig.Titled(RestoredTitle) == 1).ConfigureAwait(false), Is.True);
            Assert.That(rig.Titled(StillTryingTitle), Is.EqualTo(1), "told once, not on every try");
        }

        [Test]
        public async Task A_user_resume_during_the_guiding_retries_stops_them_with_no_further_guiding_start() {
            // #2169 rig check — the user took the run over while Ara was still trying: guiding is theirs to start.
            using var rig = new Rig { RestartSucceeds = _ => false, Settles = _ => false };
            var userResumed = false;
            rig.Sequencer.Setup(s => s.DaemonPausedRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                    Volatile.Read(ref userResumed) ? Array.Empty<Guid>() : (IReadOnlyList<Guid>)ids.ToList());
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Restarts() >= 2).ConfigureAwait(false), Is.True, "the failed start was never tried again");
            Volatile.Write(ref userResumed, true);
            var startsWhenResumed = rig.Restarts();

            await rig.Reaction.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            // One start may already be in flight when the user resumes; none begins after it.
            Assert.That(rig.Restarts(), Is.InRange(startsWhenResumed, startsWhenResumed + 1), "the retries stop once the user owns the run");
            Assert.That(rig.Resumed(), Is.Zero, "the user resumed it: nothing for Ara to resume");
        }

        [Test]
        public async Task A_star_lost_declaration_stops_the_light_and_its_relock_resumes_without_a_second_guiding_start() {
            using var rig = new Rig();
            rig.Service.StarLostGrace = TimeSpan.FromMilliseconds(300);
            rig.Service.StarLostRecoverAfter = TimeSpan.FromMinutes(10);
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True, "the watch never declared the star lost");
            rig.Sequencer.Verify(s => s.PauseActiveRunsAsync(true, It.IsAny<CancellationToken>()), Times.Once,
                "the star-lost pause is the frame-stopping one");

            await fake.BroadcastAsync(PhdEvents.GuideStep(0.02, 0.02)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the relock never resumed the run");
            Assert.That(rig.Restarts(), Is.Zero, "the guider is guiding already: nothing to restart");
        }

        [Test]
        public async Task A_second_link_loss_during_the_guiding_restart_is_waited_out_and_its_reconnect_restarts_guiding_again() {
            // #2169 review — the link drops again while the first restart waits to settle (PHD2 never got
            // going): that loss is not dropped, its reconnect restarts guiding again, and only then the run resumes.
            using var rig = new Rig { Settles = n => n >= 2 };
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Restarts() == 1).ConfigureAwait(false), Is.True, "the first restart never ran");
            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the run never resumed after the second reconnect");
            lock (rig.Steps) {
                Assert.That(rig.Steps, Is.EqualTo(RestartRestartResume), "the second reconnect restarts guiding before the run resumes");
            }
            Assert.That(rig.Titled(StillTryingTitle), Is.Zero, "the first restart was cut short by the loss, not counted as a failed try");
        }

        [Test]
        public async Task A_quick_unguided_re_drop_leaves_the_first_guided_link_loss_marked_as_guided() {
            // #2169 review — each loss keeps what it saw: a later drop with guiding off does not unmark the first.
            using var rig = new Rig { RestartSucceeds = _ => false, Settles = _ => false };
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Titled(StillTryingTitle) == 1).ConfigureAwait(false), Is.True);
            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);

            EquipmentFaultEvent[] links;
            lock (rig.Faults) {
                links = rig.Faults.Where(f => f.Details == "link_down").ToArray();
            }
            Assert.That(links, Has.Length.EqualTo(2));
            IGuiderEpisodeListener listener = rig.Service;
            Assert.That((listener.GuidedWhenLost(links[0]), listener.GuidedWhenLost(links[1])), Is.EqualTo((true, false)));
        }

        [Test]
        public async Task A_guide_camera_dropped_while_guiding_stops_the_light_and_its_reconnect_restarts_guiding_before_the_run_resumes() {
            // #2169 review — the camera blip takes guiding away like a link loss: the light stops, and the
            // run resumes only once guiding is back and settled.
            using var rig = new Rig();
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await fake.BroadcastAsync(PhdEvents.EquipmentDisconnected()).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True, "the run was not paused");
            rig.Sequencer.Verify(s => s.PauseActiveRunsAsync(false, true, It.IsAny<CancellationToken>()), Times.Once,
                "guiding was on: the pause stops the light in progress");

            fake.OnRpc("get_app_state", JsonValue.Create("Stopped"));
            await fake.BroadcastAsync(PhdEvents.GuidingStopped()).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.EquipmentReconnected()).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the run never resumed");
            lock (rig.Steps) {
                Assert.That(rig.Steps, Is.EqualTo(RestartThenResume), "guiding restarts and settles first, then the run resumes");
            }
        }

        [Test]
        public async Task A_guiding_restart_that_never_settles_keeps_the_run_paused_and_is_tried_again() {
            // #2169 review — PHD2 takes the start and guides, but the star never comes within the settle distance.
            using var rig = new Rig { Settles = _ => false, GuidesUnsettled = true };
            rig.Service.GuidingRestoreSettleTimeout = TimeSpan.FromMilliseconds(300);
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => rig.Restarts() >= 3).ConfigureAwait(false), Is.True, "the unsettled start was not tried again");
            Assert.That(rig.Resumed(), Is.Zero, "never resumed on unsettled guiding");
            Assert.That(rig.Titled(StillTryingTitle), Is.EqualTo(1), "told once, not on every try");
        }

        [Test]
        public async Task A_run_the_user_took_over_during_the_reconnect_gets_no_automatic_guiding_start() {
            // #2169 review — the user resumed or stopped the run while the guider was away: guiding is theirs to start.
            using var rig = new Rig();
            rig.Sequencer.Setup(s => s.DaemonPausedRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Guid>());
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);

            await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false);
            await rig.Reaction.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Assert.That((rig.Restarts(), rig.Titled(StillTryingTitle)), Is.EqualTo((0, 0)), "no guiding start under a run the user owns, and no alert");
        }

        [Test]
        public async Task Guiding_is_not_restarted_while_the_safety_engine_holds_the_rig_and_starts_once_it_lets_go() {
            // #2169 review — a parked or held rig (clouds, a limit) must not start guiding under the safety engine;
            // the hold is waited out, not given up on.
            using var rig = new Rig();
            var held = true;
            rig.Service.SafetyHoldsRig = () => Volatile.Read(ref held);
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => rig.Titled(StillTryingTitle) == 1).ConfigureAwait(false), Is.True, "the run stays paused, and the user is told");
            Assert.That((rig.Restarts(), rig.Resumed()), Is.EqualTo((0, 0)), "no guiding start and no resume while the safety engine holds the rig");
            Volatile.Write(ref held, false);

            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the run never resumed once the hold ended");
            lock (rig.Steps) {
                Assert.That(rig.Steps, Is.EqualTo(RestartThenResume), "guiding restarts after the hold, then the run resumes");
            }
        }

        [Test]
        public async Task A_failed_guiding_restart_says_nothing_when_the_user_took_the_run_over_meanwhile() {
            // #2169 review — the user resumed or stopped the run while the start was failing: no stale alert.
            using var rig = new Rig { RestartSucceeds = _ => false, Settles = _ => false };
            var reads = 0;
            // Read 1: before the try; read 2: the restore's own check. From read 3 the user owns the run.
            rig.Sequencer.Setup(s => s.DaemonPausedRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                    Interlocked.Increment(ref reads) <= 2 ? ids.ToList() : (IReadOnlyList<Guid>)Array.Empty<Guid>());
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => rig.Restarts() == 1).ConfigureAwait(false), Is.True, "the restart never ran");
            await rig.Reaction.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Assert.That((rig.Titled(StillTryingTitle), rig.Resumed(), rig.Restarts()), Is.EqualTo((0, 0, 1)), "the user owns the run: no alert, no resume, no second start");
        }

        [Test]
        public async Task The_guiding_restart_waits_for_the_reconnects_guide_camera_check() {
            // #2169 review — the reconnect re-checks the guide camera (#1362); guiding starts after it, never
            // while the camera may still be re-picked.
            using var rig = new Rig();
            rig.Profile.ActiveProfile.GuiderSettings.GuiderCamera = "Alpaca Camera [192.0.2.10:6800/1]";
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var armed = false; // the reconnect's checks are held; the first connect's are not
            var checks = 0;
            rig.Alpaca.Setup(a => a.GetConfiguredDevicesAsync("192.0.2.10", 6800, It.IsAny<CancellationToken>()))
                .Returns(async (string _, int _, CancellationToken _) => {
                    if (Volatile.Read(ref armed)) {
                        Interlocked.Increment(ref checks);
                        await hold.Task.ConfigureAwait(false);
                        lock (rig.Steps) {
                            if (!rig.Steps.Contains("camera checked")) {
                                rig.Steps.Add("camera checked");
                            }
                        }
                    }
                    return (IReadOnlyDictionary<string, AlpacaConfiguredDevice>)new Dictionary<string, AlpacaConfiguredDevice>();
                });
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);
            Volatile.Write(ref armed, true);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => Volatile.Read(ref checks) > 0).ConfigureAwait(false), Is.True, "the reconnect never re-checked the camera");
            // A restart that does not wait would land in this window; one that waits stays put until the check ends.
            await WaitUntilAsync(() => rig.Restarts() > 0, timeoutMs: 2_000).ConfigureAwait(false);
            hold.SetResult();

            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the run never resumed");
            lock (rig.Steps) {
                Assert.That(rig.Steps, Is.EqualTo(CheckThenRestartThenResume), "the camera check ends before guiding starts");
            }
        }

        // The guide camera drops while PHD2 guides: guiding stops with it.
        private static async Task DropGuideCameraWhileGuidingAsync(Rig rig, FakeGuider fake) {
            await fake.BroadcastAsync(PhdEvents.EquipmentDisconnected()).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True, "the run was not paused");
            fake.OnRpc("get_app_state", JsonValue.Create("Stopped"));
            await fake.BroadcastAsync(PhdEvents.AppState("Stopped")).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.GuidingStopped()).ConfigureAwait(false);
        }

        private static JsonObject SingleFrameComplete(bool success) => new() {
            ["Event"] = "SingleFrameComplete", ["Timestamp"] = 0.0, ["Host"] = "fake-guider", ["Inst"] = 1,
            ["Success"] = success, ["Error"] = success ? null : "camera not connected",
        };

        // Every guide frame Ara takes fails while [cameraOut] says the camera is still unplugged; counts them.
        private static Func<int> CheckFrames(FakeGuider fake, Func<bool> cameraOut) {
            var captures = 0;
            fake.OnRpc("capture_single_frame", request => {
                Interlocked.Increment(ref captures);
                var ok = !cameraOut();
                _ = Task.Run(() => fake.BroadcastAsync(SingleFrameComplete(success: ok)));
                return JsonValue.Create(0);
            });
            return () => Volatile.Read(ref captures);
        }

        [Test]
        public async Task A_camera_back_event_with_no_guide_frames_keeps_the_camera_fault_open_and_the_run_paused() {
            // #2169 rig check — the guider said "camera back" while the camera was still unplugged. The event is
            // only a hint: Ara takes guide frames, and with none coming back the fault stays open, the run stays
            // paused and guiding is not started on a camera that is not there.
            using var rig = new Rig();
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);
            var captures = CheckFrames(fake, cameraOut: () => true);
            await DropGuideCameraWhileGuidingAsync(rig, fake).ConfigureAwait(false);

            await fake.BroadcastAsync(PhdEvents.EquipmentReconnected()).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => captures() >= 2 && rig.Titled(StillTryingTitle) == 1).ConfigureAwait(false), Is.True,
                "Ara never kept checking the camera, or never told the user");
            Assert.That(rig.WasRecovered("equipment_disconnected"), Is.False, "no guide frame came: the camera drop is not over");
            Assert.That((rig.Resumed(), rig.Restarts()), Is.EqualTo((0, 0)), "the run stays paused, and guiding is not started without a camera");
            Assert.That(rig.Titled(StillTryingTitle), Is.EqualTo(1), "told once");
        }

        [Test]
        public async Task Guide_frames_after_a_camera_drop_recover_the_fault_and_the_run_resumes_guided_without_waiting_out_a_retry() {
            // #2169 rig check — the guider never says the camera is back (guider#66) but guides again on its own:
            // its guide steps prove the camera works. The fault is recovered and the run resumes at once, not
            // after the retry wait.
            using var rig = new Rig();
            rig.Reaction.GuiderRetryBackoff = [TimeSpan.FromMinutes(10)];
            rig.Service.GuideCameraRetryBackoff = [TimeSpan.FromMinutes(10)];
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);
            await DropGuideCameraWhileGuidingAsync(rig, fake).ConfigureAwait(false);

            await rig.OnRestart!(0.02).ConfigureAwait(false); // the guider guides again by itself

            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the run never resumed");
            Assert.That(rig.WasRecovered("equipment_disconnected"), Is.True, "the guide frames end the camera drop");
            lock (rig.Steps) {
                Assert.That(rig.Steps, Is.EqualTo(RestartThenResume), "guiding is made sure of (PHD2 skips a start while guiding), then the run resumes");
            }
        }

        [Test]
        public async Task An_unguided_camera_drop_lets_the_frame_finish_and_resolves_only_once_a_guide_frame_comes_back() {
            // #2169 rig check — unguided, the frame in progress finishes; the "camera back" event alone does not
            // end the drop. Ara takes guide frames: they fail while the camera is gone (the user is told once),
            // and the run resumes once one comes back.
            using var rig = new Rig();
            await using var fake = await ConnectedFakeAsync(rig, guiding: false).ConfigureAwait(false);
            var captures = CheckFrames(fake, cameraOut: () => rig.Titled(CameraStillTryingTitle) == 0);

            await fake.BroadcastAsync(PhdEvents.EquipmentDisconnected()).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True, "the run was not paused");
            await fake.BroadcastAsync(PhdEvents.EquipmentReconnected()).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the run never resumed");
            Assert.That(captures(), Is.GreaterThanOrEqualTo(2), "a failed check frame, then one that came back");
            Assert.That(rig.WasRecovered("equipment_disconnected"), Is.True);
            Assert.That(rig.FramePauses(), Is.Zero, "an unguided run's frame is not stopped");
            Assert.That((rig.Restarts(), rig.Titled(CameraStillTryingTitle)), Is.EqualTo((0, 1)), "no guiding start; told once");
        }

        [Test]
        public async Task A_camera_drop_with_no_sequence_running_still_closes_once_a_guide_frame_comes_back() {
            // #2169 review — no run to pause, so no episode waits on the camera: its fault row still closes on
            // a real frame, and a later drop is a new fault again.
            using var rig = new Rig();
            rig.Sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Guid>());
            rig.Sequencer.Setup(s => s.PauseActiveRunsAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Guid>());
            rig.Sequencer.Setup(s => s.PauseActiveRunsAsync(false, true, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Guid>());
            await using var fake = await ConnectedFakeAsync(rig, guiding: false).ConfigureAwait(false);

            await fake.BroadcastAsync(PhdEvents.EquipmentDisconnected()).ConfigureAwait(false);
            await rig.Reaction.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.EquipmentReconnected()).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => rig.WasRecovered("equipment_disconnected")).ConfigureAwait(false), Is.True,
                "the camera came back but its fault row never closed");
            await fake.BroadcastAsync(PhdEvents.EquipmentDisconnected()).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => { lock (rig.Faults) { return rig.Faults.Count(f => f.Details == "equipment_disconnected") == 2; } })
                .ConfigureAwait(false), Is.True, "a later drop publishes a new fault");
        }

        [Test]
        public async Task A_camera_drop_the_user_took_over_still_closes_once_a_guide_frame_comes_back() {
            // #2169 review — the user resumed the run while the camera was out: Ara stops waiting for it, but the
            // fault row still closes once a real frame comes.
            using var rig = new Rig();
            var userOwns = false;
            rig.Sequencer.Setup(s => s.DaemonPausedRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) =>
                    Volatile.Read(ref userOwns) ? Array.Empty<Guid>() : (IReadOnlyList<Guid>)ids.ToList());
            await using var fake = await ConnectedFakeAsync(rig, guiding: false).ConfigureAwait(false);
            var cameraOut = true;
            var captures = CheckFrames(fake, cameraOut: () => Volatile.Read(ref cameraOut));

            await fake.BroadcastAsync(PhdEvents.EquipmentDisconnected()).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True, "the run was not paused");
            await fake.BroadcastAsync(PhdEvents.EquipmentReconnected()).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => captures() >= 1).ConfigureAwait(false), Is.True, "the camera was never checked");
            Volatile.Write(ref userOwns, true);
            await rig.Reaction.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Volatile.Write(ref cameraOut, false);

            Assert.That(await WaitUntilAsync(() => rig.WasRecovered("equipment_disconnected")).ConfigureAwait(false), Is.True,
                "the camera came back but its fault row never closed");
            Assert.That(rig.Resumed(), Is.Zero, "the run is the user's");
        }

        [Test]
        public async Task An_unguided_camera_drop_then_a_link_loss_resumes_only_once_a_guide_frame_comes_back() {
            // #2169 review — the link drops while the camera is out. The reconnect brings the link back, not the
            // camera: the run stays paused until a guide frame comes back, and the notice is the camera's.
            using var rig = new Rig();
            await using var fake = await ConnectedFakeAsync(rig, guiding: false).ConfigureAwait(false);
            var cameraOut = true;
            var captures = CheckFrames(fake, cameraOut: () => Volatile.Read(ref cameraOut));

            await fake.BroadcastAsync(PhdEvents.EquipmentDisconnected()).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True, "the run was not paused");
            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.WasRecovered("link_down")).ConfigureAwait(false), Is.True, "the link drop never recovered");
            var atReconnect = captures();

            Assert.That(await WaitUntilAsync(() => captures() >= atReconnect + 2).ConfigureAwait(false), Is.True,
                "the camera was not checked again after the reconnect");
            Assert.That((rig.Resumed(), rig.WasRecovered("equipment_disconnected")), Is.EqualTo((0, false)),
                "the link is back but the camera is not: the run stays paused");
            Volatile.Write(ref cameraOut, false);

            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the run never resumed");
            Assert.That(rig.WasRecovered("equipment_disconnected"), Is.True);
            Assert.That((rig.Restarts(), rig.Titled("Guide camera back — sequence resumed")), Is.EqualTo((0, 1)));
        }
    }
}
