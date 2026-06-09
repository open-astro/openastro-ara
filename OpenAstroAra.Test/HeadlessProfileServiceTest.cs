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
using OpenAstroAra.Sequencer.SequenceItem.Connect;
using OpenAstroAra.Sequencer.SequenceItem.FilterWheel;
using OpenAstroAra.Sequencer.SequenceItem.Guider;
using OpenAstroAra.Server.Services;
using System.Globalization;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-22 — verifies the <see cref="HeadlessProfileService"/> stub satisfies
    /// the profile-bound instruction prototypes (Dither / SwitchFilter / the
    /// Connect dir), which complete the §38k instruction-registration surface.
    /// </summary>
    [TestFixture]
    public class HeadlessProfileServiceTest {

        [Test]
        public void ActiveProfile_is_a_default_profile() {
            var s = new HeadlessProfileService();
            Assert.That(s.ActiveProfile, Is.Not.Null);
        }

        [Test]
        public void Profiles_is_non_null_and_command_line_flag_false() {
            var s = new HeadlessProfileService();
            Assert.That(s.Profiles, Is.Not.Null);
            Assert.That(s.ProfileWasSpecifiedFromCommandLineArgs, Is.False);
        }

        [Test]
        public void Mutators_do_not_throw() {
            var s = new HeadlessProfileService();
            Assert.DoesNotThrow(() => {
                s.Add();
                s.ChangeLatitude(40.0);
                s.ChangeLongitude(-105.0);
                s.ChangeElevation(1600);
                s.ChangeLocale(CultureInfo.InvariantCulture);
                s.ChangeHorizon(string.Empty);
                s.Release();
            });
            Assert.That(s.Clone(null!), Is.False);
            Assert.That(s.SelectProfile(null!), Is.False);
            Assert.That(s.RemoveProfile(null!), Is.False);
        }

        // §38k-22 factory integration — the seven profile-bound instructions.

        [TestCase("Dither")]
        [TestCase("SwitchFilter")]
        [TestCase("ConnectAllEquipment")]
        [TestCase("ConnectEquipment")]
        [TestCase("DisconnectAllEquipment")]
        [TestCase("DisconnectEquipment")]
        [TestCase("SwitchProfile")]
        public void WithDefaults_registers_profile_bound_instruction(string typeName) {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain(typeName));
        }

        [Test]
        public void WithDefaults_factory_resolves_Dither_via_prototype_lookup() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var prototype = factory.GetItem<Dither>();
            Assert.That(prototype, Is.Not.Null);
            Assert.That(prototype, Is.InstanceOf<Dither>());
        }

        [Test]
        public void WithDefaults_factory_resolves_ConnectAllEquipment_via_prototype_lookup() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var prototype = factory.GetItem<ConnectAllEquipment>();
            Assert.That(prototype, Is.Not.Null);
            Assert.That(prototype, Is.InstanceOf<ConnectAllEquipment>());
        }

        [Test]
        public void WithDefaults_factory_resolves_SwitchFilter_via_prototype_lookup() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var prototype = factory.GetItem<SwitchFilter>();
            Assert.That(prototype, Is.Not.Null);
            Assert.That(prototype, Is.InstanceOf<SwitchFilter>());
        }
    }
}
