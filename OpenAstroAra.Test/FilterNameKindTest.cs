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
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Sequencer.SequenceItem.FilterWheel;
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    /// <summary>
    /// <see cref="FilterNameKind"/> is the daemon twin of the client's <c>recognizedKind</c>; the cases
    /// here are the client test's cases (filter_set_state_test.dart), so the two can't drift apart
    /// unnoticed. Plus the <see cref="SwitchFilter"/> resolution it serves.
    /// </summary>
    [TestFixture]
    public class FilterNameKindTest {

        [TestCase("Luminance", "l")]
        [TestCase("L", "l")]
        [TestCase("lum", "l")]
        [TestCase("R", "r")]
        [TestCase("Red", "r")]
        [TestCase("G", "g")]
        [TestCase("B", "b")]
        [TestCase("Ha", "ha")]
        [TestCase("H", "ha")]
        [TestCase("Hα", "ha")]
        [TestCase("halpha", "ha")]
        [TestCase("H-alpha", "ha")]
        [TestCase("Ha7nm", "ha")]
        [TestCase("Antlia Ha 3nm", "ha")]
        [TestCase("OIII", "oiii")]
        [TestCase("O", "oiii")]
        [TestCase("o3", "oiii")]
        [TestCase("O-III", "oiii")]
        [TestCase("SII", "sii")]
        [TestCase("S", "sii")]
        [TestCase("s2", "sii")]
        [TestCase("S-II", "sii")]
        [TestCase("L-eXtreme", "duo")]
        [TestCase("L-eNhance", "tri")]
        [TestCase("OSC", "osc")]
        public void Recognized_covers_the_AlpacaBridge_presets_and_their_aliases(string label, string kind) {
            Assert.That(FilterNameKind.Recognized(label), Is.EqualTo(kind));
        }

        [TestCase("U'")]
        [TestCase("G'")]
        [TestCase("R'")]
        [TestCase("I'")]
        [TestCase("Z'")]
        [TestCase("Clear")]
        [TestCase("CLR")]
        [TestCase("Dark")]
        [TestCase("UV")]
        [TestCase("IR")]
        [TestCase("Filter 1")]
        [TestCase("UHC-S")]
        [TestCase("")]
        [TestCase(null)]
        public void Recognized_leaves_names_with_no_planning_kind_unrecognised(string? label) {
            Assert.That(FilterNameKind.Recognized(label), Is.Null);
        }

        private static List<FilterInfo> WheelNames() => new() {
            new FilterInfo("Luminance", 0, 0),
            new FilterInfo("Red", 0, 1),
            new FilterInfo("Ha", 0, 2),
            new FilterInfo("OIII", 0, 3),
        };

        [Test]
        public void SwitchFilter_resolves_a_plan_name_to_the_wheel_slot_of_the_same_kind() {
            var fromPlan = new FilterInfo("Hα", 0, -1);
            Assert.That(SwitchFilter.Resolve(fromPlan, WheelNames())?.Name, Is.EqualTo("Ha"));
            Assert.That(SwitchFilter.Resolve(new FilterInfo("R", 0, -1), WheelNames())?.Name, Is.EqualTo("Red"));
        }

        [Test]
        public void SwitchFilter_prefers_the_exact_name_then_the_position_over_the_kind() {
            var wheel = WheelNames();
            Assert.That(SwitchFilter.Resolve(new FilterInfo("Ha", 0, 0), wheel)?.Position, Is.EqualTo((short)2), "exact name first");
            Assert.That(SwitchFilter.Resolve(new FilterInfo("H-alpha", 0, 1), wheel)?.Name, Is.EqualTo("Red"), "a saved position beats the kind");
        }

        [Test]
        public void SwitchFilter_leaves_an_ambiguous_or_unknown_name_unresolved() {
            var twoHa = WheelNames();
            twoHa.Add(new FilterInfo("Ha 3nm", 0, 4));
            Assert.That(SwitchFilter.Resolve(new FilterInfo("Hα", 0, -1), twoHa), Is.Null, "two Hα slots: nothing to choose");
            Assert.That(SwitchFilter.Resolve(new FilterInfo("SII", 0, -1), WheelNames()), Is.Null, "no SII slot");
            Assert.That(SwitchFilter.Resolve(new FilterInfo("Filter 7", 0, -1), WheelNames()), Is.Null, "no kind");
            Assert.That(SwitchFilter.Resolve(null, WheelNames()), Is.Null);
        }
    }
}
