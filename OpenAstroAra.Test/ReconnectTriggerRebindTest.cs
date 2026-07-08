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
using OpenAstroAra.Sequencer.Trigger.Connect;
using OpenAstroAra.Server.Services;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The #746 empty-runner family of bugs, ReconnectTrigger edition: the base
    /// [OnDeserializing] hook clears the ctor-added ConnectEquipment before Newtonsoft
    /// repopulates the runner from JSON, so a WILMA-built node (empty runner) fired zero
    /// items and a NINA export executed an instance the trigger's SelectedDevice/FAILED
    /// check did not point at. [OnDeserialized] now re-binds the runner to hold exactly
    /// the bound instruction.
    /// </summary>
    [TestFixture]
    public class ReconnectTriggerRebindTest {

        [Test]
        public void FactoryRegistersTheReconnectTriggerPrototype() {
            var trigger = HeadlessSequencerFactory.WithDefaults().GetTrigger<ReconnectTrigger>();
            Assert.That(trigger, Is.Not.Null,
                "an unregistered prototype degrades every imported/built ReconnectTrigger to UnknownSequenceTrigger");
        }

        [Test]
        public void DeserializationRebindsTheRunnerToTheBoundInstruction() {
            var trigger = HeadlessSequencerFactory.WithDefaults().GetTrigger<ReconnectTrigger>()!;

            // The Newtonsoft lifecycle: [OnDeserializing] clears, properties populate
            // (SelectedDevice delegates onto the bound instruction), [OnDeserialized] runs.
            trigger.OnDeserializing(default);
            Assert.That(trigger.TriggerRunner.GetItemsSnapshot(), Is.Empty,
                "precondition — the base hook wipes the ctor-added instruction");
            trigger.SelectedDevice = "Focuser";
            trigger.OnDeserialized(default);

            var items = trigger.TriggerRunner.GetItemsSnapshot();
            Assert.That(items, Has.Count.EqualTo(1));
            Assert.That(items.Single(), Is.SameAs(trigger.ConnectEquipmentInstruction),
                "Execute must run the same instance SelectedDevice controls and the FAILED check reads");
            Assert.That(trigger.SelectedDevice, Is.EqualTo("Focuser"), "the JSON's device survives the re-bind");
        }
    }
}
