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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Services;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free unit coverage for the §14e <see cref="IFocuserMediator"/> surface that
    /// <see cref="FocuserService"/> serves alongside its REST surface (one singleton backs both, so
    /// the <c>MoveFocuser*</c> sequence instructions drive the live device). The live move is
    /// exercised by the <c>[Category("Integration")]</c> companion test; here we cover the
    /// not-connected / disposed contracts the Sequencer relies on never to throw.
    /// </summary>
    [TestFixture]
    public class FocuserMediatorTest {

        [Test]
        public void GetInfo_before_connect_reports_not_connected() {
            using var svc = new FocuserService();
            var info = ((IFocuserMediator)svc).GetInfo();
            Assert.That(info.Connected, Is.False);
            Assert.That(info.Position, Is.EqualTo(0));
        }

        [Test]
        public void GetInfo_after_Dispose_reports_not_connected_without_throwing() {
            var svc = new FocuserService();
            svc.Dispose();
            // A running sequence may poll GetInfo during shutdown; it must report "not connected"
            // rather than throw ObjectDisposedException (unlike the REST GetAsync).
            var info = ((IFocuserMediator)svc).GetInfo();
            Assert.That(info.Connected, Is.False);
        }

        [Test]
        public async Task MoveFocuser_when_not_connected_returns_cached_position_without_throwing() {
            using var svc = new FocuserService();
            var pos = await ((IFocuserMediator)svc).MoveFocuser(1234, CancellationToken.None);
            // Not connected → no move, reports the cached position (0), never throws into the run.
            Assert.That(pos, Is.EqualTo(0));
        }

        [Test]
        public async Task MoveFocuserRelative_when_not_connected_returns_cached_position() {
            using var svc = new FocuserService();
            var pos = await ((IFocuserMediator)svc).MoveFocuserRelative(50, CancellationToken.None);
            Assert.That(pos, Is.EqualTo(0));
        }

        [Test]
        public async Task MoveFocuserByTemperatureRelative_when_not_connected_returns_cached_position() {
            using var svc = new FocuserService();
            var pos = await ((IFocuserMediator)svc).MoveFocuserByTemperatureRelative(10.0, 2.0, CancellationToken.None);
            Assert.That(pos, Is.EqualTo(0));
        }

        [Test]
        public void ToggleTempComp_when_not_connected_is_a_no_op() {
            using var svc = new FocuserService();
            Assert.DoesNotThrow(() => ((IFocuserMediator)svc).ToggleTempComp(true));
        }
    }
}
