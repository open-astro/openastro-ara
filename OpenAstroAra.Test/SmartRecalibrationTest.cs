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
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PhdEvents;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Recalibrate when something that matters changed, not every run: the stored calibration fails
    /// Ara's verdict, was taken too far from the equator, the instruction or the profile asks. A good
    /// one at a sane declination is reused (PHD2 corrects it for declination itself — 2026-10-16's
    /// calibration from Dec 44 matched cos(Dec) at Dec 56 and Dec 0). Plus the Dec backlash estimate
    /// from the calibration's clearing legs.
    /// </summary>
    [TestFixture]
    public class SmartRecalibrationTest {

        private static GuiderCalibrationDataDto Cal(double dec = 43.7, double ortho = 3.5, double xRate = 1.82, double yRate = 2.50, bool calibrated = true) =>
            new(calibrated, -97.4, -3.9, xRate, yRate, "+", "+", dec, ortho);

        [Test]
        public void A_good_calibration_near_the_equator_is_reused() {
            var (recal, why) = GuiderService.ShouldRecalibrate(false, false, "complete", null, Cal());
            Assert.That(recal, Is.False, why);
            Assert.That(why, Is.Null);
        }

        [Test]
        public void The_instruction_or_the_profile_can_still_force_it() {
            Assert.That(GuiderService.ShouldRecalibrate(true, false, "complete", null, Cal()).Recalibrate, Is.True);
            Assert.That(GuiderService.ShouldRecalibrate(false, true, "complete", null, Cal()).Why, Does.Contain("every session"));
        }

        [Test]
        public void A_calibration_that_fails_the_verdict_is_redone() {
            var (recal, why) = GuiderService.ShouldRecalibrate(false, false, "complete", null, Cal(ortho: 20));
            Assert.That(recal, Is.True);
            Assert.That(why, Does.Contain("from square"));
            var barelyMoved = GuiderService.ShouldRecalibrate(false, false, "complete", null, Cal(dec: 10, yRate: 0.4));
            Assert.That(barelyMoved.Recalibrate, Is.True);
            Assert.That(barelyMoved.Why, Does.Contain("barely moved"));
            var failedRun = GuiderService.ShouldRecalibrate(false, false, "failed", "star lost", Cal());
            Assert.That(failedRun.Recalibrate, Is.True, "the last run failed; the guider still holds an older one");
        }

        [Test]
        public void A_calibration_from_high_declination_is_redone_and_none_at_all_is_left_to_the_guider() {
            var high = GuiderService.ShouldRecalibrate(false, false, "complete", null, Cal(dec: 72));
            Assert.That(high.Recalibrate, Is.True);
            Assert.That(high.Why, Does.Contain("Dec 72"));
            Assert.That(GuiderService.ShouldRecalibrate(false, false, "none", null, null).Recalibrate, Is.False, "no calibration: PHD2 calibrates on its own");
            Assert.That(GuiderService.ShouldRecalibrate(false, false, "none", null, Cal(calibrated: false)).Recalibrate, Is.False);
        }

        [Test]
        public void Dec_backlash_is_the_clearing_legs_times_the_step_length() {
            var steps = new List<PhdEventCalibrating> {
                new() { Direction = "West", Step = 1, Distance = 2 },
                new() { Direction = "Backlash", Step = 1, Distance = 0.1 },
                new() { Direction = "Backlash", Step = 2, Distance = 0.2 },
                new() { Direction = "backlash", Step = 3, Distance = 0.9 },
                new() { Direction = "North", Step = 1, Distance = 5 },
            };
            Assert.That(GuiderLiveService.CountBacklashClearingSteps(steps), Is.EqualTo(3));
            Assert.That(GuiderLiveService.EstimateDecBacklashMs(3, 500), Is.EqualTo(1500));
            Assert.That(GuiderLiveService.EstimateDecBacklashMs(0, 500), Is.EqualTo(0), "no clearing step is a real answer");
            Assert.That(GuiderLiveService.EstimateDecBacklashMs(3, null), Is.Null, "step length unknown");
            Assert.That(GuiderLiveService.EstimateDecBacklashMs(null, 500), Is.Null, "no completed run");
        }
    }
}
