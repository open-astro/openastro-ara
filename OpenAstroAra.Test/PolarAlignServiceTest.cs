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
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §45 (polar-align-b) — the real <see cref="PolarAlignService"/> skeleton driven against the bench's
    /// <see cref="FakeGuider"/> through the real <see cref="GuiderService"/>: Start acquires the guide-camera
    /// PA-session lease and goes active; Stop releases it and goes stopped; both publish the lifecycle WS
    /// event; and a not-connected Start fails the same way the guide ops do. No capture/solve loop yet.
    /// </summary>
    [TestFixture]
    [Category("bench")]
    public class PolarAlignServiceTest {

        private static readonly bool[] AcquireThenRelease = { true, false };

        private static GuiderRecoveryCoordinator NewRecovery() =>
            new(Mock.Of<IGuiderProcessSupervisor>(),
                Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(),
                NullLogger<GuiderRecoveryCoordinator>.Instance);

        // A FakeGuider that answers the connect handshake and records the active flag of every set_pa_session.
        private static FakeGuider StartFakeWithPaSession(ConcurrentQueue<bool> paSessionActiveCalls) {
            var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
            fake.OnRpc("set_pa_session", req => {
                var active = req["params"]?["active"]?.GetValue<bool>() ?? false;
                paSessionActiveCalls.Enqueue(active);
                return new JsonObject { ["active"] = active, ["expires_in_s"] = active ? 600 : null };
            });
            return fake;
        }

        [Test]
        public async Task Status_is_idle_before_start() {
            using var guider = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            using var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance);

            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("idle"));
            Assert.That(status.FramesCaptured, Is.EqualTo(0));
            Assert.That(status.LastFrameId, Is.Null);
            Assert.That(status.CurrentErrorArcmin, Is.Null);
        }

        [Test]
        public async Task Start_acquires_the_lease_and_reports_active() {
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFakeWithPaSession(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            using var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance, ws.Object);

            await svc.StartAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(paCalls.TryDequeue(out var active), Is.True, "Start should send set_pa_session");
            Assert.That(active, Is.True, "Start acquires the lease (active=true)");
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("capturing"), "an active routine reports the capturing state");
            ws.Verify(w => w.PublishAsync(WsEventCatalog.PolarAlignStarted, It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Test]
        public async Task Stop_releases_the_lease_and_reports_stopped() {
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFakeWithPaSession(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            using var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance, ws.Object);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            paCalls.TryDequeue(out _); // discard the Start (active=true) call

            await svc.StopAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(paCalls.TryDequeue(out var active), Is.True, "Stop should send set_pa_session");
            Assert.That(active, Is.False, "Stop releases the lease (active=false)");
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("stopped"));
            ws.Verify(w => w.PublishAsync(WsEventCatalog.PolarAlignStopped, It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Test]
        public async Task Start_is_idempotent_and_acquires_the_lease_only_once() {
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFakeWithPaSession(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            using var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(paCalls.Count, Is.EqualTo(1), "a second Start on an already-active routine is a no-op accept");
        }

        [Test]
        public async Task Concurrent_starts_acquire_the_lease_and_publish_only_once() {
            // The endpoint calls straight into the singleton with no request serialization, so two
            // near-simultaneous Starts race. _opLock serializes them: exactly one acquires the lease +
            // publishes, the other then sees _active and is a no-op accept (guards the double-acquire).
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFakeWithPaSession(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            using var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance, ws.Object);

            await Task.WhenAll(
                svc.StartAsync(null, CancellationToken.None),
                svc.StartAsync(null, CancellationToken.None)).ConfigureAwait(false);

            var acquisitions = 0;
            while (paCalls.TryDequeue(out var active)) {
                if (active) {
                    acquisitions++;
                }
            }
            Assert.That(acquisitions, Is.EqualTo(1), "concurrent Starts must acquire the lease exactly once");
            ws.Verify(w => w.PublishAsync(WsEventCatalog.PolarAlignStarted, It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Test]
        public async Task Stop_waits_for_an_in_flight_start_and_leaves_the_lease_released() {
            // The Start/Stop race: a Stop issued while a Start is mid-lease-RPC must serialize behind it
            // (via _opLock), so the set_pa_session calls can't reorder on the wire and leave the daemon
            // holding the lease while the service reports "stopped". We block Start inside its active:true
            // RPC, fire Stop, and prove Stop waits — then the wire order is [true, false] and state=stopped.
            var paCalls = new ConcurrentQueue<bool>();
            using var startEnteredRpc = new ManualResetEventSlim(false);
            using var releaseStartRpc = new ManualResetEventSlim(false);
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
            fake.OnRpc("set_pa_session", req => {
                var active = req["params"]?["active"]?.GetValue<bool>() ?? false;
                if (active) {
                    startEnteredRpc.Set();
                    releaseStartRpc.Wait(TimeSpan.FromSeconds(10));
                }
                paCalls.Enqueue(active);
                return new JsonObject { ["active"] = active };
            });
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            using var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance);

            var startTask = svc.StartAsync(null, CancellationToken.None);
            Assert.That(startEnteredRpc.Wait(TimeSpan.FromSeconds(10)), Is.True, "Start should reach its lease RPC");

            // Start now holds _opLock inside the (blocked) lease RPC; a Stop must wait on the semaphore.
            var stopTask = svc.StopAsync(null, CancellationToken.None);
            await Task.Delay(200).ConfigureAwait(false);
            Assert.That(stopTask.IsCompleted, Is.False, "Stop must wait for the in-flight Start (serialized by _opLock)");

            releaseStartRpc.Set();
            await Task.WhenAll(startTask, stopTask).ConfigureAwait(false);

            Assert.That(paCalls.ToArray(), Is.EqualTo(AcquireThenRelease),
                "the lease RPCs must run in call order — acquire then release — not race on the wire");
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("stopped"), "final state matches the last op, with the lease released");
        }

        [Test]
        public void Start_without_a_connected_guider_throws() {
            using var guider = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            using var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance);

            Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(null, CancellationToken.None));
        }

        [Test]
        public async Task Stop_without_a_connected_guider_still_succeeds_and_reports_stopped() {
            // Stop is best-effort about the lease — with no guider there's nothing to release, but the
            // service must still transition and ack rather than throw.
            using var guider = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            using var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance);

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("stopped"));
        }

        private static async Task<GuiderService> ConnectGuiderAsync(FakeGuider fake) {
            var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            var connected = await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "the guider never reached Connected against the fake");
            return svc;
        }

        private static async Task<GuiderDto?> PollAsync(GuiderService svc, Func<GuiderDto, bool> predicate) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try {
                while (!cts.IsCancellationRequested) {
                    var dto = await svc.GetAsync(cts.Token).ConfigureAwait(false);
                    if (dto is not null && predicate(dto)) {
                        return dto;
                    }
                    await Task.Delay(100, cts.Token).ConfigureAwait(false);
                }
            } catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token) {
                // Our own 15s deadline elapsed — let the caller's assertion report the miss.
            }
            return null;
        }
    }
}
