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
    [Category("bench")] // §42.2 virtual-observatory bench — selected by bench/ (TestCategory=bench)
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
            // The fake sends the raw PHD2 token "Guiding"; GuiderService.MapGuidingState normalizes
            // it to the lowercase §63.2 DTO token "guiding", which is what we assert here.
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

        [Test]
        public async Task Reflects_star_lost_when_the_guide_star_is_lost() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null,
                "the service never reached Connected against the fake guider");

            await fake.BroadcastAsync(PhdEvents.AppState("Guiding")).ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.Runtime?.State == "guiding").ConfigureAwait(false), Is.Not.Null,
                "an AppState=Guiding event did not reach the runtime state");

            // §42.2 in-band fault: the guider loses the star mid-guiding. PHD2 emits a StarLost
            // event, which PHD2Guider folds into AppState=LostLock → the §63.2 "star_lost" token.
            // The session stays Connected (it's a guiding-quality fault, not a link drop).
            await fake.BroadcastAsync(PhdEvents.StarLost()).ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.Runtime?.State == "star_lost").ConfigureAwait(false), Is.Not.Null,
                "a StarLost event did not surface as the star_lost runtime state");
            var afterLost = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(afterLost?.State, Is.EqualTo(EquipmentConnectionState.Connected),
                "a lost star is a guiding fault, not a disconnect — the link should stay Connected");
        }

        [Test]
        public async Task Drops_to_Error_when_the_guider_link_dies_mid_session() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null,
                "the service never reached Connected against the fake guider");
            // Connect leaves exactly the persistent event-stream connection open (per-call RPC
            // connections close after their reply); wait for it to settle before dropping. Assert
            // the wait so a slow CI box fails here ("nothing to drop") rather than later at the
            // drop-count assert with a misleading message.
            Assert.That(await WaitUntilAsync(() => fake.ConnectionCount >= 1).ConfigureAwait(false), Is.True,
                "the persistent event-stream connection never settled, so there was nothing to drop");

            // §42.2 link fault: the guider daemon drops the socket mid-session. PHD2Guider's
            // listener sees EOF and raises PHD2ConnectionLost; GuiderService.OnConnectionLost moves
            // the session to Error and kicks off §63.3 recovery (its outcome — Unsupervised off a
            // systemd host — is unit-covered by GuiderRecoveryCoordinatorTest, not re-asserted here).
            Assert.That(fake.DropConnections(), Is.GreaterThan(0), "expected at least one live connection to drop");
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Error).ConfigureAwait(false), Is.Not.Null,
                "a dropped guider link did not surface as the Error state");
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
                // Our own 15s deadline elapsed — fall through and let the caller's assertion report
                // the miss. A cancellation from a *different* token (e.g. a service-internal
                // disconnect race) is not swallowed here; it surfaces as the real failure cause.
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
