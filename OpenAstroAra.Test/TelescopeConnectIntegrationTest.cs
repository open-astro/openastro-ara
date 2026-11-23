#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §14e — live integration test for <see cref="TelescopeService"/>. Discovers a Telescope from a
    /// running ASCOM OmniSim, connects, unparks + enables tracking, slews to a coordinate, verifies
    /// the cached runtime reflects the slew, then aborts/disconnects. Runs in the
    /// <c>alpaca-sim-integration</c> CI job. #1134 — on real gear (<c>ARA_ALPACA_URL</c>) only the
    /// read-only case runs unless <c>ARA_ALPACA_ALLOW_MOTION=1</c>; the slew cases then aim near the
    /// current pointing and never push the test's site into the mount.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class TelescopeConnectIntegrationTest : OmniSimIntegrationFixture {

        protected override string DeviceName => "Telescope";

        /// <summary>#1134 — the case that runs on every mount: connect, read the pointing and the
        /// capabilities through REST and the mediator, disconnect. Nothing is written to the mount.</summary>
        [Test]
        public async Task Connect_reads_pointing_and_capabilities_without_moving_then_disconnects() {
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no Telescope found (OmniSim discovery or ARA_ALPACA_URL)");

            using var svc = new TelescopeService();
            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, d => d.State != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));
            try {
                var read = await PollUntilAsync(svc, d => d.Capabilities is not null
                    && d.Runtime.RightAscensionHours is not null && d.Runtime.DeclinationDegrees is not null).ConfigureAwait(false);
                Assert.That(read!.Capabilities, Is.Not.Null, "capabilities should be populated once connected");
                Assert.That(read.Runtime.RightAscensionHours, Is.Not.Null, "RA is read from the mount");
                Assert.That(read.Runtime.DeclinationDegrees, Is.Not.Null, "Dec is read from the mount");
                Assert.That(read.Runtime.RightAscensionHours!.Value, Is.InRange(0.0, 24.0));
                Assert.That(read.Runtime.DeclinationDegrees!.Value, Is.InRange(-90.0, 90.0));
                var info = ((ITelescopeMediator)svc).GetInfo();
                Assert.That(info.Connected, Is.True, "the mediator should report connected");
            } finally {
                await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            }
            var disconnected = await PollUntilAsync(svc, d => d.State == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        [Test]
        public async Task Connect_unparks_tracks_and_slews_then_disconnects() {
            RequireMotionOptIn("unparks, tracks and slews the mount");
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no Telescope discovered from the running OmniSim");

            using var svc = new TelescopeService();
            // The profile's site goes into the mount on connect (PushSite's own tests cover the writes).
            // #1134 — never on real gear: the test's Auckland site would overwrite the rig's own.
            var sitePushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!OnRealGear) {
                var store = new Mock<IProfileStore>();
                store.Setup(s => s.GetSiteSettings()).Returns(new SiteSettingsDto(
                    "Home", -36.85, 174.76, 120, "Pacific/Auckland", false, 0, 4, 2, "astronomical"));
                svc.FollowProfileSite(store.Object);
                svc.RunSitePush = push => { sitePushed.TrySetResult(); return Task.Run(push); };
            }

            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, d => d.State != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "connection never left the Connecting state");
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));
            if (!OnRealGear) {
                Assert.That(await Task.WhenAny(sitePushed.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false),
                    Is.SameAs(sitePushed.Task), "connecting should push the profile's site into the mount");
            }
            Assert.That(connected.Capabilities, Is.Not.Null, "capabilities should be populated once connected");

            // Drive the mount inside a try/finally so the simulator is always disconnected even if an
            // assertion fires mid-test: DisconnectAsync runs the full SafeDisconnectDispose teardown
            // (AbortSlew + Connected=false), unlike `using`/Dispose which takes the fast non-blocking
            // path (DisposeQuietly only). That leaves the sim mount halted + disconnected for retries.
            try {
                // Make sure the mount can move: unpark (idempotent if already unparked) and track.
                await svc.UnparkAsync(null, CancellationToken.None).ConfigureAwait(false);
                await PollUntilAsync(svc, d => !d.Runtime.Parked).ConfigureAwait(false);
                await svc.SetTrackingAsync(true, CancellationToken.None).ConfigureAwait(false);
                var tracking = await PollUntilAsync(svc, d => d.Runtime.Tracking).ConfigureAwait(false);
                Assert.That(tracking!.Runtime.Tracking, Is.True, "tracking should report on after SetTracking(true)");

                // Slew to a coordinate near the current pointing so the OmniSim settles quickly, then
                // confirm the read-back lands near the target.
                var startRa = tracking.Runtime.RightAscensionHours ?? 6.0;
                var targetRa = NormalizeRaHours(startRa + RaOffsetHours);
                var targetDec = TargetDec(tracking.Runtime.DeclinationDegrees);
                await svc.SlewAsync(new SlewRequestDto(targetRa, targetDec), null, CancellationToken.None).ConfigureAwait(false);
                var slewed = await PollUntilAsync(svc,
                    d => d.Runtime.DeclinationDegrees is double dec && Math.Abs(dec - targetDec) < 1.0
                      && d.Runtime.State != "slewing").ConfigureAwait(false);
                Assert.That(slewed!.Runtime.DeclinationDegrees, Is.Not.Null);
                Assert.That(slewed.Runtime.DeclinationDegrees!.Value, Is.EqualTo(targetDec).Within(1.0),
                    "the mount should report the target declination after the slew");
            } finally {
                await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            }
            var disconnected = await PollUntilAsync(svc, d => d.State == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        /// <summary>
        /// §14e — the same <see cref="TelescopeService"/> also serves <see cref="ITelescopeMediator"/>:
        /// the telescope instructions read <c>GetInfo()</c> (Validate) and call the mount ops
        /// (Execute). This exercises the live mediator Unpark + SetTrackingMode + SlewToCoordinatesAsync
        /// path — the ops the §38k telescope instructions invoke.
        /// </summary>
        [Test]
        public async Task Mediator_Unpark_SetTrackingMode_and_Slew_drive_the_live_device() {
            RequireMotionOptIn("unparks, tracks and slews the mount");
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no Telescope discovered from the running OmniSim");

            using var svc = new TelescopeService();
            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, d => d.State != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));

            try {
                // Wait for the first refresh so GetInfo serves capabilities + a real position.
                await PollUntilAsync(svc, d => d.Capabilities is not null && d.Runtime.RightAscensionHours is not null).ConfigureAwait(false);

                // The ops below are the ITelescopeMediator surface the instructions call; the casts
                // are inline (CA1859) and bounded — each blocks until its terminal condition.
                var unparked = await ((ITelescopeMediator)svc).UnparkTelescope(progress: null!, CancellationToken.None).ConfigureAwait(false);
                Assert.That(unparked, Is.True, "UnparkTelescope should confirm the mount left the parked state");

                Assert.That(((ITelescopeMediator)svc).SetTrackingMode(TrackingMode.Sidereal), Is.True,
                    "SetTrackingMode(Sidereal) should succeed against the OmniSim mount");

                var info = ((ITelescopeMediator)svc).GetInfo();
                Assert.That(info.Connected, Is.True);
                Assert.That(info.AtPark, Is.False);
                Assert.That(info.TrackingModes, Does.Contain(TrackingMode.Sidereal),
                    "GetInfo should surface the sim's supported tracking modes for SetTracking.Validate");

                // Slew near the current pointing so the sim settles quickly; the mediator op returns
                // true only once Slewing clears and the pointing lands inside the settle window.
                var current = ((ITelescopeMediator)svc).GetCurrentPosition();
                var targetDec = TargetDec(current.Dec);
                var target = new Coordinates(
                    Angle.ByHours(NormalizeRaHours(current.RA + RaOffsetHours)), Angle.ByDegree(targetDec), current.Epoch);
                var slewed = await ((ITelescopeMediator)svc).SlewToCoordinatesAsync(target, CancellationToken.None).ConfigureAwait(false);
                Assert.That(slewed, Is.True, "mediator slew should confirm arrival at the target");

                var settled = ((ITelescopeMediator)svc).GetCurrentPosition();
                Assert.That(settled.Dec, Is.EqualTo(targetDec).Within(5.0),
                    "post-slew GetCurrentPosition should reflect the slewed declination");
            } finally {
                await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            }
            var disconnected = await PollUntilAsync(svc, d => d.State == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        // OmniSim: 0.2 h east in RA. Real gear (#1134): no RA step, so the slew is the 2° Dec step alone.
        private double RaOffsetHours => OnRealGear ? 0.0 : 0.2;

        // OmniSim: a fixed +45°. Real gear (#1134): 2° in Dec from where the mount points, a short slew
        // next to the current pointing (not a horizon check: the mount's own limits still apply).
        private double TargetDec(double? currentDec) {
            if (!OnRealGear) {
                return 45.0;
            }
            Assert.That(currentDec, Is.Not.Null, "no declination read from the mount to slew near");
            var dec = currentDec!.Value;
            return dec > 85.0 ? dec - 2.0 : dec + 2.0;
        }

        private static double NormalizeRaHours(double ra) {
            ra %= 24.0;
            return ra < 0 ? ra + 24.0 : ra;
        }

        // Polls up to ~24s (slew + the 2s cache) until the predicate holds; null on timeout.
        private static async Task<TelescopeDto?> PollUntilAsync(TelescopeService svc, Func<TelescopeDto, bool> predicate) {
            for (var i = 0; i < 120; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto is not null && predicate(dto)) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
