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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Profile.Interfaces;
using System;

namespace OpenAstroAra.Test.AstrometryTest {

    /// <summary>
    /// §58 — the side-of-pier projection + flip-timing astrometry in <see cref="MeridianFlip"/> that the
    /// §58.2 <c>MeridianFlipTrigger.ShouldTrigger</c> decision depends on. A wrong pier-side projection feeds a
    /// wrong flip decision (an un-flipped OTA can swing into the mount), so this locks the math down.
    ///
    /// All coordinates are built in <see cref="Epoch.JNOW"/>: <see cref="Coordinates.Transform"/> short-circuits
    /// a same-epoch transform to an identity copy (no SOFA precession), so every case here is exact and
    /// platform-independent — no native-library or wall-clock dependency.
    /// </summary>
    [TestFixture]
    public class MeridianFlipTest {

        // RA/Dec in JNOW so the internal Transform(JNOW) is a no-op; Dec is irrelevant to the hour-angle math.
        private static Coordinates JNow(double raHours) =>
            new(Angle.ByHours(raHours), Angle.ByDegree(20), Epoch.JNOW);

        private static Angle Lst(double hours) => Angle.ByHours(hours);

        // ExpectedPierSide: hoursToLST = (RA - LST) mod 24 ∈ [0,24); < 12 → West, ≥ 12 → East.
        // (West = counterweight-down expected on the west side, per the method's contract.)
        [Test]
        [TestCase(12.0, 12.0, PierSide.pierWest)] // on the meridian → hoursToLST 0
        [TestCase(18.0, 12.0, PierSide.pierWest)] // hoursToLST 6
        [TestCase(12.0, 1.0, PierSide.pierWest)]  // hoursToLST 11 (just under the boundary)
        [TestCase(6.0, 12.0, PierSide.pierEast)]  // hoursToLST 18
        [TestCase(1.0, 12.0, PierSide.pierEast)]  // hoursToLST 13
        [TestCase(0.0, 12.0, PierSide.pierEast)]  // hoursToLST exactly 12 → East (boundary is ≥ 12)
        [TestCase(12.0, 0.0, PierSide.pierEast)]  // hoursToLST exactly 12 → East
        [TestCase(1.0, 23.0, PierSide.pierWest)]  // wrap: (1-23) mod 24 = 2
        [TestCase(23.0, 1.0, PierSide.pierEast)]  // wrap: (23-1) = 22
        public void ExpectedPierSide_projects_from_hour_angle(double raHours, double lstHours, PierSide expected) {
            var result = MeridianFlip.ExpectedPierSide(JNow(raHours), Lst(lstHours));
            Assert.That(result, Is.EqualTo(expected));
        }

        // TimeToMeridian: hoursToMeridian = (RA - LST) mod 12 ∈ [0,12).
        [Test]
        [TestCase(12.0, 12.0, 0.0)]   // on the meridian
        [TestCase(13.0, 12.0, 1.0)]   // 1h east of the meridian
        [TestCase(12.0, 13.0, 11.0)]  // just past → wraps to 11h until the next transit
        [TestCase(18.0, 12.0, 6.0)]
        [TestCase(12.0, 18.0, 6.0)]   // mod 12 wrap
        [TestCase(1.0, 12.0, 1.0)]    // (1-12) mod 12 = 1
        public void TimeToMeridian_is_hour_angle_mod_12(double raHours, double lstHours, double expectedHours) {
            var result = MeridianFlip.TimeToMeridian(JNow(raHours), Lst(lstHours));
            Assert.That(result.TotalHours, Is.EqualTo(expectedHours).Within(1e-6));
        }

        private static IMeridianFlipSettings Settings(bool useSideOfPier, double maxMinutesAfterMeridian = 15) {
            var s = new Mock<IMeridianFlipSettings>();
            s.SetupGet(x => x.UseSideOfPier).Returns(useSideOfPier);
            s.SetupGet(x => x.MaxMinutesAfterMeridian).Returns(maxMinutesAfterMeridian);
            return s.Object;
        }

        [Test]
        public void TimeToMeridianFlip_without_side_of_pier_is_the_projected_time_to_meridian() {
            // projectedLST = LST - MaxMinutesAfterMeridian (15min). On the meridian: time-to-flip = the 15min
            // grace window itself.
            var result = MeridianFlip.TimeToMeridianFlip(
                Settings(useSideOfPier: false), JNow(12.0), Lst(12.0), PierSide.pierUnknown);
            Assert.That(result.TotalMinutes, Is.EqualTo(15.0).Within(1e-3));
        }

        [Test]
        public void TimeToMeridianFlip_with_side_of_pier_but_unknown_pier_ignores_pier_side() {
            // UseSideOfPier on, but the driver reports pierUnknown → same result as the no-pier path.
            var result = MeridianFlip.TimeToMeridianFlip(
                Settings(useSideOfPier: true), JNow(12.0), Lst(12.0), PierSide.pierUnknown);
            Assert.That(result.TotalMinutes, Is.EqualTo(15.0).Within(1e-3));
        }

        [Test]
        public void TimeToMeridianFlip_defers_12h_when_close_to_meridian_but_already_in_the_flipped_state() {
            // Just BEFORE the meridian (LST 11.9, RA 12): time-to-meridian 0.1h (< 1h). Expected pier side there
            // is West, but the mount reports East → it's already flipped, so the next flip is ~12h out.
            var coords = JNow(12.0);
            var pierAware = MeridianFlip.TimeToMeridianFlip(
                Settings(useSideOfPier: true), coords, Lst(11.9), PierSide.pierEast);
            var baseline = MeridianFlip.TimeToMeridianFlip(
                Settings(useSideOfPier: false), coords, Lst(11.9), PierSide.pierEast);

            Assert.That(baseline.TotalHours, Is.EqualTo(0.35).Within(1e-3));        // projected time-to-meridian
            Assert.That(pierAware.TotalHours, Is.EqualTo(0.35 + 12.0).Within(1e-3)); // + 12h deferral
        }

        [Test]
        public void TimeToMeridianFlip_defers_12h_when_just_past_meridian_and_pier_already_matches() {
            // Just PAST the meridian (LST 12.1, RA 12): the flip window is imminent, the expected pier side (East)
            // already matches the mount → the flip just happened, so the next one is ~12h out.
            var coords = JNow(12.0);
            var pierAware = MeridianFlip.TimeToMeridianFlip(
                Settings(useSideOfPier: true), coords, Lst(12.1), PierSide.pierEast);
            var baseline = MeridianFlip.TimeToMeridianFlip(
                Settings(useSideOfPier: false), coords, Lst(12.1), PierSide.pierEast);

            Assert.That(baseline.TotalHours, Is.EqualTo(0.15).Within(1e-3));
            Assert.That(pierAware.TotalHours, Is.EqualTo(0.15 + 12.0).Within(1e-3));
        }

        [Test]
        public void TimeToMeridianFlip_does_not_defer_when_pier_side_matches_the_pre_flip_expectation() {
            // Just before the meridian (LST 11.9), the mount reports the expected pre-flip pier side (West):
            // a normal upcoming flip, no 12h deferral.
            var result = MeridianFlip.TimeToMeridianFlip(
                Settings(useSideOfPier: true), JNow(12.0), Lst(11.9), PierSide.pierWest);
            Assert.That(result.TotalHours, Is.EqualTo(0.35).Within(1e-3));
        }
    }
}
