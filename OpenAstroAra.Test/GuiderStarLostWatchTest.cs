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
            public GuiderService Service = null!;

            public Rig(bool runActive) {
                var profiles = new Mock<IProfileStore>();
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
                    profiles.Object, () => Sequencer.Object, notifications.Object, Mock.Of<IFaultLogService>()) {
                    StarLostGrace = Grace,
                };
            }

            public int Paused() => Sequencer.Invocations.Count(i => i.Method.Name == nameof(ISequencerService.PauseActiveRunsAsync));
            public int Resumed() => Sequencer.Invocations.Count(i => i.Method.Name == nameof(ISequencerService.ResumeRunsAsync));
            public void Dispose() => Service.Dispose();
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000) {
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
    }
}
