#region "copyright"

/*
    Copyright (c) 2026 OpenAstro contributors

    This file is part of OpenAstro Ara.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Equipment.Equipment.MyDome;
using OpenAstroAra.Profile.Interfaces;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Pins the dome-geometry port from Accord.Math's column-vector Matrix4x4 to System.Numerics
    /// (#1386 part 3). The expected azimuth/altitude pairs were computed with the Accord build of
    /// the same code for one offset configuration (scope 100 mm north, 50 mm east, 200 mm up;
    /// lateral 30 mm; GEM axis 250 mm; Dec offset 40 mm; dome radius 2500 mm). The two libraries
    /// agree to 0.006° on 200,000 random configurations; the tolerance here is 0.01°.
    /// </summary>
    [TestFixture]
    public class DomeSynchronizationTest {

        private static DomeSynchronization Sut(OpenAstroAra.Profile.Profile profile, MountType mountType) {
            var d = profile.DomeSettings;
            d.MountType = mountType;
            d.ScopePositionNorthSouthMm = 100;
            d.ScopePositionEastWestMm = 50;
            d.ScopePositionUpDownMm = 200;
            d.LateralAxisMm = 30;
            d.GemAxisMm = 250;
            d.DecOffsetHorizontalMm = 40;
            d.DomeRadiusMm = 2500;
            var svc = new Mock<IProfileService>();
            svc.SetupGet(p => p.ActiveProfile).Returns(profile);
            return new DomeSynchronization(svc.Object);
        }

        [TestCase(MountType.EQUATORIAL, 47.0, 12.0, 10.0, 45.0, PierSide.pierEast, 282.16305, 87.28645)]
        [TestCase(MountType.EQUATORIAL, 47.0, 12.0, 10.0, 45.0, PierSide.pierWest, 283.71050, 83.90945)]
        [TestCase(MountType.ForkOnWedge, 47.0, 3.5, 2.0, -10.0, PierSide.pierEast, 204.31931, 36.96347)]
        [TestCase(MountType.EQUATORIAL, -33.9, 20.0, 22.5, -60.0, PierSide.pierEast, 139.12312, 60.41353)]
        [TestCase(MountType.ForkOnWedge, -33.9, 6.0, 5.0, 20.0, PierSide.pierEast, 344.20620, 37.13066)]
        public void TargetDomeCoordinates_matches_the_Accord_build(MountType mountType, double latitude, double lst, double raHours, double decDeg, PierSide pierSide, double expectedAz, double expectedAlt) {
            using var profile = new OpenAstroAra.Profile.Profile();
            var sut = Sut(profile, mountType);
            var scope = new Coordinates(raHours, decDeg, Epoch.JNOW, Coordinates.RAType.Hours);

            var dome = sut.TargetDomeCoordinates(scope, lst, Angle.ByDegree(latitude), Angle.ByDegree(-105.0), 1500, pierSide);

            Assert.That(dome.Azimuth.Degree, Is.EqualTo(expectedAz).Within(0.01));
            Assert.That(dome.Altitude.Degree, Is.EqualTo(expectedAlt).Within(0.01));
        }
    }
}
