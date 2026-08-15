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

    /// <summary>§57.9 — the Target RA/Dec display latch (pure): shown by default, masked after
    /// a Park/Find Home, released by an Ara slew/sync, a reconnect, or the registers changing
    /// under the latch (an external goto/sync) — but NOT by the park motion itself.</summary>
    [TestFixture]
    public class TargetDisplayLatchTest {

        [Test]
        public void Registers_are_shown_by_default() {
            var latch = new TargetDisplayLatch();
            Assert.That(latch.Observe(5.5, 20.0), Is.False);
            Assert.That(latch.Observe(5.5, 20.0), Is.False, "steady state stays shown");
        }

        [Test]
        public void A_park_masks_the_stale_registers_until_a_target_command() {
            var latch = new TargetDisplayLatch();
            Assert.That(latch.Observe(5.5, 20.0), Is.False, "the goto's destination shows");
            latch.NoteNonTargetCommand(); // Park
            Assert.That(latch.Observe(5.5, 20.0), Is.True, "the registers still hold the old goto");
            Assert.That(latch.Observe(5.5, 20.0), Is.True, "and keep holding it — stays masked");
            latch.NoteTargetCommand(); // Ara goto/sync
            Assert.That(latch.Observe(7.0, -10.0), Is.False, "a real target command takes over");
        }

        [Test]
        public void An_external_slew_changing_the_registers_releases_the_latch() {
            var latch = new TargetDisplayLatch();
            latch.NoteNonTargetCommand(); // Park (or Find Home)
            Assert.That(latch.Observe(5.5, 20.0), Is.True, "first tick captures the stale snapshot");
            // Handset/ASIAIR goto writes new registers — no Ara API call happens.
            Assert.That(latch.Observe(12.25, 45.0), Is.False, "changed registers = a new target; show it");
            Assert.That(latch.Observe(12.25, 45.0), Is.False, "and it stays shown");
        }

        [Test]
        public void The_park_motion_itself_does_not_release_the_latch() {
            // The park/home slew reads as an IsSlewing episode (§57.8) but never touches the
            // target registers — many poll ticks of the SAME values must stay masked.
            var latch = new TargetDisplayLatch();
            latch.NoteNonTargetCommand();
            for (var tick = 0; tick < 5; tick++) {
                Assert.That(latch.Observe(5.5, 20.0), Is.True, $"tick {tick} of the park motion");
            }
        }

        [Test]
        public void A_second_park_rearms_against_the_registers_current_value() {
            var latch = new TargetDisplayLatch();
            latch.NoteNonTargetCommand();
            Assert.That(latch.Observe(5.5, 20.0), Is.True);
            Assert.That(latch.Observe(9.0, 0.0), Is.False, "external goto released it");
            latch.NoteNonTargetCommand(); // Find Home after the external goto
            Assert.That(latch.Observe(9.0, 0.0), Is.True, "the new value is now the stale one");
            Assert.That(latch.Observe(5.5, 20.0), Is.False, "and a further change releases again");
        }

        [Test]
        public void Reset_shows_the_registers_again_on_a_fresh_session() {
            var latch = new TargetDisplayLatch();
            latch.NoteNonTargetCommand();
            Assert.That(latch.Observe(5.5, 20.0), Is.True);
            latch.Reset(); // disconnect/reconnect
            Assert.That(latch.Observe(5.5, 20.0), Is.False, "a fresh session trusts the registers");
        }

        [Test]
        public void Null_registers_participate_in_the_change_comparison() {
            // A mount that reports null (target never set / unsupported) then starts reporting a
            // value after an external goto: null -> value is a register change and releases.
            var latch = new TargetDisplayLatch();
            latch.NoteNonTargetCommand();
            Assert.That(latch.Observe(null, null), Is.True, "null snapshot is captured, masked");
            Assert.That(latch.Observe(null, null), Is.True, "still null, still masked");
            Assert.That(latch.Observe(3.0, 15.0), Is.False, "null -> value is a change; show it");
        }
    }
}
