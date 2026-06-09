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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Server.Services.Equipment;
using System;
using System.Threading;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-19 — verifies <see cref="HeadlessFlatDeviceMediator"/> sentinel
    /// behavior. Completes the device-mediator stub set together with
    /// <see cref="HeadlessWeatherDataMediator"/>. No instruction is registered
    /// against it (there are no flat-device sequence items; the disconnect
    /// instructions that would consume the full device set are deferred with the
    /// Connect capstone).
    /// </summary>
    [TestFixture]
    public class HeadlessFlatDeviceMediatorTest {

        [Test]
        public void GetInfo_returns_not_connected() {
            var m = new HeadlessFlatDeviceMediator();
            Assert.That(m.GetInfo().Connected, Is.False);
        }

        [Test]
        public void Cover_ops_do_not_throw() {
            var m = new HeadlessFlatDeviceMediator();
            var p = new Progress<ApplicationStatus>();
            Assert.DoesNotThrowAsync(() => m.OpenCover(p, CancellationToken.None));
            Assert.DoesNotThrowAsync(() => m.CloseCover(p, CancellationToken.None));
            Assert.DoesNotThrowAsync(() => m.SetBrightness(50, p, CancellationToken.None));
            Assert.DoesNotThrowAsync(() => m.ToggleLight(true, p, CancellationToken.None));
        }

        [Test]
        public void GetDevice_throws_not_supported() {
            var m = new HeadlessFlatDeviceMediator();
            Assert.Throws<NotSupportedException>(() => m.GetDevice());
        }
    }
}
