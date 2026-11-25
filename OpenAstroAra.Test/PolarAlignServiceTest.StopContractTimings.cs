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
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2048's abort contract (ARA-1, its non-red cases) on <see cref="FakeGemMount"/>: a stop the mount ignores is resent
    /// until it stops (abort_ignored=2); a Slewing that reads false early through a 1 s driver cache is not taken for a
    /// stop (M08); and the stop goes out at once and is confirmed through 1 s GETs (M06). The timed ones run on the
    /// contract's own defaults (<see cref="UseTheContractsStopTimings"/>).
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>abort_ignored=2: the first two AbortSlews are ignored, the third stops the mount.</summary>
        private static readonly bool[] IgnoredTwiceThenHonoured = [false, false, true];

        [Test]
        public async Task Stop_retries_until_stopped() {
            var mount = GemAt(-4.0, 85.0, lstHours: 20.0);
            mount.AbortIgnored = 2; // abort_ignored=2: the Stop's first AbortSlew and the exit's first are ignored
            mount.MoveAxisStopIgnored = int.MaxValue; // only an AbortSlew stops this mount
            var ws = new WsRecorder();
            using var svc = await InFirstGemTurnAsync(mount, ws).ConfigureAwait(false);

            var at = mount.Now;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var aborts = AbortsSince(mount, at);
            Assert.That(aborts, Has.Length.GreaterThanOrEqualTo(3), mount.Describe());
            Assert.That(aborts.Take(3).Select(a => a.Honoured), Is.EqualTo(IgnoredTwiceThenHonoured), "resent until the mount took it");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero, "confirmed: no mount_did_not_stop");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.EqualTo(1));
            mount.AssertNoMotionSince(aborts[2].At);
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignStopped), CounterweightLimitDeg);
        }

        // M08: Slewing reads false while the mount still turns, and the driver answers position and Slewing from a 1 s cache
        // (AlpacaBridge iOptron). Two reads inside one cache window look still: the 1.5 s between the two reads, and the 2 s
        // after the last send, keep that from counting as stopped, and the stop is resent while the mount still turns.
        [Test]
        public async Task M08_Slewing_false_early_through_a_1s_cache_is_not_confirmed_and_the_stop_is_resent() {
            var mount = GemAt(-4.0, 85.0, lstHours: 20.0);
            mount.DriverCache = TimeSpan.FromSeconds(1);
            mount.SlewingFalseAfter = TimeSpan.FromSeconds(0.5);
            mount.AbortIgnored = 2;
            mount.MoveAxisStopIgnored = int.MaxValue;
            var ws = new WsRecorder();
            using var svc = await InFirstGemTurnAsync(mount, ws, UseTheContractsStopTimings).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(0.6)).ConfigureAwait(false); // Slewing lies from here on

            var at = mount.Now;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var aborts = AbortsSince(mount, at);
            Assert.That(aborts.Take(3).Select(a => a.Honoured), Is.EqualTo(IgnoredTwiceThenHonoured), "resent while the mount still turned. " + mount.Describe());
            Assert.That(aborts[2].At - aborts[1].At, Is.GreaterThanOrEqualTo(svc.StopResendAfter - TimeSpan.FromMilliseconds(50)),
                "the exit's resend came after its first round was not confirmed");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero, "confirmed once the mount really stopped. " + mount.Describe());
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.EqualTo(1));
            Assert.That(RaTurns(mount).Single().EndedAt, Is.EqualTo(aborts[2].At), "the turn ended at the honoured AbortSlew");
            mount.AssertEndedStopped(tracking: false);
        }

        // M06's slow variant: every GET takes 1 s. The stop goes out at once (it reads nothing first), and the confirmation
        // still finds two still reads inside its window.
        [Test]
        public async Task M06_Stop_mid_turn_with_1s_slow_GETs_goes_out_within_1s_and_is_confirmed() {
            var mount = GemAt(-4.0, 85.0, lstHours: 20.0);
            mount.SlowReads = TimeSpan.FromSeconds(1);
            var ws = new WsRecorder();
            using var svc = await InFirstGemTurnAsync(mount, ws, UseTheContractsStopTimings).ConfigureAwait(false);

            var at = mount.Now;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var abort = AbortsSince(mount, at).FirstOrDefault();
            Assert.That(abort, Is.Not.Null, mount.Describe());
            Assert.That(abort!.At - at, Is.LessThan(TimeSpan.FromSeconds(1)), "the stop went out within 1 s");
            Assert.That((abort.Reason, abort.Honoured), Is.EqualTo(("polar_align", true)));
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero, "confirmed through 1 s GETs: no mount_did_not_stop");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.EqualTo(1));
            Assert.That(Math.Abs(RaTurns(mount).Single().TrueDeltaHaDeg), Is.LessThan(5.0), "the 30° turn was cut short");
            mount.AssertNoMotionSince(abort.At);
            mount.AssertEndedStopped(tracking: false);
        }
    }
}
