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
        private const string NotRestartedTitle = "Guiding did not restart — sequence stays paused";
        private static readonly string[] RestartThenResume = ["restart", "resume"];
        private static readonly string[] RestartRestartResume = ["restart", "restart", "resume"];

        private sealed class Rig : IDisposable {
            public readonly Mock<ISequencerService> Sequencer = new();
            public readonly List<NotificationDto> Notifications = new();
            public readonly List<string> Steps = new(); // "restart" and "resume", in the order they happened
            public readonly List<EquipmentFaultEvent> Faults = new(); // every fault the guider published
            public readonly Guid RunId = Guid.NewGuid();
            public readonly HeadlessProfileService Profile = new();
            public readonly FaultReactionService Reaction;
            public bool RestartSucceeds = true;
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
                Reaction = new FaultReactionService(hub, reconnector: null, profiles.Object, () => Sequencer.Object,
                    telescope: null, notifications.Object, ws.Object, guiderEpisodes: () => Service);
                Reaction.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                Service = new GuiderService(
                    Profile,
                    new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(),
                        Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
                    NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(), ws.Object,
                    profiles.Object, () => Sequencer.Object, notifications.Object, hub) {
                    GuidingRestoreSettleTimeout = TimeSpan.FromSeconds(5),
                    GuidingRestoreReconcileWait = TimeSpan.FromSeconds(5),
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
                    return RestartSucceeds;
                };
            }

            public int Restarts() { lock (Steps) { return Steps.Count(s => s == "restart"); } }
            public int Resumed() => Sequencer.Invocations.Count(i => i.Method.Name == nameof(ISequencerService.ResumeRunsAsync));
            public int Paused() => Sequencer.Invocations.Count(i => i.Method.Name == nameof(ISequencerService.PauseActiveRunsAsync));
            public int FramePauses() => Sequencer.Invocations.Count(i => i.Method.Name == nameof(ISequencerService.PauseActiveRunsAsync)
                && i.Arguments.Count == 3 && (bool)i.Arguments[1]!);
            public int Titled(string title) { lock (Notifications) { return Notifications.Count(n => n.Title == title); } }

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
            Assert.That(rig.Titled(NotRestartedTitle), Is.Zero);
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
        public async Task A_guiding_restart_that_fails_leaves_the_run_paused_with_one_alert_and_no_retry() {
            using var rig = new Rig { RestartSucceeds = false };
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => rig.Titled(NotRestartedTitle) == 1).ConfigureAwait(false), Is.True, "the user is never told why the run stays paused");
            var idle = rig.Reaction.WhenIdleAsync();
            Assert.That(await Task.WhenAny(idle, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false), Is.SameAs(idle), "the episode ends: no retry loop");
            await Task.Delay(500).ConfigureAwait(false);
            Assert.That(rig.Resumed(), Is.Zero, "never resumed unguided");
            Assert.That((rig.Restarts(), rig.Titled(NotRestartedTitle)), Is.EqualTo((1, 1)), "one attempt, one alert");
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
            Assert.That(rig.Titled(NotRestartedTitle), Is.Zero, "the first restart was cut short by the loss, not given up on");
        }

        [Test]
        public async Task A_quick_unguided_re_drop_leaves_the_first_guided_link_loss_marked_as_guided() {
            // #2169 review — each loss keeps what it saw: a later drop with guiding off does not unmark the first.
            using var rig = new Rig { RestartSucceeds = false, Settles = _ => false };
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Titled(NotRestartedTitle) == 1).ConfigureAwait(false), Is.True);
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
        public async Task A_guiding_restart_that_never_settles_leaves_the_run_paused_with_one_alert() {
            // #2169 review — PHD2 takes the start and guides, but the star never comes within the settle distance.
            using var rig = new Rig { Settles = _ => false, GuidesUnsettled = true };
            rig.Service.GuidingRestoreSettleTimeout = TimeSpan.FromSeconds(1);
            await using var fake = await ConnectedFakeAsync(rig, guiding: true).ConfigureAwait(false);

            await DropAndReconnectStoppedAsync(rig, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => rig.Titled(NotRestartedTitle) == 1).ConfigureAwait(false), Is.True, "the user is never told why the run stays paused");
            await rig.Reaction.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Assert.That((rig.Restarts(), rig.Resumed()), Is.EqualTo((1, 0)), "started once, never resumed on unsettled guiding");
        }
    }
}
