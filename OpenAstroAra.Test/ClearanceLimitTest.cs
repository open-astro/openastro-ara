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
using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1589 — the clearance limit: off by default (D9), so a profile that upgrades keeps its behaviour; on, a
    /// GoTo to a target at or above <c>clearance_max_altitude_deg</c> is refused with <c>above-clearance-limit</c>
    /// (409, ADR 0022) before it reaches the mount. The test site is 0.1° off the pole, so a target's altitude
    /// is its declination (±0.1°) at any time of day.
    /// </summary>
    [TestFixture]
    public class ClearanceLimitTest {

        private static readonly IReadOnlyList<Guid> None = Array.Empty<Guid>();

        private static SafetyPoliciesDto Safety(bool enabled, double limitDeg = 80) =>
            new(OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 5, MeridianRecenter: true,
                MeridianRecalGuider: true, OnAltitudeLimit: "skip_target",
                ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: true) {
                ClearanceLimitEnabled = enabled, ClearanceMaxAltitudeDeg = limitDeg,
            };

        private static Mock<IProfileStore> Store(SafetyPoliciesDto safety) {
            var store = new Mock<IProfileStore>();
            store.Setup(p => p.GetSiteSettings()).Returns(new SiteSettingsDto("pole", 89.9, 0, 0, "UTC", false, 0, 4, 2.0, "astronomical"));
            store.Setup(p => p.GetCustomHorizon()).Returns(new CustomHorizonDto(Array.Empty<CustomHorizonPointDto>()));
            store.Setup(p => p.GetSafetyPolicies()).Returns(safety);
            return store;
        }

        private static (Mock<ITelescopeService> Telescope, Mock<ISequencerService> Sequencer) Rig() {
            var telescope = new Mock<ITelescopeService>();
            telescope.Setup(t => t.SlewAsync(It.IsAny<SlewRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationAcceptedDto(Guid.NewGuid(), "telescope.slew", DateTimeOffset.UtcNow, null));
            var sequencer = new Mock<ISequencerService>();
            sequencer.Setup(s => s.ActiveRunIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(None);
            return (telescope, sequencer);
        }

        /// <summary>An RA on the meridian now (longitude 0): the #2073 counterweight check never refuses it.</summary>
        private static double RaOnTheMeridian() => SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, 0) / 15.0 % 24.0;

        private static Task<Microsoft.AspNetCore.Http.IResult> Slew(SlewRequestDto request, Mock<ITelescopeService> t, Mock<ISequencerService> s, IProfileStore profiles) =>
            EquipmentEndpoints.SlewAsync(request, null, t.Object, s.Object, profiles, CancellationToken.None);

        private static void Dispatched(Mock<ITelescopeService> telescope, Times times) =>
            telescope.Verify(t => t.SlewAsync(It.IsAny<SlewRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), times);

        [Test]
        public void The_clearance_limit_is_off_by_default_at_80_degrees() {
            // A profile.json from before the limit carries neither key: the record's own defaults stand in.
            var safety = new SafetyPoliciesDto(OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 5, MeridianRecenter: true, MeridianRecalGuider: true,
                OnAltitudeLimit: "skip_target", ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: true);
            Assert.Multiple(() => {
                Assert.That(safety.ClearanceLimitEnabled, Is.False);
                Assert.That(safety.ClearanceMaxAltitudeDeg, Is.EqualTo(80.0));
                Assert.That(ClearanceGuard.LimitDeg(safety), Is.Null, "off means no limit at all");
                Assert.That(ProfileSections.SafetyPolicies.Default!.ClearanceLimitEnabled, Is.False, "a new profile starts with it off");
            });
        }

        [Test]
        public void At_the_limit_counts_as_reached_and_just_under_it_does_not() {
            Assert.Multiple(() => {
                Assert.That(ClearanceGuard.Reached(80.0, 80.0), Is.True, "the stop comes at the limit, not past it");
                Assert.That(ClearanceGuard.Reached(79.99, 80.0), Is.False);
            });
        }

        [Test]
        public async Task With_the_clearance_limit_off_a_GoTo_near_the_zenith_goes_to_the_mount() {
            var (telescope, sequencer) = Rig();

            var result = await Slew(new SlewRequestDto(RaOnTheMeridian(), 88), telescope, sequencer, Store(Safety(enabled: false)).Object);

            Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.Accepted<OperationAcceptedDto>>());
            Dispatched(telescope, Times.Once());
        }

        [Test]
        public async Task A_GoTo_at_or_above_the_clearance_limit_is_refused_with_its_altitude_and_the_limit() {
            var (telescope, sequencer) = Rig();

            var refusal = await Assert.ThrowsAsync<RefusalException>(() =>
                Slew(new SlewRequestDto(RaOnTheMeridian(), 85, Force: true), telescope, sequencer, Store(Safety(enabled: true)).Object));

            Assert.Multiple(() => {
                Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.AboveClearanceLimit));
                Assert.That(refusal.Cause.Status, Is.EqualTo(409));
                Assert.That((double)refusal.Parameters["altitude_deg"]!, Is.EqualTo(85).Within(0.2));
                Assert.That(refusal.Parameters["limit_deg"], Is.EqualTo(80.0));
                Assert.That(refusal.Message, Does.Contain("80.0").And.Contain("clearance limit"));
            });
            Dispatched(telescope, Times.Never());
        }

        [Test]
        public async Task A_GoTo_under_the_clearance_limit_goes_to_the_mount() {
            var (telescope, sequencer) = Rig();

            await Slew(new SlewRequestDto(RaOnTheMeridian(), 75), telescope, sequencer, Store(Safety(enabled: true)).Object);

            Dispatched(telescope, Times.Once());
        }

        [Test]
        public async Task A_sync_above_the_clearance_limit_is_not_checked() {
            // A sync moves nothing.
            var (telescope, sequencer) = Rig();

            await Slew(new SlewRequestDto(RaOnTheMeridian(), 85, Sync: true), telescope, sequencer, Store(Safety(enabled: true)).Object);

            Dispatched(telescope, Times.Once());
        }

        [TestCase(90.0)]
        [TestCase(120.0)]
        [TestCase(0.0)]
        [TestCase(-5.0)]
        public void A_saved_clearance_limit_must_be_above_0_and_under_90_degrees(double limit) {
            var error = ProfileEndpoints.ClearanceLimitRangeError(Safety(enabled: false, limitDeg: limit));

            Assert.That(error, Does.Contain("clearance_max_altitude_deg"), "refused even while the limit is off, so turning it on cannot pick it up");
        }

        [Test]
        public void A_saved_clearance_limit_under_90_degrees_is_accepted() {
            Assert.That(ProfileEndpoints.ClearanceLimitRangeError(Safety(enabled: true, limitDeg: 89.5)), Is.Null);
        }
    }
}
