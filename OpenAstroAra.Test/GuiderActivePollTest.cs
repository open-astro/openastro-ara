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
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
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
            var sink = new Mock<IEquipmentFaultSink>();
            var published = new TaskCompletionSource<EquipmentFaultEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            sink.Setup(k => k.Publish(It.IsAny<EquipmentFaultEvent>())).Callback<EquipmentFaultEvent>(f => published.TrySetResult(f));
            using var svc = new GuiderService(new HeadlessProfileService(), RecoveredImmediately(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(),
                ws: null, profileStore: PolicyStore().Object, sequencerResolver: null,
                notifications: Mock.Of<INotificationService>(), faultSink: sink.Object) {
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
            // a link_down fault enters the hub's sink even though the socket never died.
            Assert.That(await Task.WhenAny(published.Task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false),
                Is.SameAs(published.Task),
                "the failed-ping streak never reached the §42.2 fault sink");
            var fault = await published.Task.ConfigureAwait(false);
            Assert.That(fault.DeviceType, Is.EqualTo(DeviceType.Guider));
            Assert.That(fault.Details, Is.EqualTo("link_down"));
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
            lock (posted) {
                Assert.That(posted.Find(n => n.Title == "Guider reconnected automatically")?.Message, Does.EndWith("Runs the guider loss paused resume on their own."),
                    "no guide star is lost: the runs the link loss paused resume with the link");
            }
        }

        private static readonly string[] CameraThenLink = ["equipment_disconnected", "link_down"];

        // §42.2 — what the guider hands the hub: the shared reaction ends its guider episode only on a
        // Recovered for the fault it published.
        private sealed class RecordingSink : IEquipmentFaultSink {
            private readonly List<EquipmentFaultEvent> _published = new();
            private readonly List<EquipmentFaultEvent> _recovered = new();
            public void Publish(EquipmentFaultEvent fault) { lock (_published) { _published.Add(fault); } }
            void IEquipmentFaultSink.Recovered(EquipmentFaultEvent fault) { lock (_published) { _recovered.Add(fault); } }
            public EquipmentFaultEvent[] Published() { lock (_published) { return [.. _published]; } }
            public EquipmentFaultEvent[] Recovered() { lock (_published) { return [.. _recovered]; } }
        }

        private static async Task<GuiderService> AutoReconnectingAsync(FakeGuider fake, IEquipmentFaultSink sink) {
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            var svc = new GuiderService(new HeadlessProfileService(), RecoveredImmediately(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(),
                ws: null, profileStore: PolicyStore(retrySec: 60).Object, sequencerResolver: () => Mock.Of<ISequencerService>(),
                notifications: Mock.Of<INotificationService>(), faultSink: sink) {
                ReconnectAttemptInterval = TimeSpan.FromMilliseconds(200),
                ReconnectGraceFromSeconds = _ => TimeSpan.FromSeconds(20),
            };
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(await WaitForStateAsync(svc, EquipmentConnectionState.Connected), Is.EqualTo(EquipmentConnectionState.Connected),
                "never reached Connected against the fake guider");
            Assert.That(await WaitUntilAsync(() => fake.ConnectionCount >= 1), Is.True, "event stream never settled");
            return svc;
        }

        [Test]
        public async Task The_auto_reconnect_reports_the_link_down_fault_recovered() {
            // The reconnect disposes the dropped guider before it dials: the open link-down fault must
            // outlive that dispose, or the next Connected recovers nothing and the guider episode never ends.
            await using var fake = FakeGuider.Start();
            var sink = new RecordingSink();
            using var svc = await AutoReconnectingAsync(fake, sink).ConfigureAwait(false);

            Assert.That(fake.DropConnections(), Is.GreaterThan(0), "expected a live connection to drop");
            Assert.That(await WaitUntilAsync(() => sink.Published().Length == 1), Is.True, "the link drop never reached the sink");
            Assert.That(await WaitForStateAsync(svc, EquipmentConnectionState.Connected, timeoutMs: 20000),
                Is.EqualTo(EquipmentConnectionState.Connected), "the auto-reconnect never re-established the session");

            Assert.That(await WaitUntilAsync(() => sink.Recovered().Length == 1), Is.True,
                "the guider is Connected again but the link-down fault was never reported Recovered");
            var published = sink.Published().Single();
            Assert.That(published.Details, Is.EqualTo("link_down"));
            Assert.That(sink.Recovered().Single(), Is.SameAs(published), "Recovered names the fault the drop published");
        }

        [Test]
        public async Task The_auto_reconnect_recovers_the_link_down_fault_and_only_guide_frames_recover_the_open_camera_fault() {
            // A guide camera drop, then a link drop: both faults are still open when the reconnect connects.
            // #2169 — the reconnect ends the link drop only: the camera may still be unplugged, so its fault
            // waits for real guide frames.
            await using var fake = FakeGuider.Start();
            var sink = new RecordingSink();
            using var svc = await AutoReconnectingAsync(fake, sink).ConfigureAwait(false);
            await fake.BroadcastAsync(PhdEvents.EquipmentDisconnected()).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => sink.Published().Length == 1), Is.True, "the camera drop never reached the sink");

            Assert.That(fake.DropConnections(), Is.GreaterThan(0), "expected a live connection to drop");
            Assert.That(await WaitUntilAsync(() => sink.Published().Length == 2), Is.True, "the link drop never reached the sink");
            Assert.That(await WaitForStateAsync(svc, EquipmentConnectionState.Connected, timeoutMs: 20000),
                Is.EqualTo(EquipmentConnectionState.Connected), "the auto-reconnect never re-established the session");

            Assert.That(await WaitUntilAsync(() => sink.Recovered().Length >= 1), Is.True, "the link-down fault was not reported Recovered");
            var published = sink.Published();
            Assert.That(published.Select(f => f.Details), Is.EqualTo(CameraThenLink));
            Assert.That(sink.Recovered(), Is.EqualTo(new[] { published[1] }), "the reconnect recovers the link drop, not the camera drop");

            await fake.BroadcastAsync(PhdEvents.GuideStep(0.1, 0.1)).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => sink.Recovered().Length == 2), Is.True, "the guide frames never recovered the camera drop");
            Assert.That(sink.Recovered()[1], Is.SameAs(published[0]));
        }
    }
}