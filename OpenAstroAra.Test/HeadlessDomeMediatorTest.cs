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
using OpenAstroAra.Sequencer.SequenceItem.Dome;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Equipment;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-18 — verifies <see cref="HeadlessDomeMediator"/> sentinel behavior
    /// + that the seven dome instructions register and resolve via the factory.
    /// (SynchronizeDome is deferred — it also needs IDomeFollower.)
    /// </summary>
    [TestFixture]
    public class HeadlessDomeMediatorTest {

        [Test]
        public void GetInfo_returns_not_connected() {
            var m = new HeadlessDomeMediator();
            Assert.That(m.GetInfo().Connected, Is.False);
        }

        [Test]
        public void IsFollowingScope_is_false() {
            var m = new HeadlessDomeMediator();
            Assert.That(m.IsFollowingScope, Is.False);
        }

        [Test]
        public async Task OpenShutter_returns_false() {
            var m = new HeadlessDomeMediator();
            Assert.That(await m.OpenShutter(CancellationToken.None), Is.False);
        }

        [Test]
        public async Task Park_returns_false() {
            var m = new HeadlessDomeMediator();
            Assert.That(await m.Park(CancellationToken.None), Is.False);
        }

        [Test]
        public void GetDevice_throws_not_supported() {
            var m = new HeadlessDomeMediator();
            Assert.Throws<NotSupportedException>(() => m.GetDevice());
        }

        [TestCase("OpenDomeShutter")]
        [TestCase("CloseDomeShutter")]
        [TestCase("ParkDome")]
        [TestCase("FindHomeDome")]
        [TestCase("SlewDomeAzimuth")]
        [TestCase("EnableDomeSynchronization")]
        [TestCase("DisableDomeSynchronization")]
        public void WithDefaults_registers_dome_instruction(string typeName) {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain(typeName));
        }

        [Test]
        public void WithDefaults_factory_resolves_OpenDomeShutter_via_prototype_lookup() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var prototype = factory.GetItem<OpenDomeShutter>();
            Assert.That(prototype, Is.Not.Null);
            Assert.That(prototype, Is.InstanceOf<OpenDomeShutter>());
        }
    }
}
