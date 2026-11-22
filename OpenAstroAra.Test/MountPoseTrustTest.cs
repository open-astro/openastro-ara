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

        // #2173 — a verdict needs two readings at least ConfirmGap apart: the first never flags.
        private static bool Twice(MountPoseTrust trust, bool atHome, bool slewing, double decDeg, DateTimeOffset at) {
            Assert.That(trust.Observe(atHome, slewing, decDeg, at), Is.False, "one reading never flags");
            return trust.Observe(atHome, slewing, decDeg, at + MountPoseTrust.ConfirmGap);
        }

        [Test]
        public void AtHome_on_the_first_read_after_connect_is_unverified() {
            var trust = Session();
            Assert.That(trust.BlockReason, Is.EqualTo(MountPoseTrust.NotReadYet), "nothing is trusted before the first read");

            Assert.That(trust.Observe(atHome: true, slewing: false, decDeg: 90, T0), Is.False, "one reading never flags");
            Assert.That(trust.BlockReason, Is.EqualTo(MountPoseTrust.NotReadYet), "nor is the mount trusted before the next");
            Assert.That(trust.Observe(atHome: true, slewing: false, decDeg: 90, T0 + MountPoseTrust.ConfirmGap), Is.True);
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

            Assert.That(Twice(trust, atHome: false, slewing: false, decDeg: 90, T0), Is.True);
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
            Twice(trust, true, false, 90, T0);

            Assert.That(trust.Confirm(90), Is.True);
            Assert.That(trust.BlockReason, Is.Null);
            Assert.That(trust.Confirm(90), Is.False, "nothing left to clear");
        }

        [Test]
        public void The_flag_survives_a_new_session_for_the_same_device() {
            var trust = Session();
            Twice(trust, true, false, 90, T0);
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

            Assert.That(Twice(trust, atHome: true, slewing: false, decDeg: 90, T0 + TimeSpan.FromSeconds(90)), Is.True);
            Assert.That(trust.Reason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
        }

        [Test]
        public void A_pose_settled_after_a_slew_is_still_compared_on_reconnect() {
            var trust = Session();
            trust.Observe(false, false, 5, T0);
            trust.Observe(false, true, 20, T0 + TimeSpan.FromSeconds(2));
            trust.Observe(false, false, 40, T0 + TimeSpan.FromSeconds(3) + MountPoseTrust.QuietWindow); // settled
            trust.BeginSession("mount-1", null);

            Assert.That(Twice(trust, false, false, 90, T0 + TimeSpan.FromMinutes(5)), Is.True);
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
            Twice(trust, true, false, 90, T0);
            trust.BeginSession("mount-2", null);

            Assert.That(trust.Observe(false, false, 5, T0), Is.False);
            Assert.That(trust.BlockReason, Is.Null);
        }

        [Test]
        public void At_home_turning_true_with_nothing_commanded_is_unverified() {
            var trust = Session();
            trust.Observe(false, false, 5, T0);

            Assert.That(Twice(trust, atHome: true, slewing: false, decDeg: 90, T0 + TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(trust.Reason, Is.EqualTo(MountPoseTrust.AtHomeWithoutCommand));
        }

        [Test]
        public void A_dec_jump_between_still_ticks_with_nothing_commanded_is_unverified() {
            var trust = Session();
            trust.Observe(false, false, 5, T0);

            Assert.That(Twice(trust, atHome: false, slewing: false, decDeg: 60, T0 + TimeSpan.FromSeconds(2)), Is.True);
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
            Assert.That(Twice(trust, false, false, 50, later), Is.True, "once stopped, a jump is a jump again");
        }

        [Test]
        public void A_change_after_the_quiet_window_is_no_longer_explained() {
            var trust = Session();
            trust.Observe(false, false, 5, T0);
            trust.NoteCommand(T0);

            var later = T0 + MountPoseTrust.QuietWindow + TimeSpan.FromSeconds(1);
            Assert.That(Twice(trust, false, false, 60, later), Is.True);
        }

        // #2173 (rig 2026-11-21, 05:13Z): an AlpacaBridge restart, then one bad read (its placeholder after a failed
        // status poll), then good reads again. The mount never moved: it stays trusted, and so does the next session.
        [Test]
        public void One_bad_read_after_a_bridge_restart_then_good_reads_stays_verified() {
            var trust = Session();
            trust.Observe(false, false, 23.52, T0); // guiding on Merope
            trust.BeginSession("mount-1", null); // the AlpacaBridge restart: a reconnect
            var t = T0 + TimeSpan.FromMinutes(1);
            Assert.That(trust.Observe(false, false, 0.0, t), Is.False, "the first read after the reconnect is bad");
            Assert.That(trust.Observe(false, false, 23.52, t + TimeSpan.FromSeconds(3)), Is.False);
            Assert.That(trust.BlockReason, Is.Null, "the good read decides the session");

            t += TimeSpan.FromMinutes(5); // in session, nothing commanded for minutes
            Assert.That(trust.Observe(false, false, 23.52, t), Is.False);
            Assert.That(trust.Observe(false, false, 0.0, t + TimeSpan.FromSeconds(3)), Is.False, "one bad read");
            for (var i = 2; i < 6; i++) {
                Assert.That(trust.Observe(false, false, 23.52, t + TimeSpan.FromSeconds(3 * i)), Is.False);
            }
            Assert.That(trust.BlockReason, Is.Null);
            Assert.That(trust.PendingVerdict, Is.Null, "the outlier left nothing behind");
        }

        [Test]
        public void A_jump_read_twice_within_the_confirm_gap_is_one_reading() {
            var trust = Session();
            trust.Observe(false, false, 23.52, T0);
            var t = T0 + TimeSpan.FromMinutes(5);
            trust.Observe(false, false, 0.0, t);
            Assert.That(trust.Observe(false, false, 0.0, t + MountPoseTrust.ConfirmGap - TimeSpan.FromMilliseconds(1)), Is.False,
                "a driver's cached status answered twice is no second reading");
            Assert.That(trust.PendingVerdict, Is.EqualTo(MountPoseTrust.PoseJumpInSession));
            Assert.That(trust.Observe(false, false, 0.0, t + MountPoseTrust.ConfirmGap), Is.True, "a later reading agrees: a jump");
        }

        // #2173 — Joey's mount at Home read Dec +90.71°. Folded over the pole it is the same pointing as 89.29°.
        [Test]
        public void A_dec_past_the_pole_is_folded_before_the_jump_check() {
            Assert.That(MountPoseTrust.FoldDec(90.71), Is.EqualTo(89.29).Within(1e-9));
            Assert.That(MountPoseTrust.FoldDec(-93.0), Is.EqualTo(-87.0).Within(1e-9));
            Assert.That(MountPoseTrust.FoldDec(45.0), Is.EqualTo(45.0));

            var trust = Session();
            trust.Observe(false, false, 87.0, T0);
            var t = T0 + TimeSpan.FromMinutes(5);
            Assert.That(trust.Observe(false, false, 93.0, t), Is.False);
            Assert.That(trust.Observe(false, false, 93.0, t + TimeSpan.FromSeconds(3)), Is.False,
                "93° is 87° on the far side of the pole: no jump, though 6° apart unfolded");
            Assert.That(trust.BlockReason, Is.Null);
        }

        // #2173 (rig 2026-11-21, 06:20Z): resumed from Home, the re-center slews to Merope (Dec 24°). The link was slow:
        // no tick read Slewing during the slew, which outlasted the quiet window.
        [Test]
        public void A_slew_that_lands_at_its_goto_target_is_no_jump_however_long_it_took() {
            var trust = Session();
            trust.Observe(false, false, 30, T0);
            trust.Confirm(90.71); // the mount's own home search
            trust.NoteGotoTarget(24.1, T0 + TimeSpan.FromSeconds(1));
            var landed = T0 + MountPoseTrust.QuietWindow + TimeSpan.FromSeconds(30);
            Assert.That(trust.Observe(false, false, 24.0, landed), Is.False);
            Assert.That(trust.Observe(false, false, 24.0, landed + TimeSpan.FromSeconds(3)), Is.False);
            Assert.That(trust.BlockReason, Is.Null);

            var later = landed + TimeSpan.FromMinutes(5);
            Assert.That(Twice(trust, false, false, 60, later), Is.True, "once arrived, the target explains nothing more");
        }

        [Test]
        public void A_commanded_move_in_flight_explains_every_change_until_it_ends() {
            var trust = Session();
            trust.Observe(false, false, 30, T0);
            var move = trust.BeginCommandedMove(T0);
            var t = T0 + MountPoseTrust.QuietWindow + TimeSpan.FromSeconds(10);
            Assert.That(trust.Observe(false, false, 50, t), Is.False);
            Assert.That(trust.Observe(false, false, 70, t + TimeSpan.FromSeconds(3)), Is.False);
            Assert.That(trust.Observe(false, false, 70, t + TimeSpan.FromSeconds(6)), Is.False, "a park or Home still running");
            trust.EndCommandedMove(move, t + TimeSpan.FromSeconds(6), arrived: true);
            Assert.That(Twice(trust, false, false, 72.5, t + TimeSpan.FromSeconds(20)), Is.False, "the quiet window counts from its end");
            Assert.That(trust.BlockReason, Is.Null);

            Assert.That(Twice(trust, false, false, 10, t + TimeSpan.FromMinutes(5)), Is.True, "long after it ended, a jump is a jump");
        }

        [Test]
        public void A_commanded_move_from_an_older_session_does_not_end_a_newer_one() {
            var trust = Session();
            var old = trust.BeginCommandedMove(T0);
            trust.BeginSession("mount-1", null);
            trust.Observe(false, false, 30, T0);
            var current = trust.BeginCommandedMove(T0);
            trust.EndCommandedMove(old, T0, arrived: true); // the old session's park call returns late
            var t = T0 + MountPoseTrust.QuietWindow + TimeSpan.FromSeconds(10);
            trust.Observe(false, false, 50, t);
            Assert.That(trust.Observe(false, false, 50, t + TimeSpan.FromSeconds(3)), Is.False, "the current move still runs");
            trust.EndCommandedMove(current, t, arrived: true);
        }

        // #2173 review — while a jump waits for its second reading, motion is refused: a GoTo in that window would drive
        // by a model that may just have reset.
        [Test]
        public void A_jump_waiting_for_its_second_reading_refuses_motion() {
            var trust = Session();
            trust.Observe(false, false, 30, T0);
            var t = T0 + TimeSpan.FromMinutes(5);

            Assert.That(trust.Observe(false, false, 60, t), Is.False);

            Assert.That(trust.PendingVerdict, Is.EqualTo(MountPoseTrust.PoseJumpInSession));
            Assert.That(trust.BlockReason, Is.EqualTo(MountPoseTrust.NotReadYet));
        }

        // #2173 review — a GoTo (or any Ara command) landing in the confirm window made the next reading "moving", which
        // cleared the pending jump and took the jumped pose as trusted: the jump never flagged. It must flag.
        [Test]
        public void A_command_in_the_confirm_window_does_not_wash_out_a_jump() {
            var trust = Session();
            trust.Observe(false, false, 30, T0);
            var t = T0 + TimeSpan.FromMinutes(5);
            Assert.That(trust.Observe(false, false, 60, t), Is.False);

            var move = trust.BeginCommandedMove(t + TimeSpan.FromSeconds(1));
            Assert.That(trust.Observe(false, false, 60, t + TimeSpan.FromSeconds(1.5)), Is.False, "mid-move: wait");
            trust.EndCommandedMove(move, t + TimeSpan.FromSeconds(2), arrived: true);

            Assert.That(trust.Observe(false, false, 60, t + TimeSpan.FromSeconds(3)), Is.True, "the next still reading agrees: a jump");
            Assert.That(trust.Reason, Is.EqualTo(MountPoseTrust.PoseJumpInSession));
        }

        // #2173 review — a GoTo that failed, was refused or timed out never reached its target: a later jump to that Dec
        // is a jump, not the GoTo's arrival.
        [Test]
        public void A_goto_that_did_not_arrive_excuses_no_later_jump_to_its_target() {
            var trust = Session();
            trust.Observe(false, false, 30, T0);
            var move = trust.BeginCommandedMove(T0, gotoTargetDecDeg: 60);
            trust.EndCommandedMove(move, T0 + TimeSpan.FromSeconds(1), arrived: false);

            var later = T0 + TimeSpan.FromMinutes(5);
            Assert.That(Twice(trust, false, false, 60, later), Is.True);
        }
    }
}
