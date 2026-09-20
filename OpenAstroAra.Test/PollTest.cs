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
using OpenAstroAra.TestHarness.Polling;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Unit coverage for the bench's <see cref="Poll"/> helper. The core loop is
    /// exercised indirectly by every bench test that waits on it, but the edges
    /// — the post-deadline re-probe and the cancellation check in front of it —
    /// could be deleted without turning anything else red, which is what this
    /// fixture pins (review note).
    /// </summary>
    [TestFixture]
    public class PollTest {

        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

        [Test]
        public async Task Returns_as_soon_as_the_condition_holds() {
            var probes = 0;
            await Poll.UntilAsync(
                () => ++probes >= 3, TimeSpan.FromSeconds(5), "the counter to reach 3", Tick);

            Assert.That(probes, Is.EqualTo(3));
        }

        [Test]
        public void Throws_TimeoutException_naming_the_condition() {
            var ex = Assert.ThrowsAsync<TimeoutException>(() => Poll.UntilAsync(
                () => false, TimeSpan.FromMilliseconds(100), "the thing that never happens", Tick));

            Assert.That(ex!.Message, Does.Contain("waiting for the thing that never happens"));
        }

        [Test]
        public async Task Re_probes_once_after_the_deadline() {
            // The condition only becomes true after the deadline has passed, so
            // the loop itself never sees it: only the final re-probe can.
            var deadlinePassed = false;
            using var timer = new Timer(_ => deadlinePassed = true, null, TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);

            await Poll.UntilAsync(
                () => deadlinePassed,
                TimeSpan.FromMilliseconds(100),
                "the late condition",
                TimeSpan.FromMilliseconds(400));
        }

        [Test]
        public void An_already_cancelled_token_wins_over_the_re_probe() {
            // With the deadline already reached the loop body never runs, so the
            // post-deadline re-probe is the only thing left — and a cancelled
            // waiter must not be answered with "the condition held after all".
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var probed = 0;

            Assert.ThrowsAsync<OperationCanceledException>(() => Poll.UntilAsync(
                () => { probed++; return true; },
                TimeSpan.Zero,
                "a condition whose waiter was already cancelled",
                Tick,
                cts.Token));

            Assert.That(probed, Is.Zero, "the re-probe must not run for a cancelled waiter");
        }

        [Test]
        public void Rejects_a_blank_description() {
            Assert.ThrowsAsync<ArgumentException>(() => Poll.UntilAsync(
                () => true, TimeSpan.FromSeconds(1), "   "));
        }
    }
}
