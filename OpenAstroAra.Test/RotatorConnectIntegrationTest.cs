#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §14e — live integration test for <see cref="RotatorService"/>. Discovers a Rotator from a
    /// running ASCOM OmniSim, connects, reads its runtime, rotates to a target mechanical angle via
    /// <see cref="RotatorService.MoveAsync"/>, and verifies the cached angle reflects the move, then
    /// disconnects. Runs in the <c>alpaca-sim-integration</c> CI job.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class RotatorConnectIntegrationTest {

        private static readonly Uri ManagementProbeUri = new("http://127.0.0.1:32323/management/apiversions");
        private const int MaxDiscoveryAttempts = 6;

        [OneTimeSetUp]
        public async Task OneTimeSetUp() {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try {
                using var resp = await http.GetAsync(ManagementProbeUri).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) {
                    Assert.Ignore($"OmniSim management API returned {(int)resp.StatusCode} on :32323 — skipping live Rotator test.");
                }
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                Assert.Ignore("No ASCOM OmniSim answering on :32323 — start one (or run the alpaca-sim-integration CI job) to exercise this test.");
            }
        }

        [Test]
        public async Task Connect_rotates_to_angle_then_disconnects() {
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no Rotator device discovered from the running OmniSim");

            using var svc = new RotatorService();

            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, s => s != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "connection never left the Connecting state");
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));

            // Move to a mechanical angle (UseSkyAngle defaults false) and confirm the read-back.
            const double target = 90.0;
            await svc.MoveAsync(new RotatorMoveRequestDto(target), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            var moved = await PollUntilMechanicalAngleAsync(svc, target).ConfigureAwait(false);
            Assert.That(moved, Is.Not.Null, "rotator never reported the target mechanical angle");
            Assert.That(moved!.Runtime.MechanicalAngleDeg, Is.EqualTo(target).Within(0.5),
                "the cached mechanical angle should reflect the move");

            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var disconnected = await PollUntilAsync(svc, s => s == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        /// <summary>
        /// §14e — the same <see cref="RotatorService"/> also serves <see cref="IRotatorMediator"/>:
        /// the <c>MoveRotatorMechanical</c> instruction calls <c>GetInfo().Connected</c> (Validate)
        /// and <c>MoveMechanical(angle)</c> (Execute), which blocks until the rotator settles and
        /// returns the final mechanical angle. This exercises that live mediator path.
        /// </summary>
        [Test]
        public async Task Mediator_MoveMechanical_drives_the_live_device_and_returns_the_settled_angle() {
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no Rotator device discovered from the running OmniSim");

            using var svc = new RotatorService();
            // RotatorService implements IRotatorMediator; GetInfo()/MoveMechanical below are the mediator
            // surface MoveRotatorMechanical drives (called on the concrete type to satisfy CA1859 —
            // interface conformance is covered by the unit test).

            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, s => s != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));
            Assert.That(svc.GetInfo().Connected, Is.True, "the mediator should report connected once the REST connect lands");

            const float target = 120.0f;
            // Blocking move via the mediator — returns once the device settles at the target.
            var settled = await svc.MoveMechanical(target, CancellationToken.None).ConfigureAwait(false);
            Assert.That(settled, Is.EqualTo(target).Within(0.5f), "MoveMechanical should return the settled mechanical angle");
            Assert.That(svc.GetInfo().MechanicalPosition, Is.EqualTo(target).Within(0.5f),
                "the mediator snapshot should reflect the move");

            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var disconnected = await PollUntilAsync(svc, s => s == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        private static async Task<DiscoveredDeviceDto?> DiscoverAsync() {
            var discovery = new AlpacaEquipmentDiscoveryService();
            for (var attempt = 1; attempt <= MaxDiscoveryAttempts; attempt++) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var found = await discovery.DiscoverAsync(DeviceType.Rotator, forceRefresh: true, cts.Token).ConfigureAwait(false);
                if (found.Count > 0) {
                    return found[0];
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            return null;
        }

        private static async Task<RotatorDto?> PollUntilAsync(RotatorService svc, Func<EquipmentConnectionState, bool> predicate) {
            for (var i = 0; i < 50; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto is not null && predicate(dto.State)) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // Up to ~16s for the rotation to complete + the 2s cache to reflect it. Null on timeout.
        private static async Task<RotatorDto?> PollUntilMechanicalAngleAsync(RotatorService svc, double target) {
            for (var i = 0; i < 80; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                var angle = dto?.Runtime.MechanicalAngleDeg;
                if (angle is not null && Math.Abs(angle.Value - target) < 0.5) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return null;
        }
    }
}
