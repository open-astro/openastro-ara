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

        private sealed class Rig : IDisposable {
            public readonly Mock<ISequencerService> Sequencer = new();
            public readonly List<NotificationDto> Notifications = new();
            public readonly List<string> Steps = new(); // "restart" and "resume", in the order they happened
            public readonly Guid RunId = Guid.NewGuid();
            public readonly HeadlessProfileService Profile = new();
            public readonly FaultReactionService Reaction;
            public bool RestartSucceeds = true;
            public Func<Task>? OnRestart; // the fake's side of a restart: app state Guiding, a settled guide step
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
                    lock (Steps) { Steps.Add("restart"); }
                    if (RestartSucceeds && OnRestart is { } hook) {
                        await hook().ConfigureAwait(false);
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
            rig.OnRestart = async () => {
                var guider = rig.Service.RequireConnectedGuider();
                fake.OnRpc("get_app_state", JsonValue.Create("Guiding"));
                await fake.BroadcastAsync(PhdEvents.AppState("Guiding")).ConfigureAwait(false);
                await fake.BroadcastAsync(PhdEvents.StartGuiding()).ConfigureAwait(false);
                await fake.BroadcastAsync(PhdEvents.GuideStep(0.02, 0.02)).ConfigureAwait(false);
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
    }
}
