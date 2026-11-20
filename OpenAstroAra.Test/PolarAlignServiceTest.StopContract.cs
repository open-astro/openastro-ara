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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2066 — the holes left in the abort contract of #2048 (the stop-contract PR of its plan), on
    /// <see cref="FakeGemMount"/>: a stop to a mount that is not connected goes out once it reconnects, inside its window;
    /// Polar Align counts as its own only the stops tagged <c>polar_align</c>; a takeover that gives up waiting, or an end
    /// that abandons a stuck run, fences the run, so no late stop of it lands on a park. The tests that time the contract
    /// run on its own defaults, not the bench's.
    /// </summary>
    public partial class PolarAlignServiceTest {

        // ── Polar Align's own stops are the ones tagged polar_align ──────────────────────────

        // The old exit of a failed run resends its AbortSlew while the next Start drains it. One resend is refused before the
        // daemon counts it (the mount dropped off), and the user's Stop Mount lands while it is refused: the count moved, but
        // not by Polar Align. Counted as its own, the Stop Mount vanished from the new run's baseline, and that run turned
        // the mount after a Stop Mount pressed after its Start.
        [Test]
        public async Task A_Stop_Mount_racing_a_refused_Polar_Align_abort_still_stops_the_next_run_before_it_moves() {
            var mount = FakeGemMount.CounterweightDownAt(-4.0, 85.0, lstHours: 20.0);
            mount.MoveAxisRefused = true; // the first run fails at its first turn
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            svc.StopResendAfter = TimeSpan.FromSeconds(1); // the old exit resends after the next Start was called
            svc.StopVerifyNotBefore = TimeSpan.FromSeconds(3);
            svc.StopVerifyWindow = TimeSpan.FromSeconds(10);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            while ((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State != "failed") {
                Assert.That(watch.Elapsed, Is.LessThan(SignalTimeout), "the refused move never failed the run");
                await Task.Delay(5).ConfigureAwait(false);
            }
            mount.MoveAxisRefused = false;
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);

            // The new run's stop count is read at this call; the race lands after it, while the Start drains the old run.
            var start = svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None);
            var raced = 0;
            mount.RefuseAbort = reason => {
                if (reason != PolarAlignService.AbortReason || Interlocked.Exchange(ref raced, 1) == 1) {
                    return false;
                }
                _ = mount.AbortSlewAsync(CancellationToken.None); // Stop Mount (user_request), counted
                return true;
            };
            await start.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var mark = Mark(svc);
            var status = await PollStateAsync(svc, mark, "adjusting", "failed").ConfigureAwait(false);

            Assert.That(Volatile.Read(ref raced), Is.EqualTo(1), "the old exit resent its stop after the Start was called");
            Assert.That(status.State, Is.EqualTo("failed"), mount.Describe());
            var error = ws.Events.Skip(mark).First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("Stop Mount"));
            Assert.That(RaTurns(mount), Is.Empty, "no turn after the Stop Mount. " + mount.Describe());
        }
    }
}
