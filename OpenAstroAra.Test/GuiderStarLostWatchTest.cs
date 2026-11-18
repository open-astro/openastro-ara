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
    /// grace period publishes a star-lost fault and the shared reaction runs on_guider_lost, and a settled stretch of guiding
    /// afterwards resumes exactly those runs. A star that comes back inside the grace does nothing.
    /// </summary>
    [TestFixture]
    [Category("IO")] // loopback socket against the fake guider
    public class GuiderStarLostWatchTest {

        private static readonly string[] StarLostTitle = ["Guide star lost"];
        private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);
        // For the tests whose guide step must land INSIDE a window (the grace, or the wait before the
        // recovery): with 300 ms a loaded CI runner sometimes delivered the step after the timer fired
        // (2026-11-04, PR #1491). Two seconds leaves the loopback hop all the room it needs.
        private static readonly TimeSpan RoomyWindow = TimeSpan.FromSeconds(2);

        private sealed class Rig : IDisposable {
            public readonly Mock<ISequencerService> Sequencer = new();
            public readonly List<NotificationDto> Notifications = new();
            public readonly List<string> WsTopics = new();
            public readonly Guid RunId = Guid.NewGuid();
            public readonly Mock<IProfileStore> Profiles = new();
            public readonly HeadlessProfileService Profile = new();
            public readonly Mock<ICenteringService> Centering = new();
            public readonly List<EquipmentFaultEvent> Faults = new(); // what reached the hub's sink
            public readonly List<EquipmentFaultEvent> Recovered = new(); // what the guider reported Recovered
            public readonly FaultReactionService Reaction;
            public int Restarts;
            public bool RestartSucceeds = true;
            public bool RestartStartsButFails; // PHD2 guides but the settle timed out: StartGuiding returns false
            public Func<Task>? OnRestart; // the fake's side of a restart: app state Guiding, StartGuiding, a guide step
            public GuiderService Service = null!;

            public Rig(bool runActive, TimeSpan? grace = null, TimeSpan? recoverAfter = null) {
                // The profile default (10 s) would make every resume here wait 10 s; the settle tests set their own.
                Profile.ActiveProfile.GuiderSettings.SettleTime = 0;
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
                // Active runs: the guider's custody check does not read them (it asks about the runs the
                // reaction paused for its episode); a test sets them to model runs another source holds.
                Sequencer.Setup(s => s.ActiveRunIdsAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(runActive ? new List<Guid> { RunId } : new List<Guid>());
                // Until the user acts, the star-lost pause is still the daemon's.
                Sequencer.Setup(s => s.DaemonPausedRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => (IReadOnlyList<Guid>)ids.ToList());
                Sequencer.Setup(s => s.GetActiveTargetCoordinatesAsync(RunId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Target);
                Centering.Setup(c => c.CenterOnTarget(It.IsAny<Coordinates>(), It.IsAny<IProgress<PlateSolveProgress>?>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PlateSolveResult());
                var notifications = new Mock<INotificationService>();
                notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                    .Callback<NotificationDto, CancellationToken>((n, _) => { lock (Notifications) { Notifications.Add(n); } })
                    .Returns(Task.CompletedTask);
                var ws = new Mock<IWsBroadcaster>();
                ws.Setup(w => w.PublishAsync(It.IsAny<OpenAstroAra.Server.Contracts.WsEvents.WsEvent>(), It.IsAny<System.Text.Json.JsonElement>(), It.IsAny<CancellationToken>()))
                    .Callback<OpenAstroAra.Server.Contracts.WsEvents.WsEvent, System.Text.Json.JsonElement, CancellationToken>((t, _, _) => { lock (WsTopics) { WsTopics.Add(t); } })
                    .Returns(Task.CompletedTask);
                // The shared reaction behind the hub: the guider only publishes faults and reports Recovered.
                var hub = new EquipmentFaultHub(ws.Object);
                hub.Subscribe(f => { lock (Faults) { Faults.Add(f); } });
                hub.SubscribeRecovered(f => { lock (Recovered) { Recovered.Add(f); } });
                // The reaction tells the guider which runs it paused for its fault (as Program.cs wires it).
                Reaction = new FaultReactionService(hub, reconnector: null, profiles.Object, () => Sequencer.Object,
                    telescope: null, notifications.Object, ws.Object, guiderEpisodes: () => Service);
                Reaction.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                Service = new GuiderService(
                    Profile,
                    new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(),
                        Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
                    NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(), ws.Object,
                    profiles.Object, () => Sequencer.Object, notifications.Object, hub) {
                    StarLostGrace = grace ?? Grace,
                    StarLostRecoverAfter = recoverAfter ?? Grace,
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
            public void Dispose() {
                Service.Dispose();
                Reaction.Dispose();
            }
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
        public async Task A_star_lost_past_the_grace_pauses_the_run_and_a_settled_stretch_of_guiding_resumes_it() {
            using var rig = new Rig(runActive: true);
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);

            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True, "the run was not paused after the grace");
            rig.Sequencer.Verify(s => s.PauseActiveRunsAsync(true, It.IsAny<CancellationToken>()), Times.Once, "a star-lost pause is adoptable by a later daemon pause");
            Assert.That(await WaitUntilAsync(() => rig.Notifications.Count == 1).ConfigureAwait(false), Is.True);
            Assert.That(rig.Notifications[0].Title, Is.EqualTo("Guide star lost — sequence paused"));
            // The fault reaches the sink once, with the star_lost details, and the retired guider event is gone.
            lock (rig.Faults) {
                Assert.That(rig.Faults, Has.Count.EqualTo(1));
                Assert.That(rig.Faults[0].DeviceType, Is.EqualTo(DeviceType.Guider));
                Assert.That(rig.Faults[0].Kind, Is.EqualTo(EquipmentFaultKind.TrackingLost));
                Assert.That(rig.Faults[0].Details, Is.EqualTo("star_lost"));
            }
            Assert.That(rig.WsTopics, Does.Contain(OpenAstroAra.Server.Contracts.WsEvents.WsEventCatalog.EquipmentFault.Token));
            Assert.That(rig.WsTopics, Does.Not.Contain("guider.fault_action_taken"));

            // Resumes only after a settled stretch: the error under the settle distance for the settle time.
            rig.Profile.ActiveProfile.GuiderSettings.SettleTime = 1;
            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.1)).ConfigureAwait(false);
            await Task.Delay(Grace).ConfigureAwait(false);
            Assert.That(rig.Resumed(), Is.Zero, "one settled step is not a settled stretch");
            await Task.Delay(TimeSpan.FromMilliseconds(1300)).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.1)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "a settled stretch must resume the paused run");
            rig.Sequencer.Verify(s => s.ResumeRunsAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == rig.RunId), It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(await WaitUntilAsync(() => rig.Notifications.Count == 2).ConfigureAwait(false), Is.True);
            Assert.That(rig.Notifications[1].Title, Does.StartWith("Guide star back"));

            // A second episode in the same session reacts again: clouds pass more than once a night.
            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 2).ConfigureAwait(false), Is.True, "the watch is not one-shot per connect");
        }

        [Test]
        public async Task A_relock_far_from_the_lock_position_does_not_resume_the_run() {
            using var rig = new Rig(runActive: true, recoverAfter: TimeSpan.FromMinutes(10));
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.GuideStep(40.0, 40.0)).ConfigureAwait(false);
            await Task.Delay(Grace * 3).ConfigureAwait(false);
            Assert.That(rig.Resumed(), Is.Zero, "a guide step 40 px from lock is not settled guiding");
        }

        // The armed star-lost grace timer, read by reflection: a restarted grace replaces the instance.
        private static object? GraceTimer(GuiderService service) =>
            typeof(GuiderService).GetField("_starLostTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service);

        [Test]
        public async Task A_star_found_far_from_lock_between_losses_does_not_restart_the_grace() {
            // The grace is 10 min and never elapses by itself: the test fires it by hand, so no wall-clock margin.
            using var rig = new Rig(runActive: true, grace: TimeSpan.FromMinutes(10), recoverAfter: TimeSpan.FromMinutes(10));
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            var guider = rig.Service.RequireConnectedGuider();
            await fake.BroadcastAsync(PhdEvents.StarLost(status: "mass changed")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => GraceTimer(rig.Service) is not null).ConfigureAwait(false), Is.True, "the first loss never armed the grace");
            var armed = GraceTimer(rig.Service);

            await fake.BroadcastAsync(PhdEvents.GuideStep(66.0, 94.0)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => guider.AppState?.State == "Guiding").ConfigureAwait(false), Is.True, "the far step never landed");
            await fake.BroadcastAsync(PhdEvents.StarLost(status: "mass changed")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => guider.AppState?.State == "LostLock").ConfigureAwait(false), Is.True, "the second loss never landed");

            Assert.That(GraceTimer(rig.Service), Is.SameAs(armed), "the grace counts from the first loss: a second loss must not restart it");
            rig.Service.OnStarLostGraceElapsed(null);
            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True,
                "the star has been lost for the whole grace, counted from the first loss");
        }

        [Test]
        public async Task A_star_found_far_from_lock_and_kept_there_pauses_the_run_after_the_grace() {
            // No second loss: PHD2 reads Guiding again when the grace runs out, and the star is still 56 px off.
            using var rig = new Rig(runActive: true, grace: RoomyWindow, recoverAfter: TimeSpan.FromMinutes(10));
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            var guider = rig.Service.RequireConnectedGuider();
            await fake.BroadcastAsync(PhdEvents.StarLost(status: "mass changed")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => guider.AppState?.State == "LostLock").ConfigureAwait(false), Is.True, "the loss never landed");
            await fake.BroadcastAsync(PhdEvents.GuideStep(40.0, 40.0)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => guider.AppState?.State == "Guiding").ConfigureAwait(false), Is.True, "the far step never landed");

            Assert.That(await WaitUntilAsync(() => rig.Paused() == 1).ConfigureAwait(false), Is.True,
                "guiding 56 px from lock is not settled: the grace still runs out and pauses the run");
        }

        [Test]
        public async Task A_settled_stretch_restarts_when_the_error_leaves_the_settle_distance() {
            using var rig = new Rig(runActive: true, recoverAfter: TimeSpan.FromMinutes(10));
            rig.Profile.ActiveProfile.GuiderSettings.SettlePixels = 1.5;
            rig.Profile.ActiveProfile.GuiderSettings.SettleTime = 1;
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);

            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, 0.1)).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.GuideStep(3.0, 0.0)).ConfigureAwait(false); // out of the 1.5 px: the stretch starts over
            await Task.Delay(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, 0.1)).ConfigureAwait(false);
            await Task.Delay(Grace).ConfigureAwait(false); // the step must land before the check
            Assert.That(rig.Resumed(), Is.Zero, "1.6 s since the first settled step, but the stretch was broken");

            await Task.Delay(TimeSpan.FromMilliseconds(1300)).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, 0.1)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "a whole second settled resumes the run");
        }

        [Test]
        public async Task The_recovery_resumes_the_run_only_once_the_restarted_guiding_has_settled() {
            using var rig = new Rig(runActive: true);
            rig.Profile.ActiveProfile.GuiderSettings.SettlePixels = 1.5;
            // 2 s, checked against 1 s: a loaded runner may see the restart late, which eats into the margin.
            rig.Profile.ActiveProfile.GuiderSettings.SettleTime = 2;
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            Assert.That(await WaitUntilAsync(() => rig.Restarts == 1).ConfigureAwait(false), Is.True, "the recovery restarted guiding");
            var restartedAt = DateTime.UtcNow;
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True, "the settled guiding resumed the run");
            Assert.That(DateTime.UtcNow - restartedAt, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1)),
                "the recovery's resume waited for the settle time, not just the restart");
        }

        [Test]
        public async Task A_grace_callback_that_runs_after_a_settled_step_does_not_pause_the_run() {
            // Disposing the timer does not stop a callback already queued or waiting on the lock; it runs
            // after the settled step and must stand down.
            using var rig = new Rig(runActive: true, grace: RoomyWindow);
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);
            var rmsBefore = (await rig.Service.GetAsync(CancellationToken.None).ConfigureAwait(false))?.Runtime.RmsTotal;

            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.1)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Service.GetAsync(CancellationToken.None).Result?.Runtime.RmsTotal != rmsBefore).ConfigureAwait(false),
                Is.True, "the settled step never landed");
            rig.Service.OnStarLostGraceElapsed(null);
            await Task.Delay(Grace).ConfigureAwait(false);

            Assert.That(rig.Paused(), Is.Zero, "the star is back and settled: a late grace callback pauses nothing");
            Assert.That(rig.Notifications, Is.Empty);
        }

        [Test]
        public async Task A_star_that_comes_back_inside_the_grace_does_nothing() {
            using var rig = new Rig(runActive: true, grace: RoomyWindow);
            await using var fake = await GuidingFakeAsync(rig).ConfigureAwait(false);

            await fake.BroadcastAsync(PhdEvents.StarLost(status: "low SNR")).ConfigureAwait(false);
            await Task.Delay(Grace / 3).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.1)).ConfigureAwait(false);
            // Past the grace, so a timer the guide step failed to disarm would have fired.
            await Task.Delay(RoomyWindow + Grace).ConfigureAwait(false);

            Assert.That(rig.Paused(), Is.Zero, "a brief loss is PHD2's business");
            Assert.That(rig.Faults, Is.Empty, "a guide step inside the grace publishes no fault");
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
            Assert.That(rig.Notifications.Select(n => n.Title), Is.EqualTo(StarLostTitle),
                "no run was paused, so the only notice is the shared reaction's notify_only one — no resume notice");
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
            Assert.That(rig.Notifications[1].Title, Does.StartWith("Guide star back").And.Contains("sequence resumed"));
            Assert.That(rig.Notifications[1].Severity, Is.EqualTo(NotificationSeverity.Info));

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
        public async Task With_no_run_active_the_probe_does_not_report_the_star_recovered_while_it_is_still_lost() {
            // The episode paused nothing, so there is nothing for the long-outage recovery to watch: the
            // fault stays open while the guider is in LostLock (no "no paused run" read as the user acting).
            using var rig = new Rig(runActive: false);
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            var guider = rig.Service.RequireConnectedGuider();

            await Task.Delay(RoomyWindow).ConfigureAwait(false); // well past StarLostRecoverAfter and two probe intervals
            Assert.That(guider.AppState?.State, Is.EqualTo("LostLock"), "the star is still lost");
            lock (rig.Recovered) { Assert.That(rig.Recovered, Is.Empty, "a star that is still lost is not recovered"); }
            Assert.That(fake.ReceivedMethods, Does.Not.Contain("get_star_centroids"), "nothing paused: no probe");
        }

        [Test]
        public async Task A_run_paused_by_another_source_is_not_re_centred_by_the_star_lost_probe() {
            // Another episode (a mount drop, say) already holds the run paused, so the star-lost episode pauses
            // none: its probe must not stop the guider and slew the mount for a run it never paused.
            using var rig = new Rig(runActive: true);
            rig.Sequencer.Setup(s => s.PauseActiveRunsAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Guid>());
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            await Task.Delay(RoomyWindow).ConfigureAwait(false);
            Assert.That(rig.Centered(), Is.Zero, "no re-centre for a run this episode did not pause");
            Assert.That(fake.ReceivedMethods, Does.Not.Contain("stop_capture"), "the guider is not stopped for another episode's run");
            lock (rig.Recovered) { Assert.That(rig.Recovered, Is.Empty, "the star is still lost: its fault stays open"); }
        }

        [Test]
        public async Task Once_the_user_resumes_the_episodes_run_a_run_another_source_paused_is_not_re_centred() {
            // The probe asks only about the runs its own episode paused. Here the user has resumed that run
            // while another source still holds a second one paused: the episode is handed over, no slew.
            using var rig = new Rig(runActive: true);
            var own = Guid.NewGuid();
            rig.Sequencer.Setup(s => s.PauseActiveRunsAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Guid> { own });
            rig.Sequencer.Setup(s => s.ActiveRunIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Guid> { own, rig.RunId });
            rig.Sequencer.Setup(s => s.DaemonPausedRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => (IReadOnlyList<Guid>)ids.Where(id => id == rig.RunId).ToList());
            rig.Sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            Assert.That(await WaitUntilAsync(() => { lock (rig.Recovered) { return rig.Recovered.Count == 1; } }).ConfigureAwait(false), Is.True,
                "the episode's own run was resumed by the user: the episode closes");
            await Task.Delay(Grace * 3).ConfigureAwait(false);
            Assert.That(rig.Centered(), Is.Zero, "the run another source paused is not the episode's to re-centre");
            Assert.That(fake.ReceivedMethods, Does.Not.Contain("stop_capture"));
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
            using var rig = new Rig(runActive: true, recoverAfter: RoomyWindow);
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, -0.1)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => rig.Resumed() == 1).ConfigureAwait(false), Is.True);
            // Past the recovery wait, so a recovery the relock failed to disarm would have probed.
            await Task.Delay(RoomyWindow + Grace).ConfigureAwait(false);

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
            Assert.That(rig.Notifications[2].Title, Does.Contain("sequence resumed"));
            Assert.That(rig.Notifications.Count(n => n.Title.Contains("re-center failed", StringComparison.Ordinal)), Is.EqualTo(1), "one warning per episode, not one per retry");
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
                .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => ++asked == 1 ? (IReadOnlyList<Guid>)ids.ToList() : Array.Empty<Guid>());
            // The episode closes through the shared reaction, whose resume releases nothing the user took.
            rig.Sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            Assert.That(await WaitUntilAsync(() => fake.ReceivedMethods.Contains("get_star_centroids")).ConfigureAwait(false), Is.True);
            await Task.Delay(Grace * 3).ConfigureAwait(false);

            Assert.That(fake.ReceivedMethods, Does.Not.Contain("stop_capture"), "the user's run is not the daemon's to stop guiding on");
            Assert.That(rig.Centered(), Is.Zero);
            Assert.That(rig.Notifications, Has.Count.EqualTo(1), "the user settled the pause: no resume notice");
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
            rig.Sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

            await Task.Delay(Grace * 4).ConfigureAwait(false);

            Assert.That(fake.ReceivedMethods, Does.Not.Contain("get_star_centroids"), "the episode closed before the first probe");
            Assert.That(rig.Notifications, Has.Count.EqualTo(1), "the user settled the pause: no resume notice");
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
            using var rig = new Rig(runActive: true, recoverAfter: RoomyWindow);
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());

            await fake.BroadcastAsync(PhdEvents.GuidingStopped()).ConfigureAwait(false);
            await Task.Delay(RoomyWindow + Grace).ConfigureAwait(false);

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

        // ── the episode across a link drop and the §63.3 auto-reconnect ────────────────────────────

        // The PHD2 link drops while the star-lost episode is open, and the auto-reconnect (the rig's
        // supervisor mock reads the unit active) connects again with PHD2 still hunting the star.
        private static async Task ReconnectWithTheStarStillLostAsync(Rig rig, FakeGuider fake) {
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("LostLock"));
            fake.OnRpc("get_app_state", JsonValue.Create("LostLock"));
            var dropped = rig.Service.RequireConnectedGuider();
            Assert.That(fake.DropConnections(), Is.GreaterThan(0), "expected a live connection to drop");
            object? Current() {
                try {
                    return rig.Service.RequireConnectedGuider();
                } catch (InvalidOperationException) {
                    return null; // not Connected (yet)
                }
            }
            Assert.That(await WaitUntilAsync(() => Current() is { } now && !ReferenceEquals(now, dropped)).ConfigureAwait(false),
                Is.True, "the auto-reconnect never connected a new guider");
            Assert.That(await WaitUntilAsync(() => rig.Service.RequireConnectedGuider().AppState?.State == "LostLock").ConfigureAwait(false),
                Is.True, "the reconnected guider never read LostLock");
        }

        [Test]
        public async Task After_a_reconnect_the_probe_sees_the_users_resume_and_reports_the_star_lost_fault_recovered() {
            // The episode outlives the reconnect's guider dispose, so its probe must come back with the
            // connection: it alone sees the user's Resume, and the shared reaction waits for that Recovered.
            using var rig = new Rig(runActive: true, recoverAfter: TimeSpan.FromMinutes(10)); // only the reconnect can arm a probe here
            rig.Service.StarLostProbeInterval = TimeSpan.FromSeconds(1);
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            await ReconnectWithTheStarStillLostAsync(rig, fake).ConfigureAwait(false);

            rig.Sequencer.Setup(s => s.DaemonPausedRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Guid>()); // the user pressed Resume
            rig.Sequencer.Setup(s => s.ResumeRunsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

            EquipmentFaultEvent starLost;
            lock (rig.Faults) { starLost = rig.Faults.Single(f => f.Details == "star_lost"); }
            Assert.That(await WaitUntilAsync(() => { lock (rig.Recovered) { return rig.Recovered.Exists(f => ReferenceEquals(f, starLost)); } },
                    timeoutMs: (int)(2 * rig.Service.StarLostProbeInterval.TotalMilliseconds)).ConfigureAwait(false),
                Is.True, "within two probe intervals of the Resume the star-lost fault is reported Recovered");
        }

        [Test]
        public async Task After_a_reconnect_with_the_star_still_lost_the_probe_asks_for_stars_again() {
            using var rig = new Rig(runActive: true);
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => fake.ReceivedMethods.Contains("get_star_centroids")).ConfigureAwait(false), Is.True,
                "the probe ran before the drop");

            await ReconnectWithTheStarStillLostAsync(rig, fake).ConfigureAwait(false);
            var probes = fake.ReceivedMethods.Count(m => m == "get_star_centroids");
            Assert.That(await WaitUntilAsync(() => fake.ReceivedMethods.Count(m => m == "get_star_centroids") > probes).ConfigureAwait(false), Is.True,
                "the reconnected guider is still in LostLock: the long-outage probe asks its frames again");
            Assert.That(rig.Centered(), Is.Zero, "a starless frame moves nothing");
        }

        [Test]
        public async Task The_reconnect_notice_says_the_runs_the_lost_star_paused_wait_for_the_star() {
            using var rig = new Rig(runActive: true, recoverAfter: TimeSpan.FromMinutes(10));
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            await ReconnectWithTheStarStillLostAsync(rig, fake).ConfigureAwait(false);

            NotificationDto? notice = null;
            Assert.That(await WaitUntilAsync(() => {
                lock (rig.Notifications) { notice = rig.Notifications.Find(n => n.Title == "Guider reconnected automatically"); }
                return notice is not null;
            }).ConfigureAwait(false), Is.True, "the reconnect notice was never posted");
            Assert.That(notice!.Message, Does.EndWith("Runs the lost guide star paused resume once the star is back, or when you Resume them."),
                "the link is back but the star is not: the runs it paused do not resume on the reconnect");
        }

        [Test]
        public async Task A_given_up_episode_stays_given_up_across_a_reconnect() {
            // The give-up belongs to the episode, which outlives the reconnect: the probe that comes back on
            // Connected must not try again, or every reconnect repeats the give-up for the same cloud.
            using var rig = new Rig(runActive: true);
            rig.Service.Recenter = null;
            await using var fake = await LostStarPastTheGraceAsync(rig).ConfigureAwait(false);
            fake.OnRpc("get_star_centroids", _ => Stars());
            int GiveUps() {
                lock (rig.Notifications) { return rig.Notifications.Count(n => n.Title.Contains("cannot re-center", StringComparison.Ordinal)); }
            }
            Assert.That(await WaitUntilAsync(() => GiveUps() == 1).ConfigureAwait(false), Is.True, "the episode never gave up");

            await ReconnectWithTheStarStillLostAsync(rig, fake).ConfigureAwait(false);
            await Task.Delay(RoomyWindow).ConfigureAwait(false); // many probe intervals
            Assert.That(fake.ReceivedMethods.Count(m => m == "get_star_centroids"), Is.EqualTo(1), "given up before the drop: no probe after the reconnect");
            Assert.That(GiveUps(), Is.EqualTo(1), "the give-up is said once per episode");
        }
    }
}