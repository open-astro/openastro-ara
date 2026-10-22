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
using OpenAstroAra.Equipment.Equipment.MyFocuser;
using OpenAstroAra.Equipment.Equipment.MyRotator;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38: the focuser-position snapshot CameraService stamps onto a capture
    /// (FITS FOCUSPOS header + catalog column) for the §50.4 focus-vs-temperature
    /// view — present only when a focuser is connected.
    /// </summary>
    [TestFixture]
    public class CameraServiceFocuserPositionTest {

        [Test]
        public void Connected_focuser_yields_its_position() {
            var info = new FocuserInfo { Connected = true, Position = 1234 };
            Assert.That(CameraService.FocuserPositionFrom(info), Is.EqualTo(1234));
        }

        [Test]
        public void Disconnected_focuser_yields_null() {
            var info = new FocuserInfo { Connected = false, Position = 1234 };
            Assert.That(CameraService.FocuserPositionFrom(info), Is.Null);
        }

        [Test]
        public void No_focuser_yields_null() {
            Assert.That(CameraService.FocuserPositionFrom(null), Is.Null);
        }

        [Test]
        public void Connected_rotator_yields_its_mechanical_angle() {
            var info = new RotatorInfo { Connected = true, MechanicalPosition = 212.5f, Position = 30f };
            Assert.That(CameraService.RotatorMechanicalFrom(info), Is.EqualTo(212.5),
                "the mechanical angle, not the sky angle — the dust turns with the camera");
        }

        [Test]
        public void Disconnected_or_missing_rotator_yields_null() {
            Assert.That(CameraService.RotatorMechanicalFrom(new RotatorInfo { Connected = false, MechanicalPosition = 10f }), Is.Null);
            Assert.That(CameraService.RotatorMechanicalFrom(new RotatorInfo { Connected = true, MechanicalPosition = float.NaN }), Is.Null);
            Assert.That(CameraService.RotatorMechanicalFrom(null), Is.Null);
        }

        [Test]
        public void Cooler_setpoint_is_recorded_only_while_the_cooler_is_on() {
            Assert.That(CameraService.CoolerSetpointFrom(true, -10), Is.EqualTo(-10));
            // SetCCDTemperature reads back the last target with the cooler off — that frame was warm.
            Assert.That(CameraService.CoolerSetpointFrom(false, -10), Is.Null);
            Assert.That(CameraService.CoolerSetpointFrom(null, -10), Is.Null, "an unreadable CoolerOn is not a cooled frame");
            Assert.That(CameraService.CoolerSetpointFrom(true, null), Is.Null);
        }
    }
}
