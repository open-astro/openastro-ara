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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.Trigger.Connect;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1123 — the sequencer's <see cref="ReconnectTrigger"/> fires while the guider sits in Error after
    /// a PHD2 socket drop. The §63.3 recovery pass is already reconnecting in the background, so the
    /// mediator's <c>Connect()</c> must wait on that pass (bounded by the retry timeout) instead of
    /// failing the run, fall back to a plain reconnect when no pass will do it (non-systemd host), and
    /// fail cleanly when nothing reconnects in time. Driven against the bench <see cref="FakeGuider"/>.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    [Category("bench")]
    public class GuiderMediatorReconnectTest {

        // A coordinator whose first backoff blocks on `gate`, so the recovery pass stays in flight
        // until the test releases it; the unit then reads Active → Recovered → auto-reconnect.
        private static GuiderRecoveryCoordinator GatedRecovery(Task gate, Mock<IGuiderProcessSupervisor>? supervisor = null) {
            supervisor ??= new Mock<IGuiderProcessSupervisor>();
            supervisor.Setup(s => s.QueryStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(GuiderProcessStatus.Active);
            return new GuiderRecoveryCoordinator(supervisor.Object, Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance,
                new[] { TimeSpan.Zero }, (_, ct) => gate.WaitAsync(ct), TimeSpan.FromSeconds(1));
        }

        // Not a systemd host: the coordinator reads Unknown and returns Unsupervised; nothing reconnects.
        private static GuiderRecoveryCoordinator UnsupervisedRecovery(Mock<IGuiderProcessSupervisor> supervisor) {
            supervisor.Setup(s => s.QueryStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(GuiderProcessStatus.Unknown);
            return new GuiderRecoveryCoordinator(supervisor.Object, Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance,
                new[] { TimeSpan.Zero }, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));
        }

        private static GuiderService NewService(HeadlessProfileService profile, GuiderRecoveryCoordinator recovery, TimeSpan grace) =>
            new(profile, recovery, NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(),
                ws: null, profileStore: null, sequencerResolver: () => Mock.Of<ISequencerService>(),
                notifications: Mock.Of<INotificationService>()) {
                ReconnectAttemptInterval = TimeSpan.FromMilliseconds(200),
                ReconnectGraceFromSeconds = _ => grace,
            };

        private static ReconnectTrigger GuiderTrigger(HeadlessProfileService profile, IGuiderMediator guider) =>
            new(profile, Mock.Of<ICameraMediator>(), Mock.Of<IFilterWheelMediator>(), Mock.Of<IFocuserMediator>(),
                Mock.Of<IRotatorMediator>(), Mock.Of<ITelescopeMediator>(), guider, Mock.Of<ISwitchMediator>(),
                Mock.Of<IFlatDeviceMediator>(), Mock.Of<IWeatherDataMediator>(), Mock.Of<IDomeMediator>(),
                Mock.Of<ISafetyMonitorMediator>()) {
                SelectedDevice = "Guider",
            };

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 15000) {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < timeoutMs) {
                await Task.Delay(50).ConfigureAwait(false);
            }
            return condition();
        }

        private static async Task ConnectAndDropAsync(GuiderService svc, FakeGuider fake) {
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => svc.GetInfo().Connected), Is.True, "never reached Connected against the fake guider");
            Assert.That(await WaitUntilAsync(() => fake.ConnectionCount >= 1), Is.True, "event stream never settled");
            Assert.That(fake.DropConnections(), Is.GreaterThan(0), "expected a live connection to drop");
            Assert.That(await WaitUntilAsync(() => !svc.GetInfo().Connected), Is.True, "the drop never surfaced");
        }

        private static FakeGuider StartFake() {
            var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            return fake;
        }

        [Test]
        public async Task Trigger_firing_during_recovery_waits_for_the_auto_reconnect_instead_of_failing_the_run() {
            await using var fake = StartFake();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var profile = new HeadlessProfileService();
            using var svc = NewService(profile, GatedRecovery(gate.Task), grace: TimeSpan.FromSeconds(20));
            await ConnectAndDropAsync(svc, fake).ConfigureAwait(false);

            var trigger = GuiderTrigger(profile, svc);
            Assert.That(trigger.ShouldTrigger(null, null), Is.True, "the trigger must fire while the guider is in Error");
            var run = trigger.Execute(null!, new Progress<ApplicationStatus>(), CancellationToken.None);

            // Longer than the coordinator's first 1 s backoff: the old inert Connect() failed here.
            await Task.Delay(1500).ConfigureAwait(false);
            Assert.That(run.IsCompleted, Is.False, "the trigger failed the run before the in-flight recovery could reconnect");

            gate.SetResult(); // the unit reads Active → Recovered → auto-reconnect against the fake
            Assert.That(await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false), Is.SameAs(run),
                "the trigger never returned after recovery reconnected");
            await Assert.DoesNotThrowAsync(() => run, "a recovery that lands within the retry timeout must not fail the run");
            Assert.That(svc.GetInfo().Connected, Is.True);
        }

        [Test]
        public async Task Trigger_fails_cleanly_when_recovery_does_not_land_within_the_retry_timeout() {
            await using var fake = StartFake();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var profile = new HeadlessProfileService();
            var grace = TimeSpan.FromSeconds(3);
            using var svc = NewService(profile, GatedRecovery(gate.Task), grace);
            await ConnectAndDropAsync(svc, fake).ConfigureAwait(false);

            var trigger = GuiderTrigger(profile, svc);
            var sw = Stopwatch.StartNew();
            var run = trigger.Execute(null!, new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false), Is.SameAs(run),
                "the trigger must give up once the retry timeout passes, not hang");
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => run);
            Assert.That(sw.Elapsed, Is.GreaterThanOrEqualTo(grace - TimeSpan.FromMilliseconds(250)),
                "the trigger failed before waiting out the retry timeout");

            // The mediator never superseded the recovery pass: releasing it still reconnects.
            gate.SetResult();
            Assert.That(await WaitUntilAsync(() => svc.GetInfo().Connected), Is.True,
                "the in-flight recovery pass was cancelled by the trigger's give-up");
        }

        [Test]
        public async Task Unsupervised_host_trigger_does_a_plain_reconnect_without_a_second_recovery_pass() {
            await using var fake = StartFake();
            var supervisor = new Mock<IGuiderProcessSupervisor>();
            var profile = new HeadlessProfileService();
            using var svc = NewService(profile, UnsupervisedRecovery(supervisor), grace: TimeSpan.FromSeconds(20));
            await ConnectAndDropAsync(svc, fake).ConfigureAwait(false);

            var trigger = GuiderTrigger(profile, svc);
            Assert.That(trigger.ShouldTrigger(null, null), Is.True);
            var run = trigger.Execute(null!, new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false), Is.SameAs(run));
            await Assert.DoesNotThrowAsync(() => run, "on a non-systemd host the trigger must reconnect the guider itself");
            Assert.That(svc.GetInfo().Connected, Is.True);
            supervisor.Verify(s => s.QueryStatusAsync(It.IsAny<CancellationToken>()), Times.Once,
                "the reconnect must not start a second coordinator pass");
        }

        [Test]
        public async Task Unsupervised_host_trigger_fails_cleanly_when_the_guider_is_gone() {
            var fake = StartFake();
            var supervisor = new Mock<IGuiderProcessSupervisor>();
            var profile = new HeadlessProfileService();
            var grace = TimeSpan.FromSeconds(2);
            using var svc = NewService(profile, UnsupervisedRecovery(supervisor), grace);
            await ConnectAndDropAsync(svc, fake).ConfigureAwait(false);
            await fake.DisposeAsync().ConfigureAwait(false); // PHD2 is gone for good

            var trigger = GuiderTrigger(profile, svc);
            var run = trigger.Execute(null!, new Progress<ApplicationStatus>(), CancellationToken.None);
            Assert.That(await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false), Is.SameAs(run),
                "the plain reconnect must be bounded by the retry timeout");
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => run);
            Assert.That(svc.GetInfo().Connected, Is.False);
        }

        [Test]
        public async Task Mediator_connect_and_disconnect_drive_the_live_guider() {
            await using var fake = StartFake();
            var profile = new HeadlessProfileService();
            profile.ActiveProfile.GuiderSettings.PHD2ServerHost = "127.0.0.1";
            profile.ActiveProfile.GuiderSettings.PHD2ServerPort = fake.Port;
            using var svc = NewService(profile, UnsupervisedRecovery(new Mock<IGuiderProcessSupervisor>()), grace: TimeSpan.FromSeconds(20));

            Assert.That(await svc.Rescan().ConfigureAwait(false), Does.Contain(profile.ActiveProfile.GuiderSettings.GuiderName),
                "ConnectEquipment only calls Connect() when Rescan lists the profile's guider id");
            Assert.That(await svc.Connect().ConfigureAwait(false), Is.True);
            Assert.That(svc.GetInfo().Connected, Is.True);

            await svc.Disconnect().ConfigureAwait(false);
            Assert.That(svc.GetInfo().Connected, Is.False);
            var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(dto?.State ?? EquipmentConnectionState.Disconnected, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        [Test]
        public async Task GetInfo_reports_that_PHD2_can_clear_calibration_while_connected() {
            await using var fake = StartFake();
            var profile = new HeadlessProfileService();
            using var svc = NewService(profile, UnsupervisedRecovery(new Mock<IGuiderProcessSupervisor>()), grace: TimeSpan.FromSeconds(20));
            Assert.That(svc.GetInfo().CanClearCalibration, Is.False, "nothing connected, nothing to clear");

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => svc.GetInfo().Connected), Is.True);
            Assert.That(svc.GetInfo().CanClearCalibration, Is.True,
                "StartGuiding(ForceCalibration) validation flags a bogus issue when this stays false");
            Assert.That(svc.GetInfo().CanSetShiftRate, Is.True, "#1228 — PHD2 reports it can shift the lock position");
            var startGuiding = new OpenAstroAra.Sequencer.SequenceItem.Guider.StartGuiding(svc) { ForceCalibration = true };
            Assert.That(startGuiding.Validate(), Is.True);
            Assert.That(startGuiding.Issues, Is.Empty);
        }
    

        // #1228 — a Stop/Abort while the trigger waits on recovery cancels the wait instead of sitting out
        // the retry timeout.
        [Test]
        public async Task Trigger_cancelled_while_waiting_on_recovery_stops_at_once() {
            await using var fake = StartFake();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var profile = new HeadlessProfileService();
            using var svc = NewService(profile, GatedRecovery(gate.Task), grace: TimeSpan.FromSeconds(20));
            await ConnectAndDropAsync(svc, fake).ConfigureAwait(false);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var sw = Stopwatch.StartNew();
            var connect = svc.Connect(cts.Token);
            Assert.That(await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false), Is.SameAs(connect),
                "the connect must observe the cancelled token, not wait out the 20 s window");
            await Assert.ThatAsync(() => connect, Throws.InstanceOf<OperationCanceledException>());
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));

            gate.SetResult(); // the recovery pass was left in charge and still reconnects
            Assert.That(await WaitUntilAsync(() => svc.GetInfo().Connected), Is.True);
        }

        // #1228 — one deadline for the whole connect: a pass that ends without reconnecting late in the
        // window does not buy the fallback connect a second full window. The fallback dials a listener
        // that accepts and never answers, so it lingers in Connecting until the deadline.
        [Test]
        public async Task Connect_uses_one_deadline_across_the_pass_wait_and_the_fallback_connect() {
            var fake = StartFake(); // disposed explicitly below: PHD2 goes away mid-test
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var supervisor = new Mock<IGuiderProcessSupervisor>();
            var profile = new HeadlessProfileService();
            var grace = TimeSpan.FromSeconds(4);
            var recovery = new GuiderRecoveryCoordinator(supervisor.Object, Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance,
                new[] { TimeSpan.Zero }, (_, ct) => gate.Task.WaitAsync(ct), TimeSpan.FromSeconds(1));
            // After the gate: Unknown → Unsupervised, so the pass ends without reconnecting.
            supervisor.Setup(s => s.QueryStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(GuiderProcessStatus.Unknown);
            using var svc = NewService(profile, recovery, grace);
            await ConnectAndDropAsync(svc, fake).ConfigureAwait(false);

            // Swap the profile's target to a black hole before the fallback connect reads it.
            await fake.DisposeAsync().ConfigureAwait(false);
            using var blackHole = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            blackHole.Start();
            profile.ActiveProfile.GuiderSettings.PHD2ServerPort = ((System.Net.IPEndPoint)blackHole.LocalEndpoint).Port;

            var sw = Stopwatch.StartNew();
            var connect = svc.Connect();
            await Task.Delay(TimeSpan.FromSeconds(2.5)).ConfigureAwait(false);
            gate.SetResult(); // the pass ends Unsupervised ~2.5 s into a 4 s window
            Assert.That(await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false), Is.SameAs(connect));
            Assert.That(await connect.ConfigureAwait(false), Is.False);
            Assert.That(sw.Elapsed, Is.LessThan(grace + TimeSpan.FromSeconds(1.5)),
                "the fallback connect must share the entry deadline, not start a fresh window after the pass");
            Assert.That(sw.Elapsed, Is.GreaterThanOrEqualTo(grace - TimeSpan.FromMilliseconds(250)));
        }

        // #1228 — Disconnect() after Dispose() is a no-op, not an ObjectDisposedException.
        [Test]
        public async Task Mediator_disconnect_after_dispose_does_not_throw() {
            var profile = new HeadlessProfileService();
            var svc = NewService(profile, UnsupervisedRecovery(new Mock<IGuiderProcessSupervisor>()), grace: TimeSpan.FromSeconds(1));
            svc.Dispose();

            await Assert.ThatAsync(() => ((IGuiderMediator)svc).Disconnect(), Throws.Nothing);
        }
    }
}
