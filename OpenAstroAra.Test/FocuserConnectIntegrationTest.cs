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
    /// §14e — live integration test for <see cref="FocuserService"/>. Discovers a Focuser from a
    /// running ASCOM OmniSim, connects, reads its capabilities, moves it to a target position via
    /// <see cref="FocuserService.MoveAsync"/>, and verifies the cached runtime reflects the new
    /// position, then disconnects. Runs in the <c>alpaca-sim-integration</c> CI job. #1134 — on real
    /// gear only the read-only case runs unless <c>ARA_ALPACA_ALLOW_MOTION=1</c>; the move cases then
    /// step 50 from the current position, allow a few steps of settle and move back.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class FocuserConnectIntegrationTest : OmniSimIntegrationFixture {

        protected override string DeviceName => "Focuser";

        // Real gear: a small step that keeps the rig near focus, and the step slop a motor may settle with.
        private const int RealGearStep = 50;
        private int Tolerance => OnRealGear ? 3 : 0;

        /// <summary>#1134 — the case that runs on every focuser: connect, read the capabilities and the
        /// position, disconnect. Nothing is moved.</summary>
        [Test]
        public async Task Connect_reads_caps_and_position_without_moving_then_disconnects() {
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no Focuser found (OmniSim discovery or ARA_ALPACA_URL)");

            using var svc = new FocuserService();
            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, s => s != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));
            try {
                var withCaps = await PollUntilCapsAsync(svc).ConfigureAwait(false);
                Assert.That(withCaps, Is.Not.Null, "capabilities were never seeded after connect");
                Assert.That(withCaps!.Capabilities!.MaxPosition, Is.GreaterThan(0), "an absolute focuser exposes a max position");
                Assert.That(withCaps.Runtime.Position, Is.Not.Null, "the position is read from the focuser");
                Assert.That(withCaps.Runtime.Position!.Value, Is.InRange(0, withCaps.Capabilities.MaxPosition));
                Assert.That(svc.GetInfo().Connected, Is.True, "the mediator should report connected");
            } finally {
                await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            }
            var disconnected = await PollUntilAsync(svc, s => s == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        [Test]
        public async Task Connect_reads_caps_moves_then_disconnects() {
            RequireMotionOptIn("moves the focuser");
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no Focuser device discovered from the running OmniSim");

            using var svc = new FocuserService();

            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, s => s != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "connection never left the Connecting state");
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));

            // Capabilities are seeded by RefreshCacheOnce after the lock is released, so poll for
            // them rather than asserting the instant State flips to Connected (avoids a race).
            var withCaps = await PollUntilCapsAsync(svc).ConfigureAwait(false);
            Assert.That(withCaps, Is.Not.Null, "capabilities were never seeded after connect");
            Assert.That(withCaps!.Capabilities!.MaxPosition, Is.GreaterThan(0), "an absolute focuser exposes a max position");

            // Move to a target away from the current position and confirm the read-back.
            var max = withCaps.Capabilities.MaxPosition;
            var current = withCaps.Runtime.Position ?? 0;
            var target = TargetFrom(current, max);

            await svc.MoveAsync(new FocuserMoveRequestDto(target), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            var moved = await PollUntilPositionAsync(svc, target, Tolerance).ConfigureAwait(false);
            Assert.That(moved, Is.Not.Null, "focuser never reported the target position");
            Assert.That(moved!.Runtime.Position, Is.EqualTo(target).Within(Tolerance), "the cached position should reflect the move");
            if (OnRealGear) {
                // Leave the rig where it was focused.
                await svc.MoveAsync(new FocuserMoveRequestDto(current), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
                await PollUntilPositionAsync(svc, current, Tolerance).ConfigureAwait(false);
            }

            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var disconnected = await PollUntilAsync(svc, s => s == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        /// <summary>
        /// §14e — the same <see cref="FocuserService"/> also serves <see cref="IFocuserMediator"/>:
        /// the Sequencer's <c>MoveFocuserAbsolute</c> instruction calls <c>GetInfo().Connected</c>
        /// (Validate) and <c>MoveFocuser(position)</c> (Execute), which blocks until the focuser
        /// settles and returns the final position. This exercises that live mediator path.
        /// </summary>
        [Test]
        public async Task Mediator_MoveFocuser_drives_the_live_device_and_returns_the_settled_position() {
            RequireMotionOptIn("moves the focuser");
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no Focuser device discovered from the running OmniSim");

            using var svc = new FocuserService();
            // FocuserService implements IFocuserMediator; the GetInfo()/MoveFocuser members below are
            // exactly the mediator surface the MoveFocuserAbsolute instruction drives (called directly
            // on the concrete type to satisfy CA1859 — interface conformance is covered by the unit test).

            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, s => s != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));

            var withCaps = await PollUntilCapsAsync(svc).ConfigureAwait(false);
            Assert.That(withCaps, Is.Not.Null, "capabilities were never seeded after connect");
            Assert.That(svc.GetInfo().Connected, Is.True, "the mediator should report connected once the REST connect lands");

            var max = withCaps!.Capabilities!.MaxPosition;
            var current = svc.GetInfo().Position;
            var target = TargetFrom(current, max);

            // Blocking move via the mediator — returns once the device settles at the target.
            // Exact equality is safe against the OmniSim (no step-size quantisation, so it lands
            // exactly on the requested integer position); a physical focuser that rounds to the
            // nearest motor step could settle a step or two off, so real gear (#1134) gets a small tolerance.
            var settled = await svc.MoveFocuser(target, CancellationToken.None).ConfigureAwait(false);
            Assert.That(settled, Is.EqualTo(target).Within(Tolerance), "MoveFocuser should return the settled position");
            Assert.That(svc.GetInfo().Position, Is.EqualTo(target).Within(Tolerance), "the mediator snapshot should reflect the move");
            if (OnRealGear) {
                await svc.MoveFocuser(current, CancellationToken.None).ConfigureAwait(false);
            }

            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var disconnected = await PollUntilAsync(svc, s => s == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        // OmniSim: either side of mid-travel. Real gear: RealGearStep from where the focuser is, inside its travel.
        private int TargetFrom(int current, int max) {
            if (OnRealGear) {
                return current + RealGearStep <= max ? current + RealGearStep : Math.Max(0, current - RealGearStep);
            }
            return current < max / 2 ? Math.Min(max, max / 2 + 100) : Math.Max(0, max / 2 - 100);
        }

        private static async Task<FocuserDto?> PollUntilAsync(FocuserService svc, Func<EquipmentConnectionState, bool> predicate) {
            for (var i = 0; i < 50; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto is not null && predicate(dto.State)) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // Polls until capabilities are seeded (RefreshCacheOnce runs just after connect). Returns
        // null if they never appear.
        private static async Task<FocuserDto?> PollUntilCapsAsync(FocuserService svc) {
            for (var i = 0; i < 50; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto?.Capabilities is not null) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return null;
        }

        // Up to ~16s for the move to complete + the 2s cache to reflect it. Returns null on
        // timeout so the caller's Is.Not.Null assertion fails fast with the right message rather
        // than a confusing position-equality mismatch.
        private static async Task<FocuserDto?> PollUntilPositionAsync(FocuserService svc, int target, int tolerance) {
            for (var i = 0; i < 80; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto?.Runtime.Position is int position && Math.Abs(position - target) <= tolerance) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return null;
        }
    }
}
