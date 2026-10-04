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
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1192 — the guider host is gated in two places. (1) §63.3 recovery reconnects only to the
    /// host:port that dropped, under the profile that was active when it dropped: a profile switch
    /// mid-recovery abandons the pass instead of "recovering" onto the new profile's guider. (2) The
    /// local systemd unit is only started/restarted when the configured host IS this machine; a
    /// remote guider gets a plain reconnect and a log line, never a <c>systemctl start</c>.
    /// Driven against the bench <see cref="FakeGuider"/>.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    [Category("bench")]
    public class GuiderHostGatingTest {

        private static readonly Guid ProfileA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid ProfileB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

        // A coordinator whose first backoff blocks on `gate`; the unit then reads Active → Recovered.
        private static GuiderRecoveryCoordinator GatedRecovery(Task gate, Mock<IGuiderProcessSupervisor> supervisor) {
            supervisor.Setup(s => s.QueryStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(GuiderProcessStatus.Active);
            return new GuiderRecoveryCoordinator(supervisor.Object, Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance,
                new[] { TimeSpan.Zero }, (_, ct) => gate.WaitAsync(ct), TimeSpan.FromSeconds(1));
        }

        private static GuiderService NewService(HeadlessProfileService profile, GuiderRecoveryCoordinator recovery,
                Mock<IGuiderProcessSupervisor> supervisor, Func<Guid?>? activeProfileId = null,
                INotificationService? notifications = null) =>
            new(profile, recovery, NullLogger<GuiderService>.Instance, supervisor.Object,
                ws: null, profileStore: null, sequencerResolver: () => Mock.Of<ISequencerService>(),
                notifications: notifications ?? Mock.Of<INotificationService>(), faultLog: null,
                activeProfileIdResolver: activeProfileId) {
                ReconnectAttemptInterval = TimeSpan.FromMilliseconds(200),
                ReconnectGraceFromSeconds = _ => TimeSpan.FromSeconds(20),
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

        private static async Task<EquipmentConnectionState?> StateAsync(GuiderService svc) =>
            (await svc.GetAsync(CancellationToken.None).ConfigureAwait(false))?.State;

        // A loopback port nothing listens on: bind an ephemeral one, read it back, release it.
        private static int ClosedLoopbackPort() {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        // ── (1) recovery reconnects only to the connection that dropped ──

        [Test]
        public async Task Recovery_abandons_the_reconnect_when_the_profile_switched_to_another_host() {
            await using var fakeA = StartFake();
            await using var fakeB = StartFake();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var supervisor = new Mock<IGuiderProcessSupervisor>();
            var profile = new HeadlessProfileService();
            Guid? activeId = ProfileA;
            using var svc = NewService(profile, GatedRecovery(gate.Task, supervisor), supervisor, () => activeId);
            await ConnectAndDropAsync(svc, fakeA).ConfigureAwait(false);

            // POST /profiles/{id}/select: the live store swaps to profile B (a different guider box)
            // without touching the guider, while A's recovery pass is still in flight.
            activeId = ProfileB;
            profile.ActiveProfile.GuiderSettings.PHD2ServerHost = "127.0.0.1";
            profile.ActiveProfile.GuiderSettings.PHD2ServerPort = fakeB.Port;

            gate.SetResult(); // the unit reads Active → Recovered → auto-reconnect would run now
            await Task.Delay(2000).ConfigureAwait(false); // several ReconnectAttemptInterval ticks

            Assert.That(fakeB.ConnectionCount, Is.Zero, "recovery of profile A's guider connected to profile B's host");
            Assert.That(fakeA.ConnectionCount, Is.Zero, "a switched-away profile's guider must not be reconnected either");
            Assert.That(svc.GetInfo().Connected, Is.False);
            Assert.That(await StateAsync(svc).ConfigureAwait(false), Is.EqualTo(EquipmentConnectionState.Error),
                "an abandoned recovery leaves the guider in Error for the new profile's own connect");
            Assert.That(profile.ActiveProfile.GuiderSettings.PHD2ServerPort, Is.EqualTo(fakeB.Port),
                "the abandoned reconnect must not write profile A's target into profile B");

            // The pass is over, so the sequencer's connect falls through to B's target (a clean restart).
            Assert.That(await svc.Connect().ConfigureAwait(false), Is.True, "profile B's own connect must work after the abandoned pass");
            Assert.That(await WaitUntilAsync(() => fakeB.ConnectionCount >= 1), Is.True);
            Assert.That(fakeA.ConnectionCount, Is.Zero);
        }

        [Test]
        public async Task Recovery_reconnects_to_the_dropped_host_when_the_profile_is_unchanged() {
            await using var fake = StartFake();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var supervisor = new Mock<IGuiderProcessSupervisor>();
            var profile = new HeadlessProfileService();
            Guid? activeId = ProfileA;
            using var svc = NewService(profile, GatedRecovery(gate.Task, supervisor), supervisor, () => activeId);
            await ConnectAndDropAsync(svc, fake).ConfigureAwait(false);

            gate.SetResult();
            Assert.That(await WaitUntilAsync(() => svc.GetInfo().Connected), Is.True, "same profile, same host: recovery must reconnect");
            Assert.That(fake.ConnectionCount, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void RecoveryAbandonReason_flags_a_profile_or_target_change_only() {
            var target = new GuiderService.RecoveryTarget("127.0.0.1", 4400, ProfileA);
            Assert.That(GuiderService.RecoveryAbandonReason(target, "127.0.0.1", 4400, ProfileA), Is.Null);
            // Host comparison is case-insensitive (DNS names).
            Assert.That(GuiderService.RecoveryAbandonReason(new GuiderService.RecoveryTarget("Pi.local", 4400, ProfileA), "pi.local", 4400, ProfileA), Is.Null);
            Assert.That(GuiderService.RecoveryAbandonReason(target, "127.0.0.1", 4400, ProfileB), Is.Not.Null, "profile switched");
            Assert.That(GuiderService.RecoveryAbandonReason(target, "192.0.2.10", 4400, ProfileA), Is.Not.Null, "host changed");
            Assert.That(GuiderService.RecoveryAbandonReason(target, "127.0.0.1", 4401, ProfileA), Is.Not.Null, "port changed");
            // No repository (benches/tests): only the target is compared.
            var noProfile = new GuiderService.RecoveryTarget("127.0.0.1", 4400, null);
            Assert.That(GuiderService.RecoveryAbandonReason(noProfile, "127.0.0.1", 4400, null), Is.Null);
        }

        // ── (2) local-unit control only for a local guider host ──

        [Test]
        public void IsLocalGuiderHost_recognises_loopback_this_machine_and_its_addresses() {
            var machine = "raspberrypi";
            var addresses = new[] { IPAddress.Parse("192.168.4.1"), IPAddress.Parse("10.0.0.7") };
            foreach (var local in new[] { "localhost", "LOCALHOST", "127.0.0.1", "127.0.0.2", "::1", "[::1]", "", "  ",
                                          "raspberrypi", "RaspberryPi.local", "raspberrypi.lan", "192.168.4.1", "10.0.0.7", "0.0.0.0" }) {
                Assert.That(GuiderService.IsLocalGuiderHost(local, machine, addresses), Is.True, $"'{local}' is this machine");
            }
            foreach (var remote in new[] { "192.0.2.1", "192.168.4.2", "phd2-laptop", "phd2-laptop.lan", "raspberrypi2" }) {
                Assert.That(GuiderService.IsLocalGuiderHost(remote, machine, addresses), Is.False, $"'{remote}' is not this machine");
            }
            Assert.That(GuiderService.IsLocalGuiderHost(null, machine, addresses), Is.True, "no host means the default (local) guider");
        }

        [Test]
        public void IsLocalGuiderHost_ignores_the_IPv6_scope_id_of_an_interface_address() {
            // The NIC reports its link-local address with a scope (fe80::1234%2); a profile host is
            // typed without one. IPAddress.Equals compares the scope, so this needs a byte compare.
            var addresses = new[] { IPAddress.Parse("fe80::1234%2") };
            Assert.That(GuiderService.IsLocalGuiderHost("fe80::1234", "raspberrypi", addresses), Is.True);
            Assert.That(GuiderService.IsLocalGuiderHost("[fe80::1234]", "raspberrypi", addresses), Is.True);
            Assert.That(GuiderService.IsLocalGuiderHost("fe80::1235", "raspberrypi", addresses), Is.False);
        }

        [Test]
        public async Task IsLocalGuiderHostAsync_counts_a_name_that_resolves_to_our_own_address_as_local() {
            var ours = new[] { IPAddress.Parse("192.168.4.1") };
            Task<IPAddress[]> Resolve(string name, CancellationToken _) => name switch {
                "ara-alias.lan" => Task.FromResult(new[] { IPAddress.Parse("192.168.4.1") }),
                "phd2-laptop.lan" => Task.FromResult(new[] { IPAddress.Parse("192.168.4.2") }),
                _ => Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException()),
            };
            Assert.That(await GuiderService.IsLocalGuiderHostAsync("ara-alias.lan", "raspberrypi", ours, Resolve, CancellationToken.None), Is.True,
                "a CNAME/alias for one of our own addresses is this machine");
            Assert.That(await GuiderService.IsLocalGuiderHostAsync("phd2-laptop.lan", "raspberrypi", ours, Resolve, CancellationToken.None), Is.False);
            Assert.That(await GuiderService.IsLocalGuiderHostAsync("nowhere.invalid", "raspberrypi", ours, Resolve, CancellationToken.None), Is.False,
                "an unresolvable name cannot be the local unit; the resolver fault must not escape");
            Assert.That(await GuiderService.IsLocalGuiderHostAsync("192.168.4.2", "raspberrypi", ours, Resolve, CancellationToken.None), Is.False,
                "a literal never hits the resolver");
        }

        [Test]
        public async Task Connect_to_a_remote_host_never_asks_systemd_to_start_the_local_unit() {
            var supervisor = new Mock<IGuiderProcessSupervisor>();
            var profile = new HeadlessProfileService();
            using var svc = NewService(profile, GatedRecovery(Task.CompletedTask, supervisor), supervisor);
            svc.IsLocalGuiderHostDecision = static (_, _) => Task.FromResult(false); // the profile names another machine

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", ClosedLoopbackPort()), null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => svc.GetInfo().Connected == false && StateAsync(svc).Result == EquipmentConnectionState.Error), Is.True,
                "an unreachable remote guider must settle into Error");
            supervisor.Verify(s => s.RequestStart(), Times.Never, "a remote guider host must not start the LOCAL systemd unit");
        }

        [Test]
        public async Task Connect_to_a_local_host_still_asks_systemd_to_start_the_unit() {
            var supervisor = new Mock<IGuiderProcessSupervisor>();
            var profile = new HeadlessProfileService();
            using var svc = NewService(profile, GatedRecovery(Task.CompletedTask, supervisor), supervisor);

            // 127.0.0.1 is loopback → local by the real decision; the closed port makes the probe fail.
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", ClosedLoopbackPort()), null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => supervisor.Invocations.Count > 0 && supervisor.Invocations[0].Method.Name == nameof(IGuiderProcessSupervisor.RequestStart)), Is.True,
                "a local unreachable guider is started through systemd before connecting");
        }

        [Test]
        public async Task Remote_host_recovery_skips_the_systemd_tree_and_still_reconnects() {
            await using var fake = StartFake();
            var supervisor = new Mock<IGuiderProcessSupervisor>();
            var notifications = new Mock<INotificationService>();
            var profile = new HeadlessProfileService();
            using var svc = NewService(profile, GatedRecovery(Task.CompletedTask, supervisor), supervisor, () => ProfileA, notifications.Object);
            svc.IsLocalGuiderHostDecision = static (_, _) => Task.FromResult(false);
            await ConnectAndDropAsync(svc, fake).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => svc.GetInfo().Connected), Is.True,
                "a remote guider that comes back must be reconnected within the grace window");
            supervisor.Verify(s => s.QueryStatusAsync(It.IsAny<CancellationToken>()), Times.Never,
                "a remote guider has no local unit to supervise");
            supervisor.Verify(s => s.RequestRestart(), Times.Never);
            supervisor.Verify(s => s.RequestStart(), Times.Never);
            // The skipped coordinator would have posted "Guider connection lost"; with no sequence
            // running the fault reaction stays quiet too, so the remote branch must alert on its own.
            notifications.Verify(n => n.CreateAsync(It.Is<NotificationDto>(d => d.Title == "Guider connection lost"), It.IsAny<CancellationToken>()), Times.Once,
                "a dropped remote guider must be reported before the retry window runs");
        }

        [Test]
        public async Task RestartGuiderAsync_is_a_logged_no_op_for_a_remote_guider_host() {
            var supervisor = new Mock<IGuiderProcessSupervisor>();
            var profile = new HeadlessProfileService();
            using var svc = NewService(profile, GatedRecovery(Task.CompletedTask, supervisor), supervisor);
            svc.IsLocalGuiderHostDecision = static (_, _) => Task.FromResult(false);

            var accepted = await svc.RestartGuiderAsync("idem-r", CancellationToken.None).ConfigureAwait(false);
            Assert.That(accepted.OperationType, Is.EqualTo("guider.restart"));
            supervisor.Verify(s => s.RequestRestart(), Times.Never, "the local unit is not the guider this profile uses");
        }
    }
}
