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
using System;

namespace OpenAstroAra.Test {

    /// <summary>#2076 — the pure cooler-response watch: a cooler Ara switched on that reads back
    /// off (no 12 V), or reads on at 0 % power far above its set-point, for a minute.</summary>
    [TestFixture]
    public class CoolerResponseWatchTest {

        private static readonly DateTimeOffset T0 = new(2026, 11, 19, 6, 18, 0, TimeSpan.Zero);

        private static DateTimeOffset At(double seconds) => T0 + TimeSpan.FromSeconds(seconds);

        // Rule A reads: CoolerOn off, as the Uranus-C Pro with no 12 V reports it.
        private static CoolerResponseVerdict Off(CoolerResponseWatch w, double s) =>
            w.Observe(coolerOn: false, coolerStateKnown: true, powerPct: null, ccdC: 20, setpointC: -10, At(s));

        private static CoolerResponseVerdict On(CoolerResponseWatch w, double s, double power, double ccd, double setpoint = -10) =>
            w.Observe(coolerOn: true, coolerStateKnown: true, powerPct: power, ccdC: ccd, setpointC: setpoint, At(s));

        [Test]
        public void A_cooler_commanded_on_that_keeps_reading_off_fires_at_one_minute() {
            var w = new CoolerResponseWatch();
            w.Command(true);

            Assert.That(Off(w, 0), Is.EqualTo(CoolerResponseVerdict.Settling));
            Assert.That(Off(w, 59), Is.EqualTo(CoolerResponseVerdict.Settling));
            Assert.That(w.NotResponding, Is.False);
            Assert.That(Off(w, 60), Is.EqualTo(CoolerResponseVerdict.NotResponding));
            Assert.That(w.NotResponding, Is.True);
            Assert.That(w.Symptom, Is.EqualTo(CoolerResponseSymptom.ReadsOff));
        }

        [Test]
        public void The_warning_fires_once_per_episode() {
            var w = new CoolerResponseWatch();
            w.Command(true);
            Off(w, 0);
            Assert.That(Off(w, 60), Is.EqualTo(CoolerResponseVerdict.NotResponding));

            Assert.That(Off(w, 62), Is.EqualTo(CoolerResponseVerdict.Idle), "already reported");
            Assert.That(Off(w, 3600), Is.EqualTo(CoolerResponseVerdict.Idle), "an hour later: still once");
            Assert.That(w.NotResponding, Is.True, "the flag stays up");
        }

        [Test]
        public void A_repeated_cooler_on_command_does_not_restart_the_window() {
            // The sequencer ramp re-sends CoolerOn = true every 15 s.
            var w = new CoolerResponseWatch();
            w.Command(true);
            Off(w, 0);
            w.Command(true);
            Off(w, 30);
            w.Command(true);
            Assert.That(Off(w, 60), Is.EqualTo(CoolerResponseVerdict.NotResponding));
            w.Command(true);
            Assert.That(w.NotResponding, Is.True, "a re-send does not clear a reported episode");
        }

        [Test]
        public void A_cooler_off_command_disarms_and_clears() {
            var w = new CoolerResponseWatch();
            w.Command(true);
            Off(w, 0);
            Off(w, 60);
            Assert.That(w.NotResponding, Is.True);

            w.Command(false);

            Assert.That(w.NotResponding, Is.False);
            Assert.That(Off(w, 61), Is.EqualTo(CoolerResponseVerdict.Idle));
            Assert.That(Off(w, 600), Is.EqualTo(CoolerResponseVerdict.Idle), "off is off: no warning");
        }

        [Test]
        public void An_unknown_CoolerOn_read_neither_fires_nor_clears() {
            var w = new CoolerResponseWatch();
            w.Command(true);
            Off(w, 0);
            Assert.That(w.Observe(false, coolerStateKnown: false, null, null, null, At(30)), Is.EqualTo(CoolerResponseVerdict.Settling));
            Assert.That(Off(w, 60), Is.EqualTo(CoolerResponseVerdict.NotResponding), "the unknown read did not restart the window");

            Assert.That(w.Observe(false, coolerStateKnown: false, null, null, null, At(90)), Is.EqualTo(CoolerResponseVerdict.Idle));
            Assert.That(w.NotResponding, Is.True, "an unknown read does not clear the episode");

            var fresh = new CoolerResponseWatch();
            fresh.Command(true);
            Assert.That(fresh.Observe(false, coolerStateKnown: false, null, null, null, At(0)), Is.EqualTo(CoolerResponseVerdict.Idle));
            Assert.That(fresh.Observe(false, coolerStateKnown: false, null, null, null, At(120)), Is.EqualTo(CoolerResponseVerdict.Idle),
                "unknown reads alone never accuse");
        }

        [Test]
        public void A_known_on_read_restarts_the_off_window() {
            var w = new CoolerResponseWatch();
            w.Command(true);
            Off(w, 0);
            Off(w, 50);
            On(w, 52, power: 40, ccd: 10);
            Assert.That(Off(w, 54), Is.EqualTo(CoolerResponseVerdict.Settling));
            Assert.That(Off(w, 113), Is.EqualTo(CoolerResponseVerdict.Settling), "59 s since the restart");
            Assert.That(Off(w, 114), Is.EqualTo(CoolerResponseVerdict.NotResponding));
        }

