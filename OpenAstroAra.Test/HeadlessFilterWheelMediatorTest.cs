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
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Server.Services.Equipment;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-15 — verifies <see cref="HeadlessFilterWheelMediator"/> sentinel
    /// behavior. The SwitchFilter instruction is deferred (needs
    /// IProfileService), so there is no factory-registration test here.
    /// </summary>
    [TestFixture]
    public class HeadlessFilterWheelMediatorTest {

        [Test]
        public void GetInfo_returns_not_connected() {
            var m = new HeadlessFilterWheelMediator();
            Assert.That(m.GetInfo().Connected, Is.False);
        }

        [Test]
        public async Task ChangeFilter_echoes_requested_filter() {
            var m = new HeadlessFilterWheelMediator();
            var requested = new FilterInfo();
            var result = await m.ChangeFilter(requested, null, CancellationToken.None);
            Assert.That(result, Is.SameAs(requested));
        }

        [Test]
        public void GetDevice_throws_not_supported() {
            var m = new HeadlessFilterWheelMediator();
            Assert.Throws<NotSupportedException>(() => m.GetDevice());
        }
    }
}
