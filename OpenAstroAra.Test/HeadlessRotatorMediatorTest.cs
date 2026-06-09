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
using OpenAstroAra.Sequencer.SequenceItem.Rotator;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Equipment;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-16 — verifies <see cref="HeadlessRotatorMediator"/> sentinel
    /// behavior + that the MoveRotatorMechanical instruction registers and
    /// resolves via the factory.
    /// </summary>
    [TestFixture]
    public class HeadlessRotatorMediatorTest {

        [Test]
        public void GetInfo_returns_not_connected() {
            var m = new HeadlessRotatorMediator();
            Assert.That(m.GetInfo().Connected, Is.False);
        }

        [Test]
        public async Task MoveMechanical_returns_zero() {
            var m = new HeadlessRotatorMediator();
            Assert.That(await m.MoveMechanical(180f, CancellationToken.None), Is.EqualTo(0f));
        }

        [Test]
        public void GetTargetPosition_echoes_input() {
            var m = new HeadlessRotatorMediator();
            Assert.That(m.GetTargetPosition(123.5f), Is.EqualTo(123.5f));
        }

        [Test]
        public void GetDevice_throws_not_supported() {
            var m = new HeadlessRotatorMediator();
            Assert.Throws<NotSupportedException>(() => m.GetDevice());
        }

        [Test]
        public void WithDefaults_registers_MoveRotatorMechanical() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("MoveRotatorMechanical"));
        }

        [Test]
        public void WithDefaults_factory_resolves_MoveRotatorMechanical_via_prototype_lookup() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var prototype = factory.GetItem<MoveRotatorMechanical>();
            Assert.That(prototype, Is.Not.Null);
            Assert.That(prototype, Is.InstanceOf<MoveRotatorMechanical>());
        }
    }
}
