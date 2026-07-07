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
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §63.3 — active hang-detection + auto-reconnect on <see cref="GuiderService"/>:
    /// a wedged-but-connected daemon (failed ping streak) converges on the same
    /// link-down path as a socket death, a ping success resets the streak, and a
    /// recovered process is reconnected automatically within the profile's grace
    /// window. Driven against the bench <see cref="FakeGuider"/> with the ping
    /// probe seam and compressed intervals.
    /// </summary>
    [TestFixture]
    [Category("bench")]
    public class GuiderActivePollTest {

        private static GuiderRecoveryCoordinator RecoveredImmediately() {
            var supervisor = new Mock<IGuiderProcessSupervisor>();
            supervisor.Setup(s => s.QueryStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(GuiderProcessStatus.Active);
            return new GuiderRecoveryCoordinator(supervisor.Object, Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance,
                new[] { TimeSpan.Zero }, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));
        }

        private static Mock<IProfileStore> PolicyStore(string onGuiderLost = "pause_and_retry", int retrySec = 60) {
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetSafetyPolicies()).Returns(new SafetyPoliciesDto(
                OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 2, MeridianRecenter: true, MeridianRecalGuider: false,
                OnAltitudeLimit: "pause", ParkIfNoMoreTargets: true, OnGuiderLost: onGuiderLost,
                GuiderRetryTimeoutSec: retrySec, SkipTargetIfRecoveryFails: false));
            return profiles;
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 15000) {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < timeoutMs) {
                await Task.Delay(50).ConfigureAwait(false);
            }
            return condition();
        }

        private static async Task<EquipmentConnectionState> WaitForStateAsync(GuiderService svc, EquipmentConnectionState want, int timeoutMs = 15000) {
            var sw = Stopwatch.StartNew();
            var last = EquipmentConnectionState.Disconnected;
            while (sw.ElapsedMilliseconds < timeoutMs) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                last = dto?.State ?? EquipmentConnectionState.Disconnected;
                if (last == want) {
                    return last;
                }
                await Task.Delay(50).ConfigureAwait(false);
            }
            return last;
        }

        [Test]
        public async Task Three_failed_pings_declare_the_wedged_daemon_down_and_trigger_the_fault_flow() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            var sequencer = new Mock<ISequencerService>();
            var pauseRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .Callback(() => pauseRequested.TrySetResult())
                .ReturnsAsync(new List<Guid> { Guid.NewGuid() });
            using var svc = new GuiderService(new HeadlessProfileService(), RecoveredImmediately(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(),
                ws: null, profileStore: PolicyStore().Object, sequencerResolver: () => sequencer.Object,
                notifications: Mock.Of<INotificationService>()) {
                PingIntervalIdle = TimeSpan.FromMilliseconds(40),
                PingIntervalGuiding = TimeSpan.FromMilliseconds(40),
                // The daemon is "wedged": socket alive (FakeGuider still up), RPC dead.
                PingProbe = static (_, _) => Task.FromResult(false),
                // Keep the post-declaration auto-reconnect from succeeding instantly and
                // muddying the Error-state assertion: recovery still runs, but reconnect
                // attempts land AFTER we've observed Error (state then legitimately heals).
                ReconnectAttemptInterval = TimeSpan.FromSeconds(3),
            };

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(await WaitForStateAsync(svc, EquipmentConnectionState.Connected), Is.EqualTo(EquipmentConnectionState.Connected),
                "never reached Connected against the fake guider");

            // The wedge: pings fail; after the threshold the link is declared down —
            // the §42.2 fault flow pauses the run even though the socket never died.
            Assert.That(await Task.WhenAny(pauseRequested.Task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false),
                Is.SameAs(pauseRequested.Task),
                "the failed-ping streak never reached the §42.2 fault flow");
        }

        [Test]
        public async Task A_ping_success_resets_the_streak() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            var sequencer = new Mock<ISequencerService>();
            var probeCalls = 0;
            using var svc = new GuiderService(new HeadlessProfileService(), RecoveredImmediately(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(),
                ws: null, profileStore: PolicyStore().Object, sequencerResolver: () => sequencer.Object,
                notifications: Mock.Of<INotificationService>()) {
                PingIntervalIdle = TimeSpan.FromMilliseconds(30),
                PingIntervalGuiding = TimeSpan.FromMilliseconds(30),
                // fail, fail, succeed, repeat — the streak never reaches 3.
                PingProbe = (_, _) => Task.FromResult(Interlocked.Increment(ref probeCalls) % 3 == 0),
            };

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(await WaitForStateAsync(svc, EquipmentConnectionState.Connected), Is.EqualTo(EquipmentConnectionState.Connected));

            Assert.That(await WaitUntilAsync(() => Volatile.Read(ref probeCalls) >= 9), Is.True, "the ping loop never ran");
            var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(dto?.State, Is.EqualTo(EquipmentConnectionState.Connected),
                "an interleaved ping success must reset the streak — the link stays up");
            sequencer.Verify(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task A_recovered_process_is_reconnected_automatically_within_the_grace_window() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            var notifications = new Mock<INotificationService>();
            var posted = new List<NotificationDto>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .Callback<NotificationDto, CancellationToken>((n, _) => { lock (posted) { posted.Add(n); } })
                .Returns(Task.CompletedTask);
            using var svc = new GuiderService(new HeadlessProfileService(), RecoveredImmediately(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(),
                ws: null, profileStore: PolicyStore(retrySec: 60).Object, sequencerResolver: () => Mock.Of<ISequencerService>(),
                notifications: notifications.Object) {
                ReconnectAttemptInterval = TimeSpan.FromMilliseconds(200),
                ReconnectGraceFromSeconds = _ => TimeSpan.FromSeconds(20),
            };

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(await WaitForStateAsync(svc, EquipmentConnectionState.Connected), Is.EqualTo(EquipmentConnectionState.Connected),
                "never reached Connected against the fake guider");
            Assert.That(await WaitUntilAsync(() => fake.ConnectionCount >= 1), Is.True, "event stream never settled");

            // The daemon "crashes": the socket drops, recovery reports Recovered
            // immediately (mock supervisor says the unit is active), and the §63.3
            // auto-reconnect re-establishes against the still-listening fake.
            Assert.That(fake.DropConnections(), Is.GreaterThan(0), "expected a live connection to drop");
            Assert.That(await WaitForStateAsync(svc, EquipmentConnectionState.Connected, timeoutMs: 20000),
                Is.EqualTo(EquipmentConnectionState.Connected),
                "the auto-reconnect never re-established the session");
            Assert.That(await WaitUntilAsync(() => { lock (posted) { return posted.Exists(n => n.Title.Contains("reconnected", StringComparison.OrdinalIgnoreCase)); } }),
                Is.True, "the reconnect success notification was never posted");
        }
    }
}
