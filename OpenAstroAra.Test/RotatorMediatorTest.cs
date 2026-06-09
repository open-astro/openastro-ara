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
    /// Sim-free unit coverage for the §14e <see cref="IRotatorMediator"/> surface that
    /// <see cref="RotatorService"/> serves alongside its REST surface (one singleton backs both, so
    /// the <c>MoveRotatorMechanical</c> instruction drives the live device). The live move is
    /// exercised by the <c>[Category("Integration")]</c> companion test; here we cover the
    /// not-connected / disposed contracts the Sequencer relies on never to throw, plus the pure
    /// angle-normalising helpers.
    /// </summary>
    [TestFixture]
    public class RotatorMediatorTest {

        [Test]
        public void GetInfo_before_connect_reports_not_connected() {
            using var svc = new RotatorService();
            var info = ((IRotatorMediator)svc).GetInfo();
            Assert.That(info.Connected, Is.False);
            Assert.That(info.Position, Is.EqualTo(0f));
            Assert.That(info.MechanicalPosition, Is.EqualTo(0f));
        }

        [Test]
        public void GetInfo_after_Dispose_reports_not_connected_without_throwing() {
            var svc = new RotatorService();
            svc.Dispose();
            // A running sequence may poll GetInfo during shutdown; it must report "not connected"
            // rather than throw ObjectDisposedException (unlike the REST GetAsync).
            var info = ((IRotatorMediator)svc).GetInfo();
            Assert.That(info.Connected, Is.False);
        }

        [Test]
        public async Task MoveMechanical_when_not_connected_returns_cached_angle_without_throwing() {
            using var svc = new RotatorService();
            var angle = await ((IRotatorMediator)svc).MoveMechanical(123f, CancellationToken.None);
            // Not connected → no move, reports the cached angle (0), never throws into the run.
            Assert.That(angle, Is.EqualTo(0f));
        }

        [Test]
        public async Task Move_when_not_connected_returns_cached_angle() {
            using var svc = new RotatorService();
            var angle = await ((IRotatorMediator)svc).Move(90f, CancellationToken.None);
            Assert.That(angle, Is.EqualTo(0f));
        }

        [Test]
        public async Task MoveRelative_when_not_connected_returns_cached_angle() {
            using var svc = new RotatorService();
            var angle = await ((IRotatorMediator)svc).MoveRelative(45f, CancellationToken.None);
            Assert.That(angle, Is.EqualTo(0f));
        }

        [Test]
        public void Sync_when_not_connected_is_a_no_op() {
            using var svc = new RotatorService();
            Assert.DoesNotThrow(() => ((IRotatorMediator)svc).Sync(180f));
        }

        [Test]
        public void GetTargetPosition_normalises_into_zero_to_360() {
            using var svc = new RotatorService();
            Assert.That(((IRotatorMediator)svc).GetTargetPosition(0f), Is.EqualTo(0f));
            Assert.That(((IRotatorMediator)svc).GetTargetPosition(359.5f), Is.EqualTo(359.5f).Within(1e-3));
            Assert.That(((IRotatorMediator)svc).GetTargetPosition(360f), Is.EqualTo(0f).Within(1e-3), "360 wraps to 0");
            Assert.That(((IRotatorMediator)svc).GetTargetPosition(370f), Is.EqualTo(10f).Within(1e-3));
            Assert.That(((IRotatorMediator)svc).GetTargetPosition(-10f), Is.EqualTo(350f).Within(1e-3), "negative wraps positive");
        }
    }
}
