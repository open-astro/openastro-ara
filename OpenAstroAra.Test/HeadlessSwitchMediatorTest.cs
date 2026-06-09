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
using OpenAstroAra.Sequencer.SequenceItem.Switch;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Equipment;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-17 — verifies <see cref="HeadlessSwitchMediator"/> sentinel
    /// behavior + that the SetSwitchValue instruction registers and resolves
    /// via the factory.
    /// </summary>
    [TestFixture]
    public class HeadlessSwitchMediatorTest {

        [Test]
        public void GetInfo_returns_not_connected() {
            var m = new HeadlessSwitchMediator();
            Assert.That(m.GetInfo().Connected, Is.False);
        }

        [Test]
        public void SetSwitchValue_does_not_throw() {
            var m = new HeadlessSwitchMediator();
            Assert.DoesNotThrowAsync(() =>
                m.SetSwitchValue(0, 1.0, new Progress<ApplicationStatus>(), CancellationToken.None));
        }

        [Test]
        public void GetDevice_throws_not_supported() {
            var m = new HeadlessSwitchMediator();
            Assert.Throws<NotSupportedException>(() => m.GetDevice());
        }

        [Test]
        public void WithDefaults_registers_SetSwitchValue() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("SetSwitchValue"));
        }

        [Test]
        public void WithDefaults_factory_resolves_SetSwitchValue_via_prototype_lookup() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var prototype = factory.GetItem<SetSwitchValue>();
            Assert.That(prototype, Is.Not.Null);
            Assert.That(prototype, Is.InstanceOf<SetSwitchValue>());
        }
    }
}
