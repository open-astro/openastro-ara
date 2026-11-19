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
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Format;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;

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
                    // A daemon without the astrometry natives skips these two prototypes (HeadlessSequencerFactory.Instantiate).
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
        // reorder one. A host without the astrometry natives skips the two astrometry conditions, so the
        // condition list is the five of master or, without the natives, the three.
        private static readonly string[] NativeKinds = { "loop-while-above-horizon", "loop-while-altitude" };

        private static readonly string[] MasterConditions = { "LoopCondition", "TimeSpanCondition", "TimeCondition" };

        private static readonly string[] MasterFiveConditions = {
            "LoopCondition", "TimeSpanCondition", "AboveHorizonCondition", "AltitudeCondition", "TimeCondition",
        };

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
            // With the natives the list is the five of master in master's order (the astrometry pair
            // sits between TimeSpanCondition and TimeCondition); without them, the three.
            var conditions = f.Conditions.Select(x => x.GetType().Name).ToList();
            Assert.That(conditions, Is.EqualTo(conditions.Count == 5 ? MasterFiveConditions : MasterConditions));
        }

        // --- The table-driven build: the native-less skip is the factory's, tested through the build path ---
        // `HeadlessSequencerFactory.Instantiate(entries, constructors)` walks the entries in order and calls
        // the constructor registered for each entry's class. It skips an entry only when the entry has
        // NeedsAstrometryNatives and its constructor faults on a missing native library (the faults
        // HeadlessSequencerFactory.IsMissingNativeLibrary names); every other fault, and an entry with no constructor, propagates.

        private static readonly string[] SkippedResult = { "LoopCondition", "TimeSpanCondition" };

        private static SequenceKindEntry Entry(string kind, Type type, bool needsNatives) =>
            new(kind, type, SequenceNodeRole.Condition, needsNatives);

        [Test]
        public void A_flagged_entry_whose_constructor_faults_on_a_missing_native_library_is_skipped() {
            var entries = new[] {
                Entry("loop-iterations", typeof(LoopCondition), false),
                Entry("loop-while-altitude", typeof(AltitudeCondition), true),
                Entry("loop-for-duration", typeof(TimeSpanCondition), false),
            };
            var constructors = new Dictionary<Type, Func<object>> {
                [typeof(LoopCondition)] = () => new LoopCondition(),
                [typeof(AltitudeCondition)] = () => throw new DllNotFoundException("libsupernovas.so"),
                [typeof(TimeSpanCondition)] = () => new TimeSpanCondition(),
            };
            var built = HeadlessSequencerFactory.Instantiate(entries, constructors);
            Assert.That(built.Select(x => x.GetType().Name), Is.EqualTo(SkippedResult));
        }

        [Test]
        public void An_unflagged_entry_that_faults_the_same_way_fails_the_build() {
            var entries = new[] { Entry("loop-iterations", typeof(LoopCondition), false) };
            var constructors = new Dictionary<Type, Func<object>> {
                [typeof(LoopCondition)] = () => throw new DllNotFoundException("libsupernovas.so"),
            };
            Assert.Throws<DllNotFoundException>(() => HeadlessSequencerFactory.Instantiate(entries, constructors));
        }

        [Test]
        public void A_flagged_entry_that_faults_for_another_reason_fails_the_build() {
            var entries = new[] { Entry("loop-while-altitude", typeof(AltitudeCondition), true) };
            var constructors = new Dictionary<Type, Func<object>> {
                [typeof(AltitudeCondition)] = () => throw new InvalidOperationException("bug"),
            };
            Assert.Throws<InvalidOperationException>(() => HeadlessSequencerFactory.Instantiate(entries, constructors));
        }

        [Test]
        public void A_table_entry_with_no_constructor_fails_the_build() {
            var entries = new[] { Entry("loop-iterations", typeof(LoopCondition), false) };
            Assert.Throws<InvalidOperationException>(() =>
                HeadlessSequencerFactory.Instantiate(entries, new Dictionary<Type, Func<object>>()));
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