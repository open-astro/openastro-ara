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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;

namespace OpenAstroAra.Test {

    /// <summary>#1541 — the wizard's focuser backlash in/out steps are applied on a direction reversal.</summary>
    [TestFixture]
    public class FocuserBacklashTest {

        private static AutofocusSettingsDto Settings(int backlashIn = 0, int backlashOut = 0) =>
            new("hfr_v_curve", 7, 50, 5, 1, "L", true, 2.0, 15.0, 2, true, true,
                BacklashInSteps: backlashIn, BacklashOutSteps: backlashOut);

        private static FocusCalibrationDto Calibration(double? backlashUp) =>
            new(Array.Empty<FocusCalibrationSampleDto>(), DateTimeOffset.UnixEpoch, null, null, BacklashUpSteps: backlashUp);

        private static int Dir(int d, AutofocusSettingsDto s, FocusCalibrationDto? c = null) => FocuserBacklash.StepsFor(d, s, c);

        private static FocuserBacklash.Move Plan(bool absolute, int? from, int target, int last, AutofocusSettingsDto s, int offset = 0, int min = 0, int max = 10000) =>
            FocuserBacklash.Plan(absolute, from, target, last, dir => Dir(dir, s), offset, min, max);

        [Test]
        public void An_in_to_out_reversal_adds_the_out_backlash_in_the_same_direction_and_does_not_come_back() {
            var plan = Plan(true, 1000, 1200, last: -1, Settings(backlashOut: 50));

            Assert.That(plan.HardwareTarget, Is.EqualTo(1250), "one move, 50 steps past the target in the new direction");
            Assert.That(plan.Extra, Is.EqualTo(50));
            Assert.That(plan.Direction, Is.EqualTo(1));
        }

        [Test]
        public void An_out_to_in_reversal_adds_the_in_backlash_below_the_target() {
            var plan = Plan(true, 1000, 800, last: 1, Settings(backlashIn: 30));

            Assert.That(plan.HardwareTarget, Is.EqualTo(770));
            Assert.That(plan.Extra, Is.EqualTo(-30));
        }

        [Test]
        public void The_standing_offset_from_earlier_reversals_is_part_of_the_hardware_target() {
            // The user's target stays 1200; the hardware already sits 20 steps past the logical scale.
            var plan = Plan(true, 1020, 1200, last: -1, Settings(backlashOut: 50), offset: 20);

            Assert.That(plan.HardwareTarget, Is.EqualTo(1270));
            Assert.That(plan.Extra, Is.EqualTo(50));
        }

        [Test]
        public void The_same_direction_moves_without_extra_steps() {
            var plan = Plan(true, 1000, 1200, last: 1, Settings(backlashOut: 50));

            Assert.That(plan.HardwareTarget, Is.EqualTo(1200));
            Assert.That(plan.Extra, Is.EqualTo(0));
        }

        [Test]
        public void A_first_move_with_no_known_direction_adds_nothing() {
            var plan = Plan(true, 1000, 1200, last: 0, Settings(backlashOut: 50));

            Assert.That(plan.Extra, Is.EqualTo(0));
            Assert.That(plan.Direction, Is.EqualTo(1));
        }

        [Test]
        public void Zero_backlash_compensates_nothing() {
            var plan = Plan(true, 1000, 1200, last: -1, Settings());

            Assert.That(plan.Extra, Is.EqualTo(0));
        }

        [Test]
        public void A_measured_backlash_for_the_same_direction_wins_over_the_wizard_value() {
            var s = Settings(backlashIn: 30, backlashOut: 50);
            var measured = Calibration(backlashUp: 12);

            Assert.That(Dir(1, s, measured), Is.EqualTo(12), "up = out");
            Assert.That(Dir(-1, s, measured), Is.EqualTo(30), "the probe measured only the upward direction");
            Assert.That(Dir(1, s, Calibration(backlashUp: null)), Is.EqualTo(50), "an inconclusive probe leaves the wizard value");
        }

        [Test]
        public void Extra_steps_past_the_travel_stop_are_skipped() {
            var plan = Plan(true, 9000, 10000, last: -1, Settings(backlashOut: 50));

            Assert.That(plan.Extra, Is.EqualTo(0));
            Assert.That(plan.HardwareTarget, Is.EqualTo(10000));
        }

        [Test]
        public void A_relative_focuser_adds_the_extra_steps_to_the_offset() {
            var plan = Plan(false, null, 200, last: -1, Settings(backlashOut: 50), min: -10000);

            Assert.That(plan.HardwareTarget, Is.EqualTo(250));
            Assert.That(plan.Extra, Is.EqualTo(50));
        }

        [Test]
        public void The_compensating_leg_is_reported_as_backlash_compensating() {
            var idle = new FocuserStateDto("idle", 10, null, false);

            Assert.That(FocuserBacklash.Report(idle, compensating: true).State, Is.EqualTo("backlash_compensating"));
            Assert.That(FocuserBacklash.Report(idle, compensating: false).State, Is.EqualTo("idle"));
        }
    }
}
