#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using ASCOM.Common.DeviceInterfaces;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2073 — the daemon now turns tracking on before a REST GoTo, which removes the mount's
    /// "tracking is off" refusal that alone stopped two counterweight-up GoTos on 2026-11-19
    /// (HA ≈ −11.1 h on the HAE29C). So a GoTo below the pole within 2 h of the lower meridian
    /// (counterweight angle over #2048's 60° default) is refused before it reaches the mount:
    /// <c>counterweight-limit</c> (409, ADR 0022). The test site is at latitude 40° and longitude 0, and
    /// each target's RA is put at the wanted hour angle for the moment of the call.
    /// </summary>
    [TestFixture]
    public class GotoCounterweightRefusalTest {


        private static Mock<IProfileStore> Site() {
            var store = new Mock<IProfileStore>();
            store.Setup(p => p.GetSiteSettings()).Returns(new SiteSettingsDto("site", 40.0, 0, 0, "UTC", false, 0, 4, 2.0, "astronomical"));
            store.Setup(p => p.GetCustomHorizon()).Returns(new CustomHorizonDto(Array.Empty<CustomHorizonPointDto>()));
            return store;
        }

        /// <summary>The RA (hours, [0, 24)) that sits at <paramref name="hourAngleHours"/> now, at longitude 0.</summary>
        private static double RaAtHourAngle(double hourAngleHours, DateTimeOffset? at = null) {
            var lstHours = SiteAstrometry.LocalSiderealTimeDeg(at ?? DateTimeOffset.UtcNow, 0) / 15.0;
            return ((lstHours - hourAngleHours) % 24.0 + 24.0) % 24.0;
        }

        /// <summary>#2139 — the seam a person's GoTo is admitted at: a real Telescope service on the fake mount. The Slew
        /// below connects it with the case's profile store and runs; <see cref="Rig"/> is the last one.</summary>
        private sealed class Seam : IDisposable {
            public ASCOM.Common.DeviceInterfaces.AlignmentMode? Alignment { get; init; } = ASCOM.Common.DeviceInterfaces.AlignmentMode.GermanPolar;
            public FakeMountRig? Rig { get; set; }
            public void Dispose() => Rig?.Dispose();
        }

        private readonly List<Seam> _seams = new();

        [TearDown]
        public void DisposeSeams() {
            foreach (var seam in _seams) { seam.Dispose(); }
            _seams.Clear();
        }

        private (Seam Telescope, RigOccupancy Occupancy) Rig(AlignmentMode? alignment = AlignmentMode.GermanPolar) {
            // #2140 — a run active is a Session holding the mount in Rig Occupancy.
            var sequencer = new RigOccupancy();
            var seam = new Seam { Alignment = alignment };
            _seams.Add(seam);
            return (seam, sequencer);
        }

        private static async Task<Microsoft.AspNetCore.Http.IResult> Slew(SlewRequestDto request, Seam t, RigOccupancy s, IProfileStore? profiles) {
            t.Rig?.Dispose();
            t.Rig = await FakeMountRig.ConnectAsync(profiles, s, configure: f => f.Alignment = t.Alignment);
            return await EquipmentEndpoints.SlewAsync(request, null, t.Rig.Service, CancellationToken.None);
        }

        private static void NothingDispatched(Seam telescope) {
            Thread.Sleep(100);
            Assert.That(telescope.Rig!.Fake.Slews, Is.Zero, "a refused GoTo never reaches the mount");
        }

        [Test]
        public async Task A_goto_below_the_pole_near_the_lower_meridian_is_refused_with_the_counterweight_angle() {
            // The 2026-11-19 target: Dec +69° at HA ≈ −11.1 h (about 19° up at latitude 40°, so the horizon lets it by).
            var (telescope, sequencer) = Rig();
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => Slew(new SlewRequestDto(RaAtHourAngle(-11.1), 69.18), telescope, sequencer, Site().Object));
            Assert.Multiple(() => {
                Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.CounterweightLimit));
                Assert.That(refusal.Cause.Status, Is.EqualTo(409));
                Assert.That((double)refusal.Parameters["hour_angle_hours"]!, Is.EqualTo(-11.1).Within(0.01));
                Assert.That((double)refusal.Parameters["counterweight_angle_deg"]!, Is.EqualTo(76.5).Within(0.2));
                Assert.That(refusal.Parameters["limit_deg"], Is.EqualTo(60.0));
                Assert.That(refusal.Message, Does.Contain("-11.1").And.Contain("60"), "the detail names the hour angle and the limit");
            });
            NothingDispatched(telescope);
        }

        [TestCase(AlignmentMode.AltAz)]
        [TestCase(AlignmentMode.Polar)] // a fork: no counterweight to go up
        public async Task A_mount_without_a_counterweight_is_not_refused(AlignmentMode alignment) {
            // Polaris-like target near the lower meridian: an alt-az or fork mount must keep it.
            var (telescope, sequencer) = Rig(alignment);
            var accepted = await Slew(new SlewRequestDto(RaAtHourAngle(-11.1), 69.18), telescope, sequencer, Site().Object);
            Assert.That(accepted, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
        }

        [Test]
        public async Task A_mount_whose_type_is_unknown_is_checked() {
            var (telescope, sequencer) = Rig(alignment: null);
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => Slew(new SlewRequestDto(RaAtHourAngle(-11.1), 69.18), telescope, sequencer, Site().Object));
            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.CounterweightLimit), "unknown is treated as a German equatorial mount (fail-safe)");
            NothingDispatched(telescope);
        }

        [Test]
        public void Only_a_german_equatorial_or_unknown_mount_has_a_counterweight() {
            Assert.Multiple(() => {
                Assert.That(CounterweightGuard.HasCounterweight(AlignmentMode.GermanPolar), Is.True);
                Assert.That(CounterweightGuard.HasCounterweight(null), Is.True, "unknown is checked");
                Assert.That(CounterweightGuard.HasCounterweight(AlignmentMode.AltAz), Is.False);
                Assert.That(CounterweightGuard.HasCounterweight(AlignmentMode.Polar), Is.False);
            });
        }

        [Test]
        public async Task A_goto_near_the_upper_meridian_is_accepted() {
            // Counterweight near horizontal too, but this is the normal meridian-flip zone.
            var (telescope, sequencer) = Rig();
            var accepted = await Slew(new SlewRequestDto(RaAtHourAngle(0.5), 30), telescope, sequencer, Site().Object);
            Assert.That(accepted, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
        }

        [Test]
        public async Task A_goto_below_the_pole_inside_the_limit_is_accepted() {
            // HA −8 h: 30° from counterweight-down.
            var (telescope, sequencer) = Rig();
            var accepted = await Slew(new SlewRequestDto(RaAtHourAngle(-8.0), 69), telescope, sequencer, Site().Object);
            Assert.That(accepted, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
        }

        [Test]
        public async Task Force_does_not_bypass_the_counterweight_refusal() {
            var (telescope, sequencer) = Rig();
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => Slew(new SlewRequestDto(RaAtHourAngle(11.5), 69, Force: true), telescope, sequencer, Site().Object));
            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.CounterweightLimit));
            NothingDispatched(telescope);
        }

        [Test]
        public async Task A_sync_below_the_pole_is_accepted() {
            var (telescope, sequencer) = Rig();
            var accepted = await Slew(new SlewRequestDto(RaAtHourAngle(-11.1), 69, Sync: true), telescope, sequencer, Site().Object);
            Assert.That(accepted, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
        }

        [Test]
        public async Task A_missing_site_lets_the_counterweight_check_through() {
            var (telescope, sequencer) = Rig();
            Assert.That(await Slew(new SlewRequestDto(RaAtHourAngle(-11.1), 69), telescope, sequencer, null), Is.InstanceOf<Accepted<OperationAcceptedDto>>());
        }

        [Test]
        public void The_counterweight_angle_follows_the_shared_model() {
            Assert.Multiple(() => {
                Assert.That(CounterweightGuard.CounterweightAngleDeg(6.0), Is.EqualTo(0.0).Within(1e-9), "Home, west");
                Assert.That(CounterweightGuard.CounterweightAngleDeg(-6.0), Is.EqualTo(0.0).Within(1e-9), "Home, east");
                Assert.That(CounterweightGuard.CounterweightAngleDeg(0.0), Is.EqualTo(90.0).Within(1e-9), "upper meridian");
                Assert.That(CounterweightGuard.CounterweightAngleDeg(12.0), Is.EqualTo(90.0).Within(1e-9), "lower meridian");
                Assert.That(CounterweightGuard.CounterweightAngleDeg(-11.1), Is.EqualTo(76.5).Within(1e-9));
                Assert.That(CounterweightGuard.CounterweightAngleDeg(13.0), Is.EqualTo(75.0).Within(1e-9), "wrapped to −11 h");
            });
        }

        [Test]
        public void The_shared_check_refuses_only_below_the_pole_past_the_limit() {
            var at = new DateTimeOffset(2026, 11, 19, 5, 22, 0, TimeSpan.Zero);
            Coordinates At(double ha) => new(RaAtHourAngle(ha, at), 69, Epoch.J2000, Coordinates.RAType.Hours);
            var store = Site().Object;
            Assert.Multiple(() => {
                Assert.That(CounterweightGuard.TargetPastCounterweightLimit(store, At(-10.5), at)?.HourAngleHours, Is.EqualTo(-10.5).Within(1e-6));
                Assert.That(CounterweightGuard.TargetPastCounterweightLimit(store, At(10.5), at), Is.Not.Null, "west of the lower meridian too");
                Assert.That(CounterweightGuard.TargetPastCounterweightLimit(store, At(-9.9), at), Is.Null, "58.5° is inside the limit");
                Assert.That(CounterweightGuard.TargetPastCounterweightLimit(store, At(1.0), at), Is.Null, "above the pole");
                Assert.That(CounterweightGuard.TargetPastCounterweightLimit(null, At(-11.1), at), Is.Null, "no profile");
                Assert.That(CounterweightGuard.TargetPastCounterweightLimit(store, At(-11.1), at, AlignmentMode.AltAz), Is.Null, "alt-az");
                Assert.That(CounterweightGuard.TargetPastCounterweightLimit(store, At(-11.1), at, AlignmentMode.GermanPolar), Is.Not.Null, "German equatorial");
            });
        }
    }
}