        [Test]
        public void A_cooler_that_drops_to_off_while_commanded_on_fires() {
            // 12 V lost mid-session: the cooler ran, then reads off.
            var w = new CoolerResponseWatch();
            w.Command(true);
            for (var s = 0; s <= 600; s += 2) {
                Assert.That(On(w, s, power: 60, ccd: -10), Is.EqualTo(CoolerResponseVerdict.Idle));
            }
            Assert.That(Off(w, 602), Is.EqualTo(CoolerResponseVerdict.Settling));
            Assert.That(Off(w, 662), Is.EqualTo(CoolerResponseVerdict.NotResponding));
        }

        [Test]
        public void A_cooler_on_at_zero_power_far_above_its_setpoint_and_flat_fires() {
            var w = new CoolerResponseWatch();
            w.Command(true);
            Assert.That(On(w, 0, power: 0, ccd: 3), Is.EqualTo(CoolerResponseVerdict.Settling));
            Assert.That(On(w, 59, power: 0, ccd: 3), Is.EqualTo(CoolerResponseVerdict.Settling));
            Assert.That(On(w, 60, power: 0, ccd: 3), Is.EqualTo(CoolerResponseVerdict.NotResponding));
            Assert.That(w.Symptom, Is.EqualTo(CoolerResponseSymptom.NoPower));
        }

        [Test]
        public void A_sensor_that_keeps_closing_on_the_setpoint_does_not_fire() {
            // A driver that reports 0 % while the sensor still descends is cooling.
            var w = new CoolerResponseWatch();
            w.Command(true);
            var ccd = 20.0;
            for (var s = 0; s <= 600; s += 30) {
                Assert.That(On(w, s, power: 0, ccd: ccd), Is.Not.EqualTo(CoolerResponseVerdict.NotResponding), $"at {s} s");
                ccd -= 1;
            }
            Assert.That(w.NotResponding, Is.False);
        }

        [Test]
        public void An_idle_TEC_at_or_near_a_warm_setpoint_does_not_fire() {
            var w = new CoolerResponseWatch();
            w.Command(true);
            for (var s = 0; s <= 600; s += 2) {
                Assert.That(On(w, s, power: 0, ccd: 12, setpoint: 15), Is.EqualTo(CoolerResponseVerdict.Idle), "sensor below the set-point");
            }
            for (var s = 602; s <= 1200; s += 2) {
                Assert.That(On(w, s, power: 0, ccd: 16.5, setpoint: 15), Is.EqualTo(CoolerResponseVerdict.Idle), "within 2 °C");
            }
        }

        [Test]
        public void A_reported_episode_recovers_when_the_cooler_runs() {
            var w = new CoolerResponseWatch();
            w.Command(true);
            Off(w, 0);
            Off(w, 60);
            Assert.That(On(w, 62, power: 0, ccd: 20), Is.EqualTo(CoolerResponseVerdict.Idle), "on, but still no power: not yet");
            Assert.That(w.NotResponding, Is.True);
            Assert.That(On(w, 64, power: 70, ccd: 19.8), Is.EqualTo(CoolerResponseVerdict.Recovered));
            Assert.That(w.NotResponding, Is.False);
            Assert.That(w.Symptom, Is.EqualTo(CoolerResponseSymptom.None));
        }

        [Test]
        public void A_no_power_episode_recovers_when_the_sensor_moves() {
            var w = new CoolerResponseWatch();
            w.Command(true);
            On(w, 0, power: 0, ccd: 3);
            On(w, 60, power: 0, ccd: 3);
            Assert.That(w.NotResponding, Is.True);
            Assert.That(On(w, 62, power: 0, ccd: 2.4), Is.EqualTo(CoolerResponseVerdict.Recovered));
        }

        [Test]
        public void A_cooler_that_was_never_commanded_on_is_never_reported() {
            // Off at connect, or switched on by another client: not this watch's business.
            var w = new CoolerResponseWatch();
            for (var s = 0; s <= 600; s += 2) {
                Assert.That(Off(w, s), Is.EqualTo(CoolerResponseVerdict.Idle));
                Assert.That(On(w, s + 1, power: 0, ccd: 20), Is.EqualTo(CoolerResponseVerdict.Idle));
            }
            Assert.That(w.NotResponding, Is.False);
        }

        [Test]
        public void Reset_forgets_the_command() {
            var w = new CoolerResponseWatch();
            w.Command(true);
            Off(w, 0);
            Off(w, 60);
            w.Reset();
            Assert.That(w.NotResponding, Is.False);
            Assert.That(Off(w, 120), Is.EqualTo(CoolerResponseVerdict.Idle), "a new session is not armed");
        }
    
        [Test]
        public void A_Cool_Camera_step_that_counted_the_minute_first_opens_the_episode_once() {
            // #2076 review — warn, then fail: the step reports before it fails, and the refresh
            // pass that reaches the minute just after does not warn a second time.
            var w = new CoolerResponseWatch();
            Assert.That(w.ReportReadsOff(20), Is.False, "never commanded on: nothing to report");
            w.Command(true);
            Off(w, 0);
            Assert.That(w.ReportReadsOff(20), Is.True);
            Assert.That(w.NotResponding, Is.True);
            Assert.That(w.Symptom, Is.EqualTo(CoolerResponseSymptom.ReadsOff));
            Assert.That(Off(w, 60), Is.EqualTo(CoolerResponseVerdict.Idle), "already reported by the step");
            Assert.That(w.ReportReadsOff(20), Is.False, "a retried step does not warn again");
            Assert.That(On(w, 62, power: 80, ccd: 19), Is.EqualTo(CoolerResponseVerdict.Recovered));
        }
}
}
