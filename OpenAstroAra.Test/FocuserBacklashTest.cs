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

        private static int[] Legs(params int[] legs) => legs;

        private static int Dir(int d, AutofocusSettingsDto s, FocusCalibrationDto? c = null) => FocuserBacklash.StepsFor(d, s, c);

        [Test]
        public void An_in_to_out_reversal_overshoots_by_the_out_backlash_then_returns() {
            var s = Settings(backlashOut: 50);
            var plan = FocuserBacklash.Plan(absolute: true, from: 1000, target: 1200, lastDirection: -1, d => Dir(d, s), 0, 10000);

            Assert.That(plan.Legs, Is.EqualTo(Legs(1250, 1200)));
            Assert.That(plan.Direction, Is.EqualTo(1));
        }

        [Test]
        public void An_out_to_in_reversal_overshoots_by_the_in_backlash_then_returns() {
            var s = Settings(backlashIn: 30);
            var plan = FocuserBacklash.Plan(absolute: true, from: 1000, target: 800, lastDirection: 1, d => Dir(d, s), 0, 10000);

            Assert.That(plan.Legs, Is.EqualTo(Legs(770, 800)));
        }

        [Test]
        public void The_same_direction_moves_once() {
            var s = Settings(backlashOut: 50);
            var plan = FocuserBacklash.Plan(true, 1000, 1200, lastDirection: 1, d => Dir(d, s), 0, 10000);

            Assert.That(plan.Legs, Is.EqualTo(Legs(1200)));
        }

        [Test]
        public void A_first_move_with_no_known_direction_moves_once() {
            var s = Settings(backlashOut: 50);
            var plan = FocuserBacklash.Plan(true, 1000, 1200, lastDirection: 0, d => Dir(d, s), 0, 10000);

            Assert.That(plan.Legs, Is.EqualTo(Legs(1200)));
            Assert.That(plan.Direction, Is.EqualTo(1));
        }

        [Test]
        public void Zero_backlash_compensates_nothing() {
            var s = Settings();
            var plan = FocuserBacklash.Plan(true, 1000, 1200, lastDirection: -1, d => Dir(d, s), 0, 10000);

            Assert.That(plan.Legs, Is.EqualTo(Legs(1200)));
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
        public void An_overshoot_past_the_travel_stop_is_skipped() {
            var s = Settings(backlashOut: 50);
            var plan = FocuserBacklash.Plan(true, 9000, 10000, lastDirection: -1, d => Dir(d, s), 0, 10000);

            Assert.That(plan.Legs, Is.EqualTo(Legs(10000)));
        }

        [Test]
        public void A_relative_focuser_overshoots_by_offset() {
            var s = Settings(backlashOut: 50);
            var plan = FocuserBacklash.Plan(absolute: false, from: null, target: 200, lastDirection: -1, d => Dir(d, s), -10000, 10000);

            Assert.That(plan.Legs, Is.EqualTo(Legs(250, -50)));
        }

        [Test]
        public void The_compensating_leg_is_reported_as_backlash_compensating() {
            var idle = new FocuserStateDto("idle", 10, null, false);

            Assert.That(FocuserBacklash.Report(idle, compensating: true).State, Is.EqualTo("backlash_compensating"));
            Assert.That(FocuserBacklash.Report(idle, compensating: false).State, Is.EqualTo("idle"));
        }
    }
}
