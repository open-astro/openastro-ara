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
using OpenAstroAra.Sequencer.SequenceItem.Telescope;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Equipment;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-10 — verifies the second equipment-mediator stub
    /// (<see cref="HeadlessTelescopeMediator"/>) reports the not-connected
    /// sentinel + returns false for every operation, and that
    /// <see cref="HeadlessSequencerFactory.WithDefaults"/> picks it up so
    /// <see cref="SetTracking"/> lands as a resolvable prototype.
    /// </summary>
    [TestFixture]
    public class HeadlessTelescopeMediatorTest {

        [Test]
        public void GetInfo_returns_not_connected() {
            var m = new HeadlessTelescopeMediator();
            Assert.That(m.GetInfo().Connected, Is.False);
        }

        [Test]
        public async Task Connect_returns_false_without_real_driver() {
            var m = new HeadlessTelescopeMediator();
            Assert.That(await m.Connect(), Is.False);
        }

        [Test]
        public void Tracking_setters_no_op_to_false() {
            var m = new HeadlessTelescopeMediator();
            Assert.That(m.SetTrackingMode(TrackingMode.Sidereal), Is.False);
            Assert.That(m.SetTrackingEnabled(true), Is.False);
        }

        [Test]
        public async Task ParkTelescope_and_UnparkTelescope_return_false() {
            var m = new HeadlessTelescopeMediator();
            Assert.That(await m.ParkTelescope(progress: null!, CancellationToken.None), Is.False);
            Assert.That(await m.UnparkTelescope(progress: null!, CancellationToken.None), Is.False);
        }

        [Test]
        public void GetCurrentPosition_returns_zero_J2000_sentinel() {
            var m = new HeadlessTelescopeMediator();
            var pos = m.GetCurrentPosition();
            Assert.That(pos, Is.Not.Null);
            Assert.That(pos.RADegrees, Is.EqualTo(0).Within(1e-9));
            Assert.That(pos.Dec, Is.EqualTo(0).Within(1e-9));
        }

        // §38k-10 factory integration

        [Test]
        public void WithDefaults_registers_SetTracking_with_default_stub() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var typeNames = factory.Items.Select(i => i.GetType().Name).ToList();
            Assert.That(typeNames, Does.Contain("SetTracking"));
        }

        [Test]
        public void WithDefaults_factory_resolves_SetTracking_via_prototype_lookup() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var prototype = factory.GetItem<SetTracking>();
            Assert.That(prototype, Is.Not.Null);
            Assert.That(prototype, Is.InstanceOf<SetTracking>());
        }
    }
}