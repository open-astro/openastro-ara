#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1901 — <see cref="EndNightService"/> over fake devices and a fake poweroff starter: the order of the
    /// device calls, when the host poweroff is asked for, cancel at a step boundary, no cancel once the
    /// poweroff started, the refusal of a second start, and a step that fails while the next safe step runs.
    /// </summary>
    [TestFixture]
    public class EndNightServiceTest {

        /// <summary>Records the poweroff request in the shared call log; <see cref="Gate"/> holds it open.</summary>
        private sealed class FakePoweroffStarter(List<string> calls) : IHostPoweroffStarter {
            public TaskCompletionSource<bool>? Gate { get; set; }
            public HostPoweroffResult Result { get; set; } = new(true, string.Empty);
            public Task<HostPoweroffResult> StartAsync(CancellationToken ct) {
                lock (calls) calls.Add("poweroff");
                return Gate is null ? Task.FromResult(Result) : Gate.Task.ContinueWith(_ => Result, TaskScheduler.Default);
            }
        }

        private static readonly string[] DocumentedOrder = [
            "abort-run", "stop-guiding", "find-home", "park",
            "disconnect-filter-wheel", "disconnect-focuser", "disconnect-rotator", "disconnect-flat-panel",
            "cooler-off", "disconnect-camera", "notify", "poweroff",
        ];

        private List<string> calls = null!;
        private Mock<IProfileStore> profiles = null!;
        private Mock<ISequencerService> sequencer = null!;
        private Mock<IGuiderMediator> guider = null!;
        private Mock<ITelescopeMediator> telescope = null!;
        private Mock<ICameraService> camera = null!;
        private Mock<IFilterWheelService> filterWheel = null!;
        private Mock<IFocuserService> focuser = null!;
        private Mock<IRotatorService> rotator = null!;
        private Mock<IFlatDeviceService> flatDevice = null!;
        private Mock<INotificationService> notifications = null!;
        private Mock<IWsBroadcaster> ws = null!;
        private FakePoweroffStarter poweroff = null!;
        private List<NotificationDto> posted = null!;
        private List<JsonElement> progress = null!;
        private List<EndNightService> created = null!;

        private static OperationAcceptedDto Accepted(string op) => new(Guid.NewGuid(), op, DateTimeOffset.UtcNow, null);

        private void Log(string call) { lock (calls) calls.Add(call); }

        private List<string> Calls() { lock (calls) return calls.ToList(); }

        private static TelescopeInfo Mount(bool canPark = true, bool atPark = false, bool canFindHome = true) => new() {
            Connected = true, CanPark = canPark, AtPark = atPark, CanFindHome = canFindHome, TrackingEnabled = true,
        };

        [SetUp]
        public void SetUp() {
            calls = new List<string>();
            created = new List<EndNightService>();
            profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetImagingDefaults()).Returns(new ImagingDefaultsDto(
                ExposureSeconds: 60, Gain: 100, Offset: 10, Bin: 1, FrameKind: "light",
                CoolerTargetC: -10, CoolerRampCPerMin: 5, WarmupAtSessionEnd: false));

            sequencer = new Mock<ISequencerService>();
            sequencer.Setup(s => s.AbortActiveRunsAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Log("abort-run")).ReturnsAsync(1);
            sequencer.Setup(s => s.UnfinishedRunIdsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<Guid>());

            guider = new Mock<IGuiderMediator>();
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = true });
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>()))
                .Callback(() => Log("stop-guiding")).ReturnsAsync(true);

            telescope = new Mock<ITelescopeMediator>();
            telescope.Setup(t => t.GetInfo()).Returns(Mount());
            telescope.Setup(t => t.FindHome(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback(() => Log("find-home")).ReturnsAsync(true);
            telescope.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback(() => Log("park")).ReturnsAsync(true);
            telescope.Setup(t => t.SetTrackingEnabled(false))
                .Callback(() => Log("tracking-off")).Returns(true);

            camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CameraDto("cam-1", "Test Cam", EquipmentConnectionState.Connected, null,
                    new CameraStateDto("idle", -10, 80, true, null)));
            camera.Setup(c => c.SetCoolerAsync(It.IsAny<bool>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .Callback<bool, double?, CancellationToken>((on, _, _) => { if (!on) Log("cooler-off"); })
                .Returns(Task.CompletedTask);
            camera.Setup(c => c.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback(() => Log("disconnect-camera")).ReturnsAsync(Accepted("camera.disconnect"));

            filterWheel = new Mock<IFilterWheelService>();
            filterWheel.Setup(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback(() => Log("disconnect-filter-wheel")).ReturnsAsync(Accepted("filterwheel.disconnect"));
            focuser = new Mock<IFocuserService>();
            focuser.Setup(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback(() => Log("disconnect-focuser")).ReturnsAsync(Accepted("focuser.disconnect"));
            rotator = new Mock<IRotatorService>();
            rotator.Setup(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback(() => Log("disconnect-rotator")).ReturnsAsync(Accepted("rotator.disconnect"));
            flatDevice = new Mock<IFlatDeviceService>();
            flatDevice.Setup(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback(() => Log("disconnect-flat-panel")).ReturnsAsync(Accepted("flatpanel.disconnect"));

            posted = new List<NotificationDto>();
            notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Callback<NotificationDto, CancellationToken>((n, _) => { lock (posted) posted.Add(n); Log("notify"); })
                .Returns(Task.CompletedTask);

            progress = new List<JsonElement>();
            ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<WsEvent>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<WsEvent, JsonElement, CancellationToken>((e, p, _) => {
                    Assert.That(e.Token, Is.EqualTo("end_night.progress"));
                    lock (progress) progress.Add(p.Clone());
                }).Returns(Task.CompletedTask);

            poweroff = new FakePoweroffStarter(calls);
        }

        [TearDown]
        public void TearDown() {
            foreach (var sut in created) sut.Dispose();
        }

        private EndNightService CreateSUT(double warmTimeScale = 6000, RigOccupancy? occupancy = null) {
            // #2143 — composed like Program.cs: End Night's ladder leaves End Night alone.
            var ladder = new ShutdownLadder(profiles.Object, guider.Object, telescope.Object, camera.Object,
                filterWheel.Object, focuser.Object, rotator.Object, flatDevice.Object, occupancy: occupancy, routine: RigOccupancy.EndNight) {
                WarmTimeScale = warmTimeScale, // 6000: 1 "minute" = 10 ms
                WarmHardCap = TimeSpan.FromSeconds(5),
                ParkTimeout = TimeSpan.FromSeconds(2),
                FindHomeTimeout = TimeSpan.FromSeconds(2),
            };
            var sut = new EndNightService(ladder, poweroff, () => sequencer.Object, notifications.Object, ws.Object) {
                AbortWait = TimeSpan.FromSeconds(2),
                AbortPoll = TimeSpan.FromMilliseconds(10),
            };
            created.Add(sut);
            return sut;
        }

        private static async Task FinishedAsync(EndNightService sut) {
            await sut.Worker!.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task WaitUntilAsync(Func<bool> condition, string reason) {
            for (var i = 0; i < 250; i++) { // up to ~5s
                if (condition()) return;
                await Task.Delay(20);
            }
            Assert.That(condition(), Is.True, reason);
        }

        private static string StepState(EndNightStatusDto status, string id) => status.Steps.Single(s => s.Id == id).State;

        [Test]
        public async Task The_steps_run_in_the_documented_order_and_the_host_poweroff_is_last() {
            var sut = CreateSUT();
            sut.Start(shutDownHost: true);
            await FinishedAsync(sut);

            Assert.That(Calls(), Is.EqualTo(DocumentedOrder));
            var status = sut.GetStatus();
            Assert.That(status.State, Is.EqualTo(EndNightStates.Complete));
            Assert.That(status.Steps.Select(s => s.State), Is.All.EqualTo(EndNightStepStates.Done));
            Assert.That(status.Steps.Select(s => s.Id), Is.EqualTo(EndNightStepIds.All));
        }

        [Test]
        public async Task The_run_is_aborted_and_over_before_guiding_stops() {
            var polls = 0;
            var running = new List<Guid> { Guid.NewGuid() };
            sequencer.Setup(s => s.UnfinishedRunIdsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => {
                    Log("poll-run");
                    return ++polls < 3 ? running : new List<Guid>();
                });
            var sut = CreateSUT();
            sut.Start(shutDownHost: false);
            await FinishedAsync(sut);

            var order = Calls();
            Assert.That(order.IndexOf("abort-run"), Is.EqualTo(0));
            Assert.That(order.Count(c => c == "poll-run"), Is.EqualTo(3), "waits until the run, and so its Session, is over");
            Assert.That(order.IndexOf("stop-guiding"), Is.GreaterThan(order.LastIndexOf("poll-run")));
        }

        [Test]
        public async Task The_run_that_does_not_finish_aborting_is_reported_as_a_failed_step() {
            sequencer.Setup(s => s.UnfinishedRunIdsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { Guid.NewGuid() });
            var sut = CreateSUT();
            sut.AbortWait = TimeSpan.FromMilliseconds(50);
            sut.Start(shutDownHost: false);
            await FinishedAsync(sut);

            Assert.That(StepState(sut.GetStatus(), EndNightStepIds.AbortRun), Is.EqualTo(EndNightStepStates.Failed));
            Assert.That(Calls(), Does.Contain("stop-guiding"), "End Night goes on with the next safe step");
            Assert.That(posted.Single().Severity, Is.EqualTo(NotificationSeverity.Warning));
        }

        [Test]
        public async Task A_mount_that_cannot_find_home_is_parked_without_the_find_home_call() {
            telescope.Setup(t => t.GetInfo()).Returns(Mount(canFindHome: false));
            var sut = CreateSUT();
            sut.Start(shutDownHost: true);
            await FinishedAsync(sut);

            Assert.That(Calls(), Does.Not.Contain("find-home"));
            Assert.That(Calls(), Does.Contain("park"));
            Assert.That(StepState(sut.GetStatus(), EndNightStepIds.FindHome), Is.EqualTo(EndNightStepStates.Skipped));
            Assert.That(Calls(), Does.Contain("poweroff"));
        }

        [Test]
        public async Task A_mount_that_cannot_park_has_its_tracking_stopped_and_the_host_still_powers_off() {
            telescope.Setup(t => t.GetInfo()).Returns(Mount(canPark: false, canFindHome: false));
            var sut = CreateSUT();
            sut.Start(shutDownHost: true);
            await FinishedAsync(sut);

            Assert.That(Calls(), Does.Contain("tracking-off"));
            Assert.That(Calls(), Does.Not.Contain("park"));
            Assert.That(Calls().Last(), Is.EqualTo("poweroff"));
        }

        [Test]
        public async Task The_host_stays_on_when_the_mount_ended_neither_parked_nor_tracking_off() {
            telescope.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            telescope.Setup(t => t.SetTrackingEnabled(false)).Returns(false);
            var sut = CreateSUT();
            sut.Start(shutDownHost: true);
            await FinishedAsync(sut);

            Assert.That(Calls(), Does.Not.Contain("poweroff"));
            var step = sut.GetStatus().Steps.Single(s => s.Id == EndNightStepIds.HostPoweroff);
            Assert.That(step.State, Is.EqualTo(EndNightStepStates.Skipped));
            Assert.That(step.Detail, Does.Contain("mount did not end parked"));
        }

        [Test]
        public async Task The_host_stays_on_when_the_user_did_not_choose_the_shutdown() {
            var sut = CreateSUT();
            sut.Start(shutDownHost: false);
            await FinishedAsync(sut);

            Assert.That(Calls(), Does.Not.Contain("poweroff"));
            Assert.That(Calls(), Does.Contain("park"));
            Assert.That(StepState(sut.GetStatus(), EndNightStepIds.HostPoweroff), Is.EqualTo(EndNightStepStates.Skipped));
            Assert.That(sut.GetStatus().State, Is.EqualTo(EndNightStates.Complete));
        }

        // #2143 — End Night is a Rig Occupancy routine, and its ladder stops every routine first: never End Night itself.
        [Test]
        public async Task End_Night_in_the_register_is_not_stopped_by_its_own_ladder() {
            EndNightService? sut = null;
            var occupancy = new RigOccupancy().RegisterEndNight(() => sut);
            sut = CreateSUT(occupancy: occupancy);

            sut.Start(shutDownHost: true);
            await FinishedAsync(sut);

            Assert.That(Calls(), Is.EqualTo(DocumentedOrder));
            Assert.That(sut.GetStatus().State, Is.EqualTo(EndNightStates.Complete));
        }

        // #2143 — Emergency Stop ends End Night at its next step: the park it had started does not lead to more moves.
        [Test]
        public async Task Emergency_Stop_ends_End_Night_at_its_next_step() {
            var parking = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            telescope.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns(async () => { Log("park"); await parking.Task; return true; });
            EndNightService? sut = null;
            var occupancy = new RigOccupancy().RegisterEndNight(() => sut);
            sut = CreateSUT(occupancy: occupancy);
            sut.Start(shutDownHost: true);
            await WaitUntilAsync(() => Calls().Contains("park"), "the park step is in flight");
            Assert.That(occupancy.Holder(RigHolding.Mount), Is.EqualTo(RigOccupancy.EndNight), "End Night holds the mount");

            var stop = new EmergencyStopService(occupancy: occupancy).ExecuteAsync();
            await WaitUntilAsync(() => sut.GetStatus().CancelRequested, "Emergency Stop cancelled End Night");
            parking.SetResult(true);
            var result = await stop.WaitAsync(TimeSpan.FromSeconds(10));
            await FinishedAsync(sut);

            Assert.Multiple(() => {
                Assert.That(result.FailedRungs, Is.Empty);
                Assert.That(sut.GetStatus().State, Is.EqualTo(EndNightStates.Cancelled));
                Assert.That(Calls(), Does.Not.Contain("poweroff"));
            });
        }

        [Test]
        public async Task A_cancel_during_a_step_stops_at_the_next_boundary_and_leaves_the_rig_as_it_is() {
            var parking = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            telescope.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns(async () => { Log("park"); await parking.Task; return true; });
            var sut = CreateSUT();
            sut.Start(shutDownHost: true);
            await WaitUntilAsync(() => Calls().Contains("park"), "the park step is in flight");

            var cancelled = sut.Cancel();
            Assert.That(cancelled.CancelRequested, Is.True);
            parking.SetResult(true);
            await FinishedAsync(sut);

            Assert.That(Calls(), Does.Not.Contain("disconnect-filter-wheel"));
            Assert.That(Calls(), Does.Not.Contain("disconnect-camera"));
            Assert.That(Calls(), Does.Not.Contain("poweroff"));
            var status = sut.GetStatus();
            Assert.That(status.State, Is.EqualTo(EndNightStates.Cancelled));
            Assert.That(StepState(status, EndNightStepIds.ParkMount), Is.EqualTo(EndNightStepStates.Done), "the in-flight step finished");
            Assert.That(StepState(status, EndNightStepIds.DisconnectDevices), Is.EqualTo(EndNightStepStates.Skipped));
        }

        [Test]
        public async Task A_cancel_during_the_warm_up_leaves_the_cooler_on_and_the_camera_connected() {
            var setCoolerCalls = 0;
            camera.Setup(c => c.SetCoolerAsync(It.IsAny<bool>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .Callback<bool, double?, CancellationToken>((on, _, _) => { if (on) Interlocked.Increment(ref setCoolerCalls); else Log("cooler-off"); })
                .Returns(Task.CompletedTask);
            var sut = CreateSUT(warmTimeScale: 1); // one real minute between ramp steps, so the cancel lands inside one
            sut.Start(shutDownHost: true);
            await WaitUntilAsync(() => Volatile.Read(ref setCoolerCalls) >= 1, "the ramp started");
            sut.Cancel();
            await FinishedAsync(sut);

            Assert.That(Calls(), Does.Not.Contain("cooler-off"));
            Assert.That(Calls(), Does.Not.Contain("disconnect-camera"));
            Assert.That(Calls(), Does.Not.Contain("poweroff"));
            Assert.That(sut.GetStatus().State, Is.EqualTo(EndNightStates.Cancelled));
        }

        [Test]
        public async Task Nothing_cancels_the_run_once_the_host_poweroff_has_started() {
            poweroff.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sut = CreateSUT();
            sut.Start(shutDownHost: true);
            await WaitUntilAsync(() => Calls().Contains("poweroff"), "the poweroff started");

            Assert.That(sut.GetStatus().Cancellable, Is.False);
            var answer = sut.Cancel();
            Assert.That(answer.CancelRequested, Is.False);
            Assert.That(answer.Cancellable, Is.False);
            poweroff.Gate.SetResult(true);
            await FinishedAsync(sut);

            Assert.That(sut.GetStatus().State, Is.EqualTo(EndNightStates.Complete));
            Assert.That(StepState(sut.GetStatus(), EndNightStepIds.HostPoweroff), Is.EqualTo(EndNightStepStates.Done));
        }

        [Test]
        public async Task A_second_start_while_one_runs_is_refused_with_the_declared_cause() {
            var parking = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            telescope.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns(async () => { Log("park"); await parking.Task; return true; });
            var sut = CreateSUT();
            sut.Start(shutDownHost: true);
            await WaitUntilAsync(() => Calls().Contains("park"), "the run is in flight");

            var refusal = Assert.Throws<RefusalException>(() => sut.Start(shutDownHost: true));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.EndNightInProgress));
            parking.SetResult(true);
            await FinishedAsync(sut);
            Assert.That(Calls().Count(c => c == "abort-run"), Is.EqualTo(1), "the refused start ran nothing");

            Assert.DoesNotThrow(() => sut.Start(shutDownHost: false), "a finished action can be started again");
            await FinishedAsync(sut);
        }

        [Test]
        public async Task A_step_that_fails_is_reported_and_the_next_safe_step_still_runs() {
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("guider is gone"));
            focuser.Setup(f => f.DisconnectAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("focuser is gone"));
            var sut = CreateSUT();
            sut.Start(shutDownHost: true);
            await FinishedAsync(sut);

            var status = sut.GetStatus();
            Assert.That(StepState(status, EndNightStepIds.StopGuiding), Is.EqualTo(EndNightStepStates.Failed));
            Assert.That(StepState(status, EndNightStepIds.DisconnectDevices), Is.EqualTo(EndNightStepStates.Failed));
            Assert.That(Calls(), Does.Contain("park"));
            Assert.That(Calls(), Does.Contain("disconnect-rotator"), "the devices after the failing one are still disconnected");
            Assert.That(Calls(), Does.Contain("disconnect-camera"));
            Assert.That(Calls().Last(), Is.EqualTo("poweroff"), "the mount is parked, so the chosen poweroff goes ahead");
            Assert.That(posted.Single().Severity, Is.EqualTo(NotificationSeverity.Warning));
            Assert.That(posted.Single().Message, Does.Contain("Guider stop FAILED").And.Contain("Focuser disconnect FAILED"));
        }

        [Test]
        public async Task The_camera_stays_connected_when_the_warm_up_failed() {
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("camera is gone"));
            var sut = CreateSUT();
            sut.Start(shutDownHost: true);
            await FinishedAsync(sut);

            var status = sut.GetStatus();
            Assert.That(StepState(status, EndNightStepIds.WarmCooler), Is.EqualTo(EndNightStepStates.Failed));
            Assert.That(StepState(status, EndNightStepIds.DisconnectCamera), Is.EqualTo(EndNightStepStates.Skipped));
            Assert.That(Calls(), Does.Not.Contain("disconnect-camera"));
            Assert.That(Calls(), Does.Contain("notify"));
        }

        [Test]
        public async Task A_poweroff_the_system_refuses_is_reported_as_a_failed_step() {
            poweroff.Result = new HostPoweroffResult(false, "polkit said no");
            var sut = CreateSUT();
            sut.Start(shutDownHost: true);
            await FinishedAsync(sut);

            var step = sut.GetStatus().Steps.Single(s => s.Id == EndNightStepIds.HostPoweroff);
            Assert.That(step.State, Is.EqualTo(EndNightStepStates.Failed));
            Assert.That(step.Detail, Does.Contain("polkit said no"));
        }

        [Test]
        public async Task Progress_goes_out_as_end_night_progress_events_from_start_to_finish() {
            var sut = CreateSUT();
            sut.Start(shutDownHost: true);
            await FinishedAsync(sut);

            List<JsonElement> events;
            lock (progress) events = progress.ToList();
            Assert.That(events.First().GetProperty("state").GetString(), Is.EqualTo(EndNightStates.Running));
            Assert.That(events.First().GetProperty("step").ValueKind, Is.EqualTo(JsonValueKind.Null));
            var park = events.Where(e => e.GetProperty("step").GetString() == EndNightStepIds.ParkMount)
                .Select(e => e.GetProperty("step_state").GetString()).ToList();
            Assert.That(park, Is.EqualTo(new[] { EndNightStepStates.Running, EndNightStepStates.Done }));
            Assert.That(events.Last().GetProperty("state").GetString(), Is.EqualTo(EndNightStates.Complete));
            Assert.That(events.Last().GetProperty("cancellable").GetBoolean(), Is.False);
        }

        // ─── the endpoint wiring ───

        [Test]
        public async Task The_start_endpoint_answers_202_with_the_status_and_the_status_read_matches() {
            var sut = CreateSUT();
            var result = EndNightEndpoints.Start(null, sut);

            Assert.That(result, Is.InstanceOf<Accepted<EndNightStatusDto>>());
            var accepted = (Accepted<EndNightStatusDto>)result;
            Assert.That(accepted.Location, Is.EqualTo("/api/v1/end-night/status"));
            Assert.That(accepted.Value!.State, Is.EqualTo(EndNightStates.Running));
            Assert.That(accepted.Value.ShutDownHost, Is.True, "a bare POST keeps the default: shut the host down");
            await FinishedAsync(sut);
            Assert.That(Calls(), Does.Contain("poweroff"));

            var again = (Accepted<EndNightStatusDto>)EndNightEndpoints.Start(new EndNightStartRequestDto(ShutDownHost: false), sut);
            Assert.That(again.Value!.ShutDownHost, Is.False);
            await FinishedAsync(sut);
        }

        [Test]
        public void The_status_before_any_start_is_idle_with_no_steps() {
            var status = CreateSUT().GetStatus();
            Assert.That(status.State, Is.EqualTo(EndNightStates.Idle));
            Assert.That(status.Steps, Is.Empty);
            Assert.That(status.Cancellable, Is.False);
        }
    }
}