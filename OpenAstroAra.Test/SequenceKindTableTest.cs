#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenAstroAra.Sequencer.Format;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The `kind` to engine class table (#2025 part A): declared once in `OpenAstroAra.Sequencer`,
    /// read by `HeadlessSequencerFactory.WithDefaults`, and equal to the tables in
    /// `docs/specs/sequence-v2.md`.
    /// </summary>
    [TestFixture]
    public class SequenceKindTableTest {

        private static List<(SequenceNodeRole Role, Type Type)> Registered(HeadlessSequencerFactory f) {
            var all = new List<(SequenceNodeRole, Type)>();
            all.AddRange(f.Containers.Select(x => (SequenceNodeRole.Container, x.GetType())));
            all.AddRange(f.Items.Select(x => (SequenceNodeRole.Instruction, x.GetType())));
            all.AddRange(f.Conditions.Select(x => (SequenceNodeRole.Condition, x.GetType())));
            all.AddRange(f.Triggers.Select(x => (SequenceNodeRole.Trigger, x.GetType())));
            return all;
        }

        [Test]
        public void Every_class_the_factory_registers_has_exactly_one_kind() {
            foreach (var (role, type) in Registered(HeadlessSequencerFactory.WithDefaults())) {
                var entries = SequenceKinds.All.Where(e => e.EngineClass == type).ToList();
                Assert.That(entries, Has.Count.EqualTo(1), type.Name + " has exactly one kind");
                Assert.That(entries[0].Role, Is.EqualTo(role), type.Name + " is registered in the list its role names");
            }
        }

        [Test]
        public void Every_kind_builds_its_class_and_none_is_missing_but_the_astrometry_pair() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var registered = Registered(factory).Select(r => r.Type).ToHashSet();
            foreach (var entry in SequenceKinds.All) {
                if (!registered.Contains(entry.EngineClass)) {
                    // A daemon without the astrometry natives skips these two prototypes (DefaultConditions).
                    Assert.That(entry.NeedsAstrometryNatives, Is.True, entry.Kind + " is registered or needs the natives");
                    continue;
                }
                var prototype = factory.Items.Cast<object>().Concat(factory.Conditions).Concat(factory.Containers).Concat(factory.Triggers)
                    .Single(x => x.GetType() == entry.EngineClass);
                var clone = ((ICloneable)prototype).Clone();
                Assert.That(clone.GetType(), Is.EqualTo(entry.EngineClass), entry.Kind + " builds its class");
            }
            Assert.That(SequenceKinds.All.Where(e => e.NeedsAstrometryNatives).Select(e => e.Kind),
                Is.EquivalentTo(NativeKinds));
        }

        [Test]
        public void Every_kind_and_every_class_appears_once_in_the_table() {
            Assert.That(SequenceKinds.All.Select(e => e.Kind), Is.Unique);
            Assert.That(SequenceKinds.All.Select(e => e.EngineClass), Is.Unique);
            Assert.That(SequenceKinds.All.Count(e => e.Role == SequenceNodeRole.Instruction), Is.EqualTo(47));
            Assert.That(SequenceKinds.All.Count(e => e.Role == SequenceNodeRole.Condition), Is.EqualTo(5));
            Assert.That(SequenceKinds.All.Count(e => e.Role == SequenceNodeRole.Container), Is.EqualTo(5));
            Assert.That(SequenceKinds.All.Count(e => e.Role == SequenceNodeRole.Trigger), Is.EqualTo(10));
            Assert.That(SequenceKinds.ForKind("capture")?.EngineClass.Name, Is.EqualTo("TakeExposure"));
            Assert.That(SequenceKinds.ForKind("unknown"), Is.Null, "the placeholder is not a table entry");
            Assert.That(SequenceKinds.ForKind("glover-exposure"), Is.Null, "no engine class exists for it yet");
        }

        [Test]
        public void The_factory_registers_the_table_in_table_order() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            foreach (var role in Enum.GetValues<SequenceNodeRole>()) {
                var inTable = SequenceKinds.All.Where(e => e.Role == role).Select(e => e.EngineClass).ToList();
                var inFactory = Registered(factory).Where(r => r.Role == role).Select(r => r.Type).ToList();
                Assert.That(inFactory, Is.EqualTo(inTable.Where(inFactory.Contains).ToList()), role + " order");
            }
        }

        // Characterization: the class set `WithDefaults` registers at master (7356fefa4938). Green before
        // the change and after it; building the registrations from the table must not add, drop or
        // reorder one. The two astrometry conditions are checked apart because a host without the natives
        // skips them.
        private static readonly string[] NativeKinds = { "loop-while-above-horizon", "loop-while-altitude" };

        private static readonly string[] MasterConditions = { "LoopCondition", "TimeSpanCondition", "TimeCondition" };

        private static readonly string[] MasterContainers = {
            "SequenceRootContainer", "SequentialContainer", "ParallelContainer", "DeepSkyObjectContainer", "SmartExposure",
        };

        private static readonly string[] MasterItems = {
            "Annotation", "WaitForUser", "WaitForTimeSpan", "WaitForTime", "WaitForSunAltitude", "WaitUntilSafe",
            "SetTracking", "UnparkScope", "ParkScope", "FindHome", "SlewScopeToRaDec", "SlewScopeToAltAz",
            "MoveFocuserAbsolute", "MoveFocuserRelative", "MoveFocuserByTemperature", "TakeExposure", "CoolCamera",
            "WarmCamera", "SetUSBLimit", "SetReadoutMode", "DewHeater", "StartGuiding", "StopGuiding", "Dither",
            "MoveRotatorMechanical", "SetSwitchValue", "OpenDomeShutter", "CloseDomeShutter", "ParkDome",
            "FindHomeDome", "SlewDomeAzimuth", "EnableDomeSynchronization", "DisableDomeSynchronization",
            "SynchronizeDome", "SwitchFilter", "RunAutofocus", "CenterAndRotate", "TrackingProbe", "FlatPanelFlats",
            "SkyFlats", "OpenFlatPanelCover", "CloseFlatPanelCover", "ConnectAllEquipment", "ConnectEquipment",
            "DisconnectAllEquipment", "DisconnectEquipment", "SwitchProfile",
        };

        private static readonly string[] MasterTriggers = {
            "MeridianFlipTrigger", "DitherAfterExposures", "AutofocusAfterExposures", "UnguidedExposureGuard",
            "CenterAfterDriftTrigger", "AutofocusAfterTimeTrigger", "AutofocusAfterTemperatureChangeTrigger",
            "AutofocusAfterHFRIncreaseTrigger", "AutofocusAfterFilterChange", "ReconnectTrigger",
        };

        [Test]
        public void WithDefaults_registers_the_same_classes_in_the_same_order_as_master() {
            var f = HeadlessSequencerFactory.WithDefaults();
            Assert.That(f.Containers.Select(x => x.GetType().Name), Is.EqualTo(MasterContainers));
            Assert.That(f.Items.Select(x => x.GetType().Name), Is.EqualTo(MasterItems));
            Assert.That(f.Triggers.Select(x => x.GetType().Name), Is.EqualTo(MasterTriggers));
            var conditions = f.Conditions.Select(x => x.GetType().Name).ToList();
            Assert.That(conditions.Where(c => c is not ("AboveHorizonCondition" or "AltitudeCondition")),
                Is.EqualTo(MasterConditions));
        }

        [Test]
        public void The_spec_kind_tables_equal_the_code_table() {
            var rows = SequenceV2Spec.KindRows();
            var spec = rows.Select(r => (r.Kind, r.ClassName, r.Role)).OrderBy(r => r.Kind, StringComparer.Ordinal).ToList();
            var code = SequenceKinds.All.Select(e => (e.Kind, e.EngineClass.Name, e.Role.ToString())).OrderBy(r => r.Kind, StringComparer.Ordinal).ToList();
            Assert.That(code, Is.EqualTo(spec));
        }
    }
}