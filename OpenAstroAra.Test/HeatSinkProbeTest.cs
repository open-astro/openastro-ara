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

    /// <summary>#1185 — the heat-sink read must not cost the status tick a round-trip per pass.</summary>
    [TestFixture]
    public class HeatSinkProbeTest {

        [Test]
        public void Reads_on_the_first_cooler_on_pass_then_every_nth_and_keeps_the_last_value_between() {
            var probe = new HeatSinkProbe();
            var calls = 0;
            double Read() { calls++; return 30 + calls; }

            Assert.That(probe.Read(true, Read), Is.EqualTo(31), "first pass reads");
            for (var i = 1; i < HeatSinkProbe.Every; i++) {
                Assert.That(probe.Read(true, Read), Is.EqualTo(31), $"pass {i} serves the cached value");
            }
            Assert.That(calls, Is.EqualTo(1), "one round-trip in the first window");
            Assert.That(probe.Read(true, Read), Is.EqualTo(32), "the Nth pass reads again");
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public void Cooler_off_means_no_read_and_null() {
            var probe = new HeatSinkProbe();
            var calls = 0;
            Assert.That(probe.Read(false, () => { calls++; return 1; }), Is.Null);
            Assert.That(probe.Read(false, () => { calls++; return 1; }), Is.Null);
            Assert.That(calls, Is.Zero, "no round-trip while the cooler is off");
            Assert.That(probe.Read(true, () => { calls++; return 25; }), Is.EqualTo(25), "cooler on → reads at once");
        }

        [Test]
        public void A_not_implemented_answer_latches_until_reset() {
            var probe = new HeatSinkProbe();
            var calls = 0;
            double Throw() { calls++; throw new ASCOM.NotImplementedException("HeatSinkTemperature"); }

            Assert.That(probe.Read(true, Throw), Is.Null);
            Assert.That(probe.Unsupported, Is.True);
            for (var i = 0; i < 3 * HeatSinkProbe.Every; i++) {
                probe.Read(true, Throw);
            }
            Assert.That(calls, Is.EqualTo(1), "asked once per connection");

            probe.Reset();
            Assert.That(probe.Unsupported, Is.False);
            Assert.That(probe.Read(true, () => 28), Is.EqualTo(28), "a reconnect asks again");
        }

        [Test]
        public void A_transient_failure_reads_as_not_reported_this_pass_only() {
            var probe = new HeatSinkProbe();
            var fail = true;
            double Read() => fail ? throw new InvalidOperationException("blip") : 29;

            Assert.That(probe.Read(true, Read), Is.Null, "the failed pass reports null");
            Assert.That(probe.Unsupported, Is.False, "not a latch");
            fail = false;
            for (var i = 1; i < HeatSinkProbe.Every; i++) {
                probe.Read(true, Read);
            }
            Assert.That(probe.Read(true, Read), Is.EqualTo(29), "the next window reads normally");
        }
    }
}
