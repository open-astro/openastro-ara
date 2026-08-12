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
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §25.5.5 cooler capability gate: a camera without a cooler, or without TEC
    /// set-point regulation, must refuse the cooler request with a clean message
    /// instead of a driver 500. Covers the wild case of a "dumb" on/off cooler
    /// (HasCooler=true, CanSetTemperature=false) which is toggleable but has no
    /// set-point.
    /// </summary>
    [TestFixture]
    public class CoolerCapabilityGateTest {

        [Test]
        public void NoCooler_RefusesToggle() {
            var err = CameraService.CoolerCapabilityError(
                canSetTemperature: false, hasCooler: false, targetTemperatureC: null);
            Assert.That(err, Is.EqualTo("this camera does not support cooling"));
        }

        [Test]
        public void NoCooler_RefusesSetpointToo() {
            var err = CameraService.CoolerCapabilityError(
                canSetTemperature: false, hasCooler: false, targetTemperatureC: -10);
            Assert.That(err, Is.EqualTo("this camera does not support cooling"));
        }

        [Test]
        public void DumbCooler_ToggleAllowed_ButSetpointRefused() {
            // HasCooler=true, CanSetTemperature=false — the Mars-C II wild case:
            // the on/off Switch is legitimate, but a TEC set-point is not.
            Assert.That(CameraService.CoolerCapabilityError(
                canSetTemperature: false, hasCooler: true, targetTemperatureC: null), Is.Null);
            Assert.That(CameraService.CoolerCapabilityError(
                canSetTemperature: false, hasCooler: true, targetTemperatureC: -10),
                Is.EqualTo("this camera does not support a cooler set-point (can_set_temperature=false)"));
        }

        [Test]
        public void FullCooler_AllowsToggleAndSetpoint() {
            Assert.That(CameraService.CoolerCapabilityError(
                canSetTemperature: true, hasCooler: true, targetTemperatureC: null), Is.Null);
            Assert.That(CameraService.CoolerCapabilityError(
                canSetTemperature: true, hasCooler: true, targetTemperatureC: -10), Is.Null);
        }

        [Test]
        public void NullCaps_SkipTheGate() {
            // Caps not yet read: no refusal — the write is attempted and its
            // failure mapped cleanly by SetCoolerAsync's catch.
            Assert.That(CameraService.CoolerCapabilityError(
                canSetTemperature: null, hasCooler: null, targetTemperatureC: null), Is.Null);
            Assert.That(CameraService.CoolerCapabilityError(
                canSetTemperature: null, hasCooler: null, targetTemperatureC: -10), Is.Null);
        }
    }
}
