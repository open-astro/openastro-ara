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
    /// A GoTo to a target under the profile's horizon is refused before it reaches the mount (#2044):
    /// <c>below-horizon-limit</c> (409, ADR 0022) with the target's altitude and the limit. A sync never
    /// moves the mount, so it goes through; <c>force</c> only bypasses <c>run_in_progress</c>. The test
    /// site is a near-pole one, so a target's altitude is its declination and the floor decides.
    /// </summary>
    [TestFixture]
    public class GotoHorizonRefusalTest {

        private static readonly IReadOnlyList<Guid> None = Array.Empty<Guid>();

        private static Mock<IProfileStore> Site(double floorDeg, bool custom = false, params CustomHorizonPointDto[] skyline) {
            var store = new Mock<IProfileStore>();
            store.Setup(p => p.GetSiteSettings()).Returns(new SiteSettingsDto("pole", 89.9, 0, 0, "UTC", custom, floorDeg, 4, 2.0, "astronomical"));
            store.Setup(p => p.GetCustomHorizon()).Returns(new CustomHorizonDto(skyline));
            return store;
        }

        private static (Mock<ITelescopeService> Telescope, Mock<ISequencerService> Sequencer) Rig(IReadOnlyList<Guid>? runs = null) {
            var telescope = new Mock<ITelescopeService>();
            telescope.Setup(t => t.SlewAsync(It.IsAny<SlewRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationAcceptedDto(Guid.NewGuid(), "telescope.slew", DateTimeOffset.UtcNow, null));
            var sequencer = new Mock<ISequencerService>();
            sequencer.Setup(s => s.ActiveRunIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(runs ?? None);
            return (telescope, sequencer);
        }

        private static Task<Microsoft.AspNetCore.Http.IResult> Slew(SlewRequestDto request, Mock<ITelescopeService> t, Mock<ISequencerService> s, IProfileStore? profiles) =>
            EquipmentEndpoints.SlewAsync(request, null, t.Object, s.Object, profiles, CancellationToken.None);

        /// <summary>An RA on the meridian now (longitude 0): a target the #2073 counterweight check never
        /// refuses, so an accepted case does not depend on the time of day.</summary>
        private static double RaOnTheMeridian() => SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, 0) / 15.0 % 24.0;

        private static void NothingDispatched(Mock<ITelescopeService> telescope) =>
            telescope.Verify(t => t.SlewAsync(It.IsAny<SlewRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never, "a refused GoTo never reaches the mount");

        [Test]
        public async Task A_slew_to_a_target_under_the_floor_is_refused_with_its_altitude_and_the_limit() {
            var (telescope, sequencer) = Rig();
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => Slew(new SlewRequestDto(5.5, 14), telescope, sequencer, Site(floorDeg: 20).Object));
            Assert.Multiple(() => {
                Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.BelowHorizonLimit));
                Assert.That(refusal.Cause.Status, Is.EqualTo(409));
                Assert.That((double)refusal.Parameters["altitude_deg"]!, Is.EqualTo(14).Within(0.3));
                Assert.That(refusal.Parameters["limit_deg"], Is.EqualTo(20.0));
                // The site is 0.1° off the pole, so the altitude drifts 13.9–14.1° over the day: name the computed one.
                var altitude = ((double)refusal.Parameters["altitude_deg"]!).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                Assert.That(refusal.Message, Does.Contain(altitude).And.Contain("20"), "the detail names the altitude and the limit");
            });
            NothingDispatched(telescope);
        }

        [Test]
        public async Task A_slew_to_a_target_above_the_floor_is_accepted() {
            var (telescope, sequencer) = Rig();
            var accepted = await Slew(new SlewRequestDto(RaOnTheMeridian(), 40), telescope, sequencer, Site(floorDeg: 20).Object);
            Assert.That(accepted, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
        }

        [Test]
        public async Task The_custom_skyline_at_the_targets_azimuth_decides_when_enabled() {
            // A flat +30° skyline refuses a 25° target the −20° flat floor would let through.
            var skyline = new[] { new CustomHorizonPointDto(0, 30), new CustomHorizonPointDto(120, 30), new CustomHorizonPointDto(240, 30) };
            var (telescope, sequencer) = Rig();
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => Slew(new SlewRequestDto(5.5, 25), telescope, sequencer, Site(-20, custom: true, skyline).Object));
            Assert.That(refusal.Parameters["limit_deg"], Is.EqualTo(30.0));
            NothingDispatched(telescope);
        }

        [Test]
        public async Task A_target_above_the_custom_skyline_is_accepted() {
            var skyline = new[] { new CustomHorizonPointDto(0, 30), new CustomHorizonPointDto(120, 30), new CustomHorizonPointDto(240, 30) };
            var (telescope, sequencer) = Rig();
            var accepted = await Slew(new SlewRequestDto(RaOnTheMeridian(), 35), telescope, sequencer, Site(-20, custom: true, skyline).Object);
            Assert.That(accepted, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
        }

        [Test]
        public async Task A_sync_to_a_target_under_the_horizon_is_accepted() {
            var (telescope, sequencer) = Rig();
            var accepted = await Slew(new SlewRequestDto(5.5, 14, Sync: true), telescope, sequencer, Site(floorDeg: 20).Object);
            Assert.That(accepted, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
        }

        [Test]
        public async Task Force_does_not_bypass_the_horizon_refusal() {
            var (telescope, sequencer) = Rig();
            await Assert.ThrowsAsync<RefusalException>(() => Slew(new SlewRequestDto(5.5, 14, Force: true), telescope, sequencer, Site(floorDeg: 20).Object));
            NothingDispatched(telescope);
        }

        [Test]
        public async Task A_forced_slew_during_a_run_to_a_low_target_is_still_refused() {
            var (telescope, sequencer) = Rig(runs: [Guid.NewGuid()]);
            await Assert.ThrowsAsync<RefusalException>(() => Slew(new SlewRequestDto(5.5, 14, Force: true), telescope, sequencer, Site(floorDeg: 20).Object));
            NothingDispatched(telescope);
        }

        [Test]
        public async Task An_out_of_range_declination_is_not_answered_with_the_horizon_refusal() {
            var (telescope, sequencer) = Rig();
            telescope.Setup(t => t.SlewAsync(It.Is<SlewRequestDto>(r => r.DeclinationDegrees < -90), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ArgumentOutOfRangeException("request"));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Slew(new SlewRequestDto(5.5, -95), telescope, sequencer, Site(floorDeg: 20).Object));
        }

        [Test]
        public async Task A_missing_site_or_a_failed_transform_lets_the_slew_through() {
            var (telescope, sequencer) = Rig();
            Assert.That(await Slew(new SlewRequestDto(5.5, 14), telescope, sequencer, null), Is.InstanceOf<Accepted<OperationAcceptedDto>>(), "no profile store");
            var broken = new Mock<IProfileStore>();
            broken.Setup(p => p.GetSiteSettings()).Throws(new InvalidOperationException("profile unreadable"));
            Assert.That(await Slew(new SlewRequestDto(5.5, 14), telescope, sequencer, broken.Object), Is.InstanceOf<Accepted<OperationAcceptedDto>>(), "a profile fault");
        }

        [Test]
        public void The_shared_check_answers_the_altitude_and_limit_or_null() {
            var target = new Coordinates(ra: 83.8, dec: 14.0, Epoch.J2000, Coordinates.RAType.Degrees);
            var low = HorizonGuard.TargetBelowHorizon(Site(floorDeg: 20).Object, target);
            Assert.That(low?.LimitDeg, Is.EqualTo(20.0));
            Assert.That(HorizonGuard.TargetBelowHorizon(Site(floorDeg: 5).Object, target), Is.Null);
            Assert.That(HorizonGuard.TargetBelowHorizon(null, target), Is.Null);
        }
    }
}
