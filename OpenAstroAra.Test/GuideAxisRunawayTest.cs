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
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1589 / #1889 — the one-axis runaway rule the polar alignment residual and the Dec runaway watch
    /// share: <see cref="GuideAxisRunaway.Steps"/> pinned steps whose distance never shrinks and ends at
    /// least 3x and 1 px above where the streak began.
    /// </summary>
    [TestFixture]
    public class GuideAxisRunawayTest {

        private const double MaxPulseMs = 2500;

        [Test]
        public void Ten_pinned_growing_steps_ending_three_times_higher_are_a_runaway() {
            var axis = new GuideAxisRunaway();
            // 0.5, 1.0 ... 5.0 px: the tenth step is 10x the first and 4.5 px above it.
            for (var n = 1; n < GuideAxisRunaway.Steps; n++) {
                Assert.That(axis.Step(0.5 * n, MaxPulseMs), Is.False, $"step {n} is not yet a streak of {GuideAxisRunaway.Steps}");
            }
            Assert.That(axis.Step(0.5 * GuideAxisRunaway.Steps, -MaxPulseMs), Is.True, "the pulse sign (North or South) does not matter");
            Assert.That(axis.StartDistance, Is.EqualTo(0.5));
            Assert.That(axis.LastDistance, Is.EqualTo(5.0));
            Assert.That(axis.MaxPulse, Is.EqualTo(MaxPulseMs));
        }

        [Test]
        public void Pulses_that_grow_every_frame_count_as_pinned() {
            // A wrong-way Dec after a flip: each pulse answers a larger error, so every pulse is a new maximum.
            var axis = new GuideAxisRunaway();
            var tripped = false;
            for (var n = 1; n <= GuideAxisRunaway.Steps; n++) {
                tripped = axis.Step(0.5 * n, 200 * n);
            }
            Assert.That(tripped, Is.True);
        }

        [Test]
        public void A_flat_distance_at_the_pulse_limit_is_a_drift_not_a_runaway() {
            var axis = new GuideAxisRunaway();
            for (var n = 1; n <= 3 * GuideAxisRunaway.Steps; n++) {
                Assert.That(axis.Step(2.0, MaxPulseMs), Is.False, $"step {n}: a steady drift never grows");
            }
        }

        [Test]
        public void Growth_under_one_pixel_is_not_a_runaway() {
            // 0.05 to 0.5 px is ten times the start but less than 1 px of growth: centroid noise scale.
            var axis = new GuideAxisRunaway();
            var tripped = false;
            for (var n = 1; n <= 2 * GuideAxisRunaway.Steps; n++) {
                tripped |= axis.Step(0.05 * System.Math.Min(n, GuideAxisRunaway.Steps), MaxPulseMs);
            }
            Assert.That(tripped, Is.False);
        }

        [Test]
        public void A_shrinking_step_restarts_the_streak() {
            var axis = new GuideAxisRunaway();
            for (var n = 1; n < GuideAxisRunaway.Steps; n++) {
                Assert.That(axis.Step(0.5 * n, MaxPulseMs), Is.False);
            }
            Assert.That(axis.Step(4.0, MaxPulseMs), Is.False, "4.5 to 4.0 px shrinks: the streak restarts");
            for (var n = 1; n < GuideAxisRunaway.Steps; n++) {
                Assert.That(axis.Step(4.0 + 0.5 * n, MaxPulseMs), Is.False, $"step {n} of the new streak: under 3x its start");
            }
            Assert.That(axis.StartDistance, Is.EqualTo(4.5), "the new streak began at the first growing step after the shrink");
        }

        [Test]
        public void Steps_with_no_pulse_never_count() {
            // Dec guide mode off (or the other direction only) sends no Dec pulse: never a runaway on that axis.
            var axis = new GuideAxisRunaway();
            for (var n = 1; n <= 3 * GuideAxisRunaway.Steps; n++) {
                Assert.That(axis.Step(0.5 * n, 0), Is.False, $"step {n}");
            }
        }

        [Test]
        public void Clear_forgets_the_streak_and_the_largest_pulse() {
            var axis = new GuideAxisRunaway();
            for (var n = 1; n < GuideAxisRunaway.Steps; n++) {
                axis.Step(0.5 * n, MaxPulseMs);
            }
            axis.Clear();
            Assert.That(axis.MaxPulse, Is.Zero);
            Assert.That(axis.Step(5.0, 1000), Is.False, "one step after Clear is a streak of one, not ten");
            Assert.That(axis.StartDistance, Is.EqualTo(5.0));
        }
    }
}
