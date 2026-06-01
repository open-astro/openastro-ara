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
using OpenAstroAra.Sequencer.SequenceItem.Focuser;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Equipment;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-12 — verifies <see cref="HeadlessFocuserMediator"/> sentinel
    /// behavior + that the three focuser instructions register and resolve
    /// via the factory.
    /// </summary>
    [TestFixture]
    public class HeadlessFocuserMediatorTest {

        [Test]
        public void GetInfo_returns_not_connected() {
            var m = new HeadlessFocuserMediator();
            Assert.That(m.GetInfo().Connected, Is.False);
        }

        [Test]
        public async Task MoveFocuser_returns_zero_position() {
            var m = new HeadlessFocuserMediator();
            Assert.That(await m.MoveFocuser(1000, CancellationToken.None), Is.EqualTo(0));
        }

        [Test]
        public async Task MoveFocuserRelative_returns_zero() {
            var m = new HeadlessFocuserMediator();
            Assert.That(await m.MoveFocuserRelative(100, CancellationToken.None), Is.EqualTo(0));
        }

        [Test]
        public async Task MoveFocuserByTemperatureRelative_returns_zero() {
            var m = new HeadlessFocuserMediator();
            Assert.That(await m.MoveFocuserByTemperatureRelative(20.0, 1.5, CancellationToken.None), Is.EqualTo(0));
        }

        [Test]
        public void ToggleTempComp_does_not_throw() {
            var m = new HeadlessFocuserMediator();
            Assert.DoesNotThrow(() => m.ToggleTempComp(true));
            Assert.DoesNotThrow(() => m.ToggleTempComp(false));
        }

        // §38k-12 factory integration — 3 new instructions

        [Test]
        public void WithDefaults_registers_MoveFocuserAbsolute() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("MoveFocuserAbsolute"));
        }

        [Test]
        public void WithDefaults_registers_MoveFocuserRelative() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("MoveFocuserRelative"));
        }

        [Test]
        public void WithDefaults_registers_MoveFocuserByTemperature() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("MoveFocuserByTemperature"));
        }

        [Test]
        public void WithDefaults_factory_resolves_MoveFocuserAbsolute_via_prototype_lookup() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var prototype = factory.GetItem<MoveFocuserAbsolute>();
            Assert.That(prototype, Is.Not.Null);
            Assert.That(prototype, Is.InstanceOf<MoveFocuserAbsolute>());
        }
    }
}
