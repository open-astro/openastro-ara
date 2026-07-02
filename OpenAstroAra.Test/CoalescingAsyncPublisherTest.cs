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

    /// <summary>
    /// The §60.9 progress back-pressure pump: single-flight (never two publishes in
    /// flight), trailing-coalesce (a burst during one publish collapses into exactly
    /// one follow-up), no lost poke (every poke is followed by a publish that started
    /// at-or-after it), and survival of a throwing delegate.
    /// </summary>
    [TestFixture]
    public class CoalescingAsyncPublisherTest {

        [Test]
        public async Task A_single_poke_publishes_once_immediately() {
            var count = 0;
            using var published = new SemaphoreSlim(0);
            var pump = new CoalescingAsyncPublisher(() => {
                Interlocked.Increment(ref count);
                published.Release();
                return Task.CompletedTask;
            });

            pump.Poke();
            Assert.That(await published.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            // Give any (wrong) extra publish a moment to show up.
            await Task.Delay(50);
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public async Task A_burst_while_publishing_collapses_to_one_trailing_publish() {
            var count = 0;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var firstStarted = new SemaphoreSlim(0);
            using var allDone = new SemaphoreSlim(0);
            var pump = new CoalescingAsyncPublisher(async () => {
                var n = Interlocked.Increment(ref count);
                if (n == 1) {
                    firstStarted.Release();
                    await gate.Task; // hold the FIRST publish in flight
                } else {
                    allDone.Release();
                }
            });

            pump.Poke();
            Assert.That(await firstStarted.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            // A burst of 25 pokes lands while the first publish is in flight…
            for (var i = 0; i < 25; i++) {
                pump.Poke();
            }
            gate.SetResult();
            // …and must collapse into exactly ONE trailing publish.
            Assert.That(await allDone.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            await Task.Delay(100);
            Assert.That(count, Is.EqualTo(2),
                "the burst coalesces: one in-flight + one trailing, never 26");
        }

        [Test]
        public async Task No_poke_is_lost_under_concurrency() {
            // Hammer Poke from many threads while the publish yields; every poke must be
            // covered by a publish that STARTED at-or-after it. Detect coverage by
            // checking the pump always quiesces with a publish after the final poke:
            // if the last poke's pending flag were ever dropped, `count` would stop
            // rising and the final assertion below would fail.
            var count = 0;
            var pump = new CoalescingAsyncPublisher(async () => {
                Interlocked.Increment(ref count);
                await Task.Yield();
            });

            var tasks = new Task[8];
            for (var t = 0; t < tasks.Length; t++) {
                tasks[t] = Task.Run(() => {
                    for (var i = 0; i < 500; i++) {
                        pump.Poke();
                    }
                });
            }
            await Task.WhenAll(tasks);
            var before = Volatile.Read(ref count);
            pump.Poke(); // the strictly-last poke — must trigger a publish at-or-after it

            // Quiesce: wait until the count settles above the pre-final-poke value.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Volatile.Read(ref count) <= before && DateTime.UtcNow < deadline) {
                await Task.Delay(10);
            }
            Assert.That(Volatile.Read(ref count), Is.GreaterThan(before),
                "the final poke must be followed by a publish — no lost tick");
            Assert.That(Volatile.Read(ref count), Is.LessThanOrEqualTo(4002),
                "publishes are bounded by pokes (sanity, never amplification)");
        }

        [Test]
        public async Task A_throwing_delegate_does_not_kill_the_pump() {
            var attempts = 0;
            using var secondDone = new SemaphoreSlim(0);
            var pump = new CoalescingAsyncPublisher(() => {
                var n = Interlocked.Increment(ref attempts);
                if (n == 1) {
                    throw new InvalidOperationException("transport hiccup");
                }
                secondDone.Release();
                return Task.CompletedTask;
            });

            pump.Poke();
            // Let the throwing publish run + release the pump.
            await Task.Delay(50);
            pump.Poke();
            Assert.That(await secondDone.WaitAsync(TimeSpan.FromSeconds(5)), Is.True,
                "the next poke still publishes after a delegate throw");
        }

        [Test]
        public async Task SealAndDrain_waits_out_the_inflight_and_trailing_publishes_then_blocks_new_pokes() {
            var count = 0;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var firstStarted = new SemaphoreSlim(0);
            var pump = new CoalescingAsyncPublisher(async () => {
                var n = Interlocked.Increment(ref count);
                if (n == 1) {
                    firstStarted.Release();
                    await gate.Task; // hold the first publish
                }
            });

            pump.Poke();
            Assert.That(await firstStarted.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
            pump.Poke(); // a trailing publish is now owed

            var drain = pump.SealAndDrainAsync();
            Assert.That(drain.IsCompleted, Is.False,
                "drain must wait for the held publish + its trailing follow-up");
            gate.SetResult();
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(count, Is.EqualTo(2), "in-flight + owed trailing publish both completed");

            // r1/#648: a LATE poke (a queued Progress<T> callback landing after the
            // run ended) must be a no-op — nothing may publish past the seal.
            pump.Poke();
            await Task.Delay(100);
            Assert.That(count, Is.EqualTo(2), "sealed: the late poke published nothing");
        }

        [Test]
        public void A_null_delegate_throws_up_front() {
            Assert.Throws<ArgumentNullException>(() => _ = new CoalescingAsyncPublisher(null!));
        }
    }
}
