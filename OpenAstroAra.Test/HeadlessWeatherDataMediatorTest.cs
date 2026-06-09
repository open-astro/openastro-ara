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
using OpenAstroAra.Server.Services.Equipment;
using System;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-20 — verifies <see cref="HeadlessWeatherDataMediator"/> sentinel
    /// behavior. <c>IWeatherDataMediator</c> adds no members beyond the
    /// <c>IDeviceMediator</c> base, so the surface is just GetInfo + GetDevice.
    /// </summary>
    [TestFixture]
    public class HeadlessWeatherDataMediatorTest {

        [Test]
        public void GetInfo_returns_not_connected() {
            var m = new HeadlessWeatherDataMediator();
            Assert.That(m.GetInfo().Connected, Is.False);
        }

        [Test]
        public void GetDevice_throws_not_supported() {
            var m = new HeadlessWeatherDataMediator();
            Assert.Throws<NotSupportedException>(() => m.GetDevice());
        }
    }
}
