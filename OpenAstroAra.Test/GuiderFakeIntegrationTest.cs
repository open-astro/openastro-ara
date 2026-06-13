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
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// bench-3: the first end-to-end virtual-observatory scenario — the REAL §63
    /// <see cref="GuiderService"/> / <c>PHD2Guider</c> driven against the bench's
    /// <see cref="FakeGuider"/> over the loopback PHD2 wire protocol, no C++ daemon.
    ///
    /// Proves the connect path the §63 deployment exercises: the daemon connects to an
    /// already-running guider on localhost (no GUI process launch — the bench surfaced
    /// that the inherited NINA-desktop <c>StartPHD2Process</c> blocked this, now retired),
    /// opens the event-stream connection, reaches <c>Connected</c>, and reflects the live
    /// event stream (guiding state, GuideStep RMS) into its status — all the guider-path
    /// fixes the bench surfaced (#403 lifecycle, #404 read-driven listener, #405 SendMessage
    /// timeout) are exercised end-to-end here.
    /// </summary>
    [TestFixture]
    public class GuiderFakeIntegrationTest {

        private static GuiderRecoveryCoordinator NewRecovery() =>
            new(Mock.Of<IGuiderProcessSupervisor>(),
                Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(),
                NullLogger<GuiderRecoveryCoordinator>.Instance);

        [Test]
        public async Task The_real_client_connects_to_the_fake_and_speaks_the_phd2_handshake() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            // Connect to the already-listening fake on localhost — no process launch. The
            // background connect opens the event-stream connection and runs the §63.4/.5 RPC
            // handshake (get_profile(s), set_*, get_pixel_scale, …) against the fake.
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);

            // The real client drove the PHD2 JSON-RPC handshake through the fake.
            var spoke = await WaitUntilAsync(() => fake.ReceivedMethods.Count > 0).ConfigureAwait(false);
            Assert.That(spoke, Is.True, "the real guider client never opened/queried the fake guider");
            Assert.That(fake.ReceivedMethods, Does.Contain("get_profile").IgnoreCase
                .Or.Contain("get_profiles").IgnoreCase,
                "the connect handshake should query the guider's profiles");
            // svc is `using`-scoped — its Dispose tears the connection down; no explicit disconnect needed.
        }

        [Test]
        public async Task Reaches_Connected_and_reflects_live_guiding_events() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);

            var connected = await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "the service never reached Connected against the fake guider");

            // Guiding state propagates from a live AppState event over the listener connection.
            // Runtime is null-guarded: it can briefly lag the Connected transition before the
            // first status snapshot populates it, and the predicate should poll-on rather than throw.
            await fake.BroadcastAsync(PhdEvents.AppState("Guiding")).ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.Runtime?.State == "guiding").ConfigureAwait(false), Is.Not.Null,
                "an AppState=Guiding event did not reach the runtime state");

            // RMS accumulates from GuideStep events.
            for (var i = 0; i < 5; i++) {
                await fake.BroadcastAsync(PhdEvents.GuideStep(raDistanceRaw: 0.4, decDistanceRaw: -0.4)).ConfigureAwait(false);
            }
            Assert.That(await PollAsync(svc, d => d.Runtime?.RmsTotal is > 0).ConfigureAwait(false), Is.Not.Null,
                "GuideStep events did not accumulate into a non-zero RMS");

            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            // Disconnect drops the guider session — GetAsync goes back to null (no device), the same
            // contract as before connect (see GuiderServiceTest.GetAsync_returns_null_before_connect).
            var after = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(after, Is.Null, "disconnect should drop the guider so GetAsync returns null");
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
            } catch (OperationCanceledException) {
                // 15s deadline elapsed — fall through and let the caller's assertion report the miss.
            }
            return null;
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!cts.IsCancellationRequested) {
                if (condition()) {
                    return true;
                }
                try {
                    await Task.Delay(100, cts.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    break;
                }
            }
            return condition();
        }
    }
}
