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
using OpenAstroAra.Sequencer.SequenceItem.Telescope;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Equipment;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-11 — verifies the third equipment-mediator stub
    /// (<see cref="HeadlessGuiderMediator"/>) returns "not connected" /
    /// "didn't succeed" sentinels, and that the four newly-registered
    /// telescope instructions resolve via the factory prototype lookup.
    /// </summary>
    [TestFixture]
    public class HeadlessGuiderMediatorTest {

        [Test]
        public void GetInfo_returns_not_connected() {
            var m = new HeadlessGuiderMediator();
            Assert.That(m.GetInfo().Connected, Is.False);
        }

        [Test]
        public async Task StartGuiding_returns_false_without_real_driver() {
            var m = new HeadlessGuiderMediator();
            var ok = await m.StartGuiding(forceCalibration: false, progress: null!, CancellationToken.None);
            Assert.That(ok, Is.False);
        }

        [Test]
        public async Task StopGuiding_returns_false_without_real_driver() {
            var m = new HeadlessGuiderMediator();
            Assert.That(await m.StopGuiding(CancellationToken.None), Is.False);
        }

        [Test]
        public async Task Dither_returns_false_without_real_driver() {
            var m = new HeadlessGuiderMediator();
            Assert.That(await m.Dither(CancellationToken.None), Is.False);
        }

        [Test]
        public void StartRMSRecording_returns_empty_guid() {
            var m = new HeadlessGuiderMediator();
            Assert.That(m.StartRMSRecording(), Is.EqualTo(Guid.Empty));
        }

        // §38k-11 factory integration — 4 new instructions

        [Test]
        public void WithDefaults_registers_UnparkScope() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("UnparkScope"));
        }

        [Test]
        public void WithDefaults_registers_ParkScope() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("ParkScope"));
        }

        [Test]
        public void WithDefaults_registers_FindHome() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("FindHome"));
        }

        [Test]
        public void WithDefaults_registers_SlewScopeToRaDec() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("SlewScopeToRaDec"));
        }

        [Test]
        public void WithDefaults_factory_resolves_ParkScope_via_prototype_lookup() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var prototype = factory.GetItem<ParkScope>();
            Assert.That(prototype, Is.Not.Null);
            Assert.That(prototype, Is.InstanceOf<ParkScope>());
        }

        // §38k-14 — guider-only instructions on the existing guider stub.
        // (Dither also needs IProfileService → deferred to a follow-up.)

        [Test]
        public void WithDefaults_registers_StartGuiding() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("StartGuiding"));
        }

        [Test]
        public void WithDefaults_registers_StopGuiding() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("StopGuiding"));
        }
    }
}