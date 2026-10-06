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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1219 — progress-driven checkpoint writes are rate-limited: a burst collapses to
    /// a leading and a trailing write, the freshest state always lands, Flush writes what is pending.</summary>
    [TestFixture]
    public class CoalescingCheckpointWriterTest {

        [Test]
        public void A_burst_inside_the_interval_writes_once_now_and_once_after_the_interval() {
            var now = 1_000L;
            var writes = 0;
            var w = new CoalescingCheckpointWriter(() => writes++, TimeSpan.FromSeconds(1), () => now);
            try {
                for (var i = 0; i < 100; i++) {
                    w.Poke();
                }
                Assert.That(writes, Is.EqualTo(1), "the leading write, then the burst is held");
                // The trailing timer fires in real time; the clock stands still, so the write lands when it does.
                Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref writes) == 2, TimeSpan.FromSeconds(5)), Is.True, "one trailing write");
                Assert.That(w.Writes, Is.EqualTo(2));
            } finally {
                w.Stop();
            }
        }

        [Test]
        public void A_poke_after_the_interval_writes_immediately() {
            var now = 0L;
            var writes = 0;
            var w = new CoalescingCheckpointWriter(() => writes++, TimeSpan.FromSeconds(1), () => now);
            try {
                w.Poke();
                now += 1_500;
                w.Poke();
                Assert.That(writes, Is.EqualTo(2));
            } finally {
                w.Stop();
            }
        }

        [Test]
        public void Flush_writes_a_pending_state_now_and_Stop_ignores_later_pokes() {
            var now = 0L;
            var writes = 0;
            var w = new CoalescingCheckpointWriter(() => writes++, TimeSpan.FromMinutes(5), () => now);
            w.Poke();
            w.Poke(); // pending behind a 5-minute interval
            Assert.That(writes, Is.EqualTo(1));
            w.Flush();
            Assert.That(writes, Is.EqualTo(2), "the run's finally gets the freshest state");
            w.Flush();
            Assert.That(writes, Is.EqualTo(2), "nothing pending, nothing written");
            w.Stop();
            w.Poke();
            Assert.That(writes, Is.EqualTo(2));
        }

        [Test]
        public async Task A_write_that_throws_does_not_poison_the_timer() {
            var calls = 0;
            var w = new CoalescingCheckpointWriter(() => { calls++; if (calls == 1) { throw new InvalidOperationException("disk"); } }, TimeSpan.FromMilliseconds(20));
            try {
                Assert.Throws<InvalidOperationException>(w.Poke);
                await Task.Delay(50);
                w.Poke(); // interval has passed → immediate, must not be swallowed by the first fault
                Assert.That(calls, Is.EqualTo(2));
            } finally {
                w.Stop();
            }
        }
    }
}
