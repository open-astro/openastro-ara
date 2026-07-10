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

    /// <summary>§42.2 item 4 — the rotator angle-drift watch: only daemon-commanded moves are
    /// checked, motion + settle gating, circular-distance tolerance, recommand-once-then-fault
    /// episode protocol shared with <see cref="SwitchReadbackWatch"/>.</summary>
    [TestFixture]
    public class RotatorDriftWatchTest {

        private static readonly DateTimeOffset T0 = new(2026, 7, 10, 5, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan PastSettle = RotatorDriftWatch.DefaultSettleWindow + TimeSpan.FromSeconds(1);
        private const double Tol = RotatorDriftWatch.DefaultToleranceDeg;

        // Shorthand: observe a mechanical-domain read (the domain every test's Command targets
        // unless it says otherwise), device idle.
        private static ReadbackVerdict Mech(RotatorDriftWatch w, double angle, DateTimeOffset t) =>
            w.Observe(mechanicalDeg: angle, skyDeg: null, isMoving: false, Tol, t);

        [Test]
        public void A_rotator_the_daemon_never_moved_is_never_checked() {
            var w = new RotatorDriftWatch();
            for (var i = 0; i < 10; i++) {
                Assert.That(Mech(w, angle: i * 17.0, T0 + TimeSpan.FromSeconds(i * 2)),
                    Is.EqualTo(ReadbackVerdict.Idle),
                    "a user turning the rotator by hand is not a fault");
            }
        }

        [Test]
        public void Reads_while_moving_or_inside_the_settle_window_never_count() {
            var w = new RotatorDriftWatch();
            w.Command(90f, mechanical: true, T0);
            Assert.That(Mech(w, 10, T0 + TimeSpan.FromSeconds(2)), Is.EqualTo(ReadbackVerdict.Settling),
                "inside the settle window — a slow rotator is still en route");
            Assert.That(w.Observe(10, null, isMoving: true, Tol, T0 + PastSettle),
                Is.EqualTo(ReadbackVerdict.Settling),
                "the device says it's still moving — en route is not drift, however long it takes");
            Assert.That(Mech(w, 90.2, T0 + PastSettle), Is.EqualTo(ReadbackVerdict.Idle),
                "settled within tolerance of the commanded angle");
        }

        [Test]
        public void Persistent_drift_recommands_once_then_fires_once() {
            var w = new RotatorDriftWatch();
            w.Command(90f, mechanical: true, T0);
            var t = T0 + PastSettle;
            Assert.That(Mech(w, 85, t), Is.EqualTo(ReadbackVerdict.Degraded));
            Assert.That(Mech(w, 85, t + TimeSpan.FromSeconds(2)), Is.EqualTo(ReadbackVerdict.Degraded));
            // §42.2 — the first exhausted streak asks for ONE re-issued move, not a fault…
            Assert.That(Mech(w, 85, t + TimeSpan.FromSeconds(4)), Is.EqualTo(ReadbackVerdict.Recommand));
            // …and the re-command gets the same fair settle window as the original move.
            Assert.That(Mech(w, 85, t + TimeSpan.FromSeconds(6)), Is.EqualTo(ReadbackVerdict.Settling));
            var t2 = t + TimeSpan.FromSeconds(4) + PastSettle;
            Assert.That(Mech(w, 85, t2), Is.EqualTo(ReadbackVerdict.Degraded));
            Assert.That(Mech(w, 85, t2 + TimeSpan.FromSeconds(2)), Is.EqualTo(ReadbackVerdict.Degraded));
            Assert.That(Mech(w, 85, t2 + TimeSpan.FromSeconds(4)), Is.EqualTo(ReadbackVerdict.Mismatch),
                "still adrift after the one re-command — now it's a fault");
            Assert.That(Mech(w, 85, t2 + TimeSpan.FromSeconds(6)), Is.EqualTo(ReadbackVerdict.Idle),
                "latched — still adrift, already reported");
        }

        [Test]
        public void A_successful_recommand_never_faults() {
            var w = new RotatorDriftWatch();
            w.Command(90f, mechanical: true, T0);
            var t = T0 + PastSettle;
            Mech(w, 85, t);
            Mech(w, 85, t);
            Assert.That(Mech(w, 85, t), Is.EqualTo(ReadbackVerdict.Recommand));
            var t2 = t + PastSettle;
            Assert.That(Mech(w, 89.9, t2), Is.EqualTo(ReadbackVerdict.Idle),
                "the re-issued move landed it — no fault, no notification");
        }

        [Test]
        public void A_single_flapped_read_never_accumulates() {
            var w = new RotatorDriftWatch();
            w.Command(90f, mechanical: true, T0);
            var t = T0 + PastSettle;
            for (var i = 0; i < 5; i++) {
                Assert.That(Mech(w, 85, t), Is.EqualTo(ReadbackVerdict.Degraded));
                Assert.That(Mech(w, 85, t), Is.EqualTo(ReadbackVerdict.Degraded));
                Assert.That(Mech(w, 90, t), Is.EqualTo(ReadbackVerdict.Idle), "a good read clears the streak");
            }
        }

        // Drives a freshly commanded, stubbornly-adrift rotator through the full episode:
        // streak → Recommand → re-armed settle → streak → Mismatch. Returns the time of the fire.
        private static DateTimeOffset DriveToFire(RotatorDriftWatch w, DateTimeOffset commanded) {
            var t = commanded + PastSettle;
            Mech(w, 85, t);
            Mech(w, 85, t);
            Assert.That(Mech(w, 85, t), Is.EqualTo(ReadbackVerdict.Recommand));
            var t2 = t + PastSettle;
            Mech(w, 85, t2);
            Mech(w, 85, t2);
            Assert.That(Mech(w, 85, t2), Is.EqualTo(ReadbackVerdict.Mismatch));
            return t2;
        }

        [Test]
        public void Recovery_after_a_fired_episode_clears_and_requires_a_fresh_move_to_rearm() {
            var w = new RotatorDriftWatch();
            w.Command(90f, mechanical: true, T0);
            var t = DriveToFire(w, T0);
            Assert.That(Mech(w, 90.1, t + TimeSpan.FromSeconds(2)), Is.EqualTo(ReadbackVerdict.Cleared));
            // The record is gone: a rotator oscillating at the boundary can't churn out faults.
            for (var i = 0; i < 6; i++) {
                Assert.That(Mech(w, i % 2 == 0 ? 85 : 90, t + TimeSpan.FromSeconds(4 + i * 2)),
                    Is.EqualTo(ReadbackVerdict.Idle));
            }
            Assert.That(w.Commanded, Is.Null);
        }

        [Test]
        public void A_fresh_command_onto_a_fired_episode_starts_a_fresh_one() {
            var w = new RotatorDriftWatch();
            w.Command(90f, mechanical: true, T0);
            var t = DriveToFire(w, T0);
            w.Command(120f, mechanical: true, t);
            var t2 = DriveToFire(w, t);
            Assert.That(t2, Is.GreaterThan(t), "the second episode fired independently");
        }

        [Test]
        public void The_comparison_uses_the_commanded_angle_domain_only() {
            var w = new RotatorDriftWatch();
            w.Command(90f, mechanical: false, T0); // sky-domain move
            var t = T0 + PastSettle;
            // Mechanical wildly off but sky on target: in position (the offset explains the gap).
            Assert.That(w.Observe(mechanicalDeg: 250, skyDeg: 90.1, isMoving: false, Tol, t),
                Is.EqualTo(ReadbackVerdict.Idle));
            // Sky adrift counts even with mechanical incidentally near the number.
            Assert.That(w.Observe(mechanicalDeg: 90, skyDeg: 84, isMoving: false, Tol, t),
                Is.EqualTo(ReadbackVerdict.Degraded));
        }

        [Test]
        public void An_unreadable_angle_never_accuses() {
            var w = new RotatorDriftWatch();
            w.Command(90f, mechanical: true, T0);
            var t = T0 + PastSettle;
            Mech(w, 85, t);
            Mech(w, 85, t);
            Assert.That(w.Observe(mechanicalDeg: null, skyDeg: 85, isMoving: false, Tol, t),
                Is.EqualTo(ReadbackVerdict.Idle),
                "the commanded domain is unreadable this tick — no basis to escalate the streak");
        }

        [Test]
        public void Reset_forgets_the_expectation() {
            var w = new RotatorDriftWatch();
            w.Command(90f, mechanical: true, T0);
            w.Reset(); // sync / reverse flip / failed move / disconnect
            Assert.That(Mech(w, 5, T0 + PastSettle), Is.EqualTo(ReadbackVerdict.Idle));
            Assert.That(w.Commanded, Is.Null);
        }

        [Test]
        public void Drift_is_measured_as_circular_distance_across_the_wraparound() {
            var w = new RotatorDriftWatch();
            w.Command(359.8f, mechanical: true, T0);
            var t = T0 + PastSettle;
            Assert.That(Mech(w, 0.1, t), Is.EqualTo(ReadbackVerdict.Idle),
                "359.8° commanded, 0.1° read is 0.3° of drift — not 359.7°");
            Assert.That(Mech(w, 1.5, t + TimeSpan.FromSeconds(2)), Is.EqualTo(ReadbackVerdict.Degraded),
                "1.7° across the wrap is genuinely out of the ±0.5° tolerance");
        }

        [TestCase(0, 0, 0)]
        [TestCase(90, 85, 5)]
        [TestCase(359.8, 0.1, 0.3)]
        [TestCase(0, 180, 180)]
        [TestCase(350, 10, 20)]
        [TestCase(10, 350, 20)]
        [TestCase(720.5, 0, 0.5)]
        public void AngularDistance_is_symmetric_shortest_arc(double a, double b, double expected) {
            Assert.That(RotatorDriftWatch.AngularDistanceDeg(a, b), Is.EqualTo(expected).Within(1e-9));
            Assert.That(RotatorDriftWatch.AngularDistanceDeg(b, a), Is.EqualTo(expected).Within(1e-9));
        }
    }
}
