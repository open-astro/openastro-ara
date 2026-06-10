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
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Services;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free unit coverage for the §14e <see cref="IDomeMediator"/> surface that
    /// <see cref="DomeService"/> serves alongside its REST surface (one singleton backs both, so the
    /// dome instructions drive the live device). The live ops are exercised by the
    /// <c>[Category("Integration")]</c> companion test; here we cover the not-connected / disposed
    /// contracts the Sequencer relies on never to throw.
    /// </summary>
    [TestFixture]
    public class DomeMediatorTest {

        [Test]
        public void GetInfo_before_connect_reports_not_connected() {
            using var svc = new DomeService();
            var info = ((IDomeMediator)svc).GetInfo();
            Assert.That(info.Connected, Is.False);
            Assert.That(info.ShutterStatus, Is.EqualTo(ShutterState.ShutterNone));
        }

        [Test]
        public void GetInfo_after_Dispose_reports_not_connected_without_throwing() {
            var svc = new DomeService();
            svc.Dispose();
            var info = ((IDomeMediator)svc).GetInfo();
            Assert.That(info.Connected, Is.False);
        }

        [Test]
        public async Task OpenShutter_when_not_connected_returns_false_without_throwing() {
            using var svc = new DomeService();
            Assert.That(await ((IDomeMediator)svc).OpenShutter(CancellationToken.None), Is.False);
        }

        [Test]
        public async Task CloseShutter_when_not_connected_returns_false() {
            using var svc = new DomeService();
            Assert.That(await ((IDomeMediator)svc).CloseShutter(CancellationToken.None), Is.False);
        }

        [Test]
        public async Task Park_when_not_connected_returns_false() {
            using var svc = new DomeService();
            Assert.That(await ((IDomeMediator)svc).Park(CancellationToken.None), Is.False);
        }

        [Test]
        public async Task FindHome_when_not_connected_returns_false() {
            using var svc = new DomeService();
            Assert.That(await ((IDomeMediator)svc).FindHome(CancellationToken.None), Is.False);
        }

        [Test]
        public async Task SlewToAzimuth_when_not_connected_returns_false() {
            using var svc = new DomeService();
            Assert.That(await ((IDomeMediator)svc).SlewToAzimuth(180.0, CancellationToken.None), Is.False);
        }

        [Test]
        public async Task Following_surface_reports_not_following_stub() {
            using var svc = new DomeService();
            Assert.That(((IDomeMediator)svc).IsFollowingScope, Is.False);
            Assert.That(await ((IDomeMediator)svc).EnableFollowing(CancellationToken.None), Is.False);
            Assert.That(await ((IDomeMediator)svc).DisableFollowing(CancellationToken.None), Is.False);
        }
    }
}
