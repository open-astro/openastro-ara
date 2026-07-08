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
            var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance);

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
            var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance, ws.Object);

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
            var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance, ws.Object);

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
            var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(paCalls.Count, Is.EqualTo(1), "a second Start on an already-active routine is a no-op accept");
        }

        [Test]
        public async Task Concurrent_starts_acquire_the_lease_and_publish_only_once() {
            // The endpoint calls straight into the singleton with no request serialization, so two
            // near-simultaneous Starts race. The reservation-under-lock must let exactly one acquire the
            // lease + publish; the other is a no-op accept (guards against the TOCTOU double-acquire).
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFakeWithPaSession(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance, ws.Object);

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
        public void Start_without_a_connected_guider_throws() {
            using var guider = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance);

            Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(null, CancellationToken.None));
        }

        [Test]
        public async Task Stop_without_a_connected_guider_still_succeeds_and_reports_stopped() {
            // Stop is best-effort about the lease — with no guider there's nothing to release, but the
            // service must still transition and ack rather than throw.
            using var guider = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance);

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
