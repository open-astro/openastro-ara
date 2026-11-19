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

    /// <summary>#2070 — when the mount's reported position stops being trusted (a false zero after a power
    /// loss) and what makes it trusted again. Pure: no device, no clock but the one passed in.</summary>
    [TestFixture]
    public class MountPoseTrustTest {

        private static readonly DateTimeOffset T0 = new(2026, 11, 19, 7, 0, 0, TimeSpan.Zero);

        private static MountPoseTrust Session(string device = "mount-1", string? restored = null) {
            var trust = new MountPoseTrust();
            trust.BeginSession(device, restored);
            return trust;
        }

        [Test]
        public void AtHome_on_the_first_read_after_connect_is_unverified() {
            var trust = Session();
            Assert.That(trust.BlockReason, Is.EqualTo(MountPoseTrust.NotReadYet), "nothing is trusted before the first read");

            Assert.That(trust.Observe(atHome: true, slewing: false, decDeg: 90, T0), Is.True);
            Assert.That(trust.Reason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
        }

        [Test]
        public void Not_at_home_on_the_first_read_with_no_history_is_trusted() {
            var trust = Session();
            Assert.That(trust.Observe(atHome: false, slewing: false, decDeg: 5, T0), Is.False);
            Assert.That(trust.BlockReason, Is.Null);
        }

        [Test]
        public void A_reconnect_at_a_trusted_home_stays_trusted() {
            var trust = Session();
            trust.Observe(false, false, 5, T0);
            trust.Confirm(90); // the user confirmed Home once
            trust.BeginSession("mount-1", null);

            Assert.That(trust.Observe(atHome: true, slewing: false, decDeg: 90, T0), Is.False);
            Assert.That(trust.BlockReason, Is.Null);
        }

        [Test]
        public void A_dec_jump_against_the_last_trusted_pose_after_a_reconnect_is_unverified() {
            var trust = Session();
            trust.Observe(false, false, 5, T0); // tracking near Orion
            trust.BeginSession("mount-1", null);

            Assert.That(trust.Observe(atHome: false, slewing: false, decDeg: 90, T0), Is.True);
            Assert.That(trust.Reason, Is.EqualTo(MountPoseTrust.PoseJumpOnReconnect));
        }

        [Test]
        public void A_slewing_first_read_does_not_flag_at_home() {
            var trust = Session();
            Assert.That(trust.Observe(atHome: true, slewing: true, decDeg: 80, T0), Is.False);
            Assert.That(trust.BlockReason, Is.Null);
        }

        [Test]
        public void A_failed_position_read_decides_nothing() {
            var trust = Session();
            Assert.That(trust.Observe(atHome: true, slewing: false, decDeg: null, T0), Is.False);
            Assert.That(trust.BlockReason, Is.EqualTo(MountPoseTrust.NotReadYet));
        }

        [Test]
        public void Confirm_clears_the_flag() {
            var trust = Session();
            trust.Observe(true, false, 90, T0);

            Assert.That(trust.Confirm(90), Is.True);
            Assert.That(trust.BlockReason, Is.Null);
            Assert.That(trust.Confirm(90), Is.False, "nothing left to clear");
        }

        [Test]
        public void The_flag_survives_a_new_session_for_the_same_device() {
            var trust = Session();
            trust.Observe(true, false, 90, T0);
            trust.BeginSession("mount-1", null);

            Assert.That(trust.Observe(atHome: false, slewing: false, decDeg: 5, T0), Is.False);
            Assert.That(trust.Reason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect), "only a confirm clears it");
        }

        [Test]
        public void A_new_session_rearms_the_first_read_but_keeps_the_last_pose() {
            var trust = Session();
            trust.Observe(false, false, 5, T0);
            trust.BeginSession("mount-1", null);
            Assert.That(trust.BlockReason, Is.EqualTo(MountPoseTrust.NotReadYet));

            Assert.That(trust.Observe(false, false, 5.5, T0), Is.False, "the same pose again is trusted");
            Assert.That(trust.BlockReason, Is.Null);
        }

        // A link trip in the middle of a GoTo: the mount finishes the slew while Ara reconnects. The pose
        // before the slew says nothing about where it is now, so the finished slew is no power loss.
        [Test]
        public void A_session_that_ended_mid_slew_leaves_no_pose_to_compare() {
            var trust = Session();
            trust.Observe(false, false, 5, T0);
            trust.NoteCommand(T0 + TimeSpan.FromSeconds(1));
            trust.Observe(false, true, 20, T0 + TimeSpan.FromSeconds(2)); // the trip lands mid-slew
            trust.BeginSession("mount-1", null);

            Assert.That(trust.Observe(false, false, 60, T0 + TimeSpan.FromSeconds(90)), Is.False);
            Assert.That(trust.BlockReason, Is.Null);
        }

        [Test]
        public void A_session_that_ended_mid_move_away_from_a_trusted_home_keeps_no_home_exception() {
            var trust = Session();
            trust.Observe(false, false, 5, T0);
            trust.Confirm(90);
            trust.NoteCommand(T0 + TimeSpan.FromSeconds(1)); // a GoTo away from Home, then the trip
            trust.BeginSession("mount-1", null);

            Assert.That(trust.Observe(atHome: true, slewing: false, decDeg: 90, T0 + TimeSpan.FromSeconds(90)), Is.True);
            Assert.That(trust.Reason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
        }

        [Test]
        public void A_pose_settled_after_a_slew_is_still_compared_on_reconnect() {
            var trust = Session();
            trust.Observe(false, false, 5, T0);
            trust.Observe(false, true, 20, T0 + TimeSpan.FromSeconds(2));
            trust.Observe(false, false, 40, T0 + TimeSpan.FromSeconds(3) + MountPoseTrust.QuietWindow); // settled
            trust.BeginSession("mount-1", null);

            Assert.That(trust.Observe(false, false, 90, T0 + TimeSpan.FromMinutes(5)), Is.True);
            Assert.That(trust.Reason, Is.EqualTo(MountPoseTrust.PoseJumpOnReconnect));
        }

        [Test]
        public void A_restored_flag_holds_for_its_device() {
            var trust = Session(restored: MountPoseTrust.AtHomeOnConnect);
            trust.Observe(false, false, 5, T0);
            Assert.That(trust.Reason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect), "a daemon restart keeps the flag");
        }

        [Test]
        public void A_different_device_starts_clean() {
            var trust = Session();
            trust.Observe(true, false, 90, T0);
            trust.BeginSession("mount-2", null);

            Assert.That(trust.Observe(false, false, 5, T0), Is.False);
            Assert.That(trust.BlockReason, Is.Null);
        }

        [Test]
        public void At_home_turning_true_with_nothing_commanded_is_unverified() {
            var trust = Session();
            trust.Observe(false, false, 5, T0);

            Assert.That(trust.Observe(atHome: true, slewing: false, decDeg: 90, T0 + TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(trust.Reason, Is.EqualTo(MountPoseTrust.AtHomeWithoutCommand));
        }

        [Test]
        public void A_dec_jump_between_still_ticks_with_nothing_commanded_is_unverified() {
            var trust = Session();
            trust.Observe(false, false, 5, T0);

            Assert.That(trust.Observe(atHome: false, slewing: false, decDeg: 60, T0 + TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(trust.Reason, Is.EqualTo(MountPoseTrust.PoseJumpInSession));
        }

        [Test]
        public void A_commanded_move_or_a_slewing_tick_explains_the_change() {
            var commanded = Session();
            commanded.Observe(false, false, 5, T0);
            commanded.NoteCommand(T0 + TimeSpan.FromSeconds(1));
            Assert.That(commanded.Observe(true, false, 90, T0 + TimeSpan.FromSeconds(3)), Is.False, "an Ara Home or GoTo");

            var slewed = Session();
            slewed.Observe(false, false, 5, T0);
            slewed.Observe(false, true, 40, T0 + TimeSpan.FromSeconds(2));
            Assert.That(slewed.Observe(false, false, 60, T0 + TimeSpan.FromSeconds(4)), Is.False, "a move the mount reported");
            Assert.That(slewed.BlockReason, Is.Null);
        }

        [Test]
        public void An_axis_move_explains_changes_until_it_stops_even_with_no_slewing_reported() {
            var trust = Session();
            trust.Observe(false, false, 5, T0);
            trust.NoteAxisMove(1, moving: true, T0);

            // A long pad press on a driver that never reports Slewing during MoveAxis.
            var t = T0;
            for (var dec = 8.0; dec <= 80.0; dec += 3.0) {
                t += TimeSpan.FromSeconds(2);
                trust.Observe(false, false, dec, t + MountPoseTrust.QuietWindow);
            }
            Assert.That(trust.BlockReason, Is.Null, "Ara's own move is no power blip");

            trust.NoteAxisMove(1, moving: false, t);
            var later = t + MountPoseTrust.QuietWindow + MountPoseTrust.QuietWindow;
            Assert.That(trust.Observe(false, false, 50, later), Is.True, "once stopped, a jump is a jump again");
        }

        [Test]
        public void A_change_after_the_quiet_window_is_no_longer_explained() {
            var trust = Session();
            trust.Observe(false, false, 5, T0);
            trust.NoteCommand(T0);

            var later = T0 + MountPoseTrust.QuietWindow + TimeSpan.FromSeconds(1);
            Assert.That(trust.Observe(false, false, 60, later), Is.True);
        }
    }
}
