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
    }
}
