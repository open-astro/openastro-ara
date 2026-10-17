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

    /// <summary>§42.3 second channel — the pure streak classifier behind the <c>degraded</c> fault.</summary>
    [TestFixture]
    public class DeviceHealthTrackerTest {

        [Test]
        public void Three_failed_reads_degrade_once_and_the_first_good_read_recovers() {
            var t = new DeviceHealthTracker();
            Assert.That(t.Observe(false, "EFW_ERROR_REMOVED"), Is.EqualTo(HealthVerdict.Faltering));
            Assert.That(t.Observe(false, "EFW_ERROR_REMOVED"), Is.EqualTo(HealthVerdict.Faltering));
            Assert.That(t.Observe(false, "EFW_ERROR_REMOVED"), Is.EqualTo(HealthVerdict.BecameDegraded), "the third failure is the episode start");
            Assert.That(t.IsDegraded, Is.True);
            Assert.That(t.LastReason, Is.EqualTo("EFW_ERROR_REMOVED"));
            Assert.That(t.Observe(false), Is.EqualTo(HealthVerdict.StillDegraded), "reported once, not per tick");
            Assert.That(t.FailureStreak, Is.EqualTo(4));
            Assert.That(t.Observe(true), Is.EqualTo(HealthVerdict.Recovered));
            Assert.That(t.IsDegraded, Is.False);
            Assert.That(t.LastReason, Is.Null);
            Assert.That(t.Observe(true), Is.EqualTo(HealthVerdict.Healthy));
        }

        [Test]
        public void A_blip_never_degrades_and_a_good_read_clears_the_streak() {
            var t = new DeviceHealthTracker();
            t.Observe(false, "timeout");
            t.Observe(false, "timeout");
            Assert.That(t.Observe(true), Is.EqualTo(HealthVerdict.Healthy));
            Assert.That(t.FailureStreak, Is.Zero);
            t.Observe(false, "timeout");
            t.Observe(false, "timeout");
            Assert.That(t.IsDegraded, Is.False, "two and two is not three in a row");
        }

        [Test]
        public void Reset_starts_a_clean_session_and_a_degraded_episode_never_outlives_it() {
            var t = new DeviceHealthTracker(threshold: 2);
            t.Observe(false, "x");
            Assert.That(t.Observe(false, "x"), Is.EqualTo(HealthVerdict.BecameDegraded));
            t.Reset();
            Assert.That(t.IsDegraded, Is.False);
            Assert.That(t.FailureStreak, Is.Zero);
            Assert.That(t.Observe(true), Is.EqualTo(HealthVerdict.Healthy), "no Recovered after a reset: the episode is gone");
        }

        [Test]
        public void A_blank_reason_keeps_the_last_real_one() {
            var t = new DeviceHealthTracker(threshold: 1);
            t.Observe(false, "ASI_ERROR_GENERAL_ERROR");
            t.Observe(false, "");
            Assert.That(t.LastReason, Is.EqualTo("ASI_ERROR_GENERAL_ERROR"));
        }
    }
}
