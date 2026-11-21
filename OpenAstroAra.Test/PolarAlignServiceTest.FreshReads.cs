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
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2066 (ARA-5, #2048 M18) — Polar Align plans and moves only from a fresh reading of the mount's current session,
    /// through the <see cref="IMountMotionControl"/> seam, on the fake GEM mount. A reading it cannot trust (none, the (0, 0)
    /// placeholder, another mount session, older than 2 s) ends the run with <c>pointing_unknown</c>; a Dec past the pole
    /// with <c>pose_ambiguous</c>; either before anything moves. The daemon's refresh cache is never read mid-move.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>A fine run on the fake whose mount <paramref name="afterThePlan"/> changes once the start was planned and
        /// confirmed, when the run takes the guide camera: before the run's own first reading, where its turns start.</summary>
        private static async Task<(FakeGemMount Mount, WsRecorder Ws, PolarAlignService Svc)> GemRunAfterAsync(Action<FakeGemMount> afterThePlan) {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            var fake = NewFake();
            fake.LeaseGate = active => {
                if (active) {
                    afterThePlan(mount);
                }
                return Task.CompletedTask;
            };
            mount.EventCount = () => ws.Events.Count;
            var svc = NewServiceOn(fake, new TrueSkySolver(mount), mount, ws, TenDegreeStore());
            svc.AxisFastRateDegPerSec = 30.0;
            svc.AxisSlowRateDegPerSec = 10.0;
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None).ConfigureAwait(false);
            return (mount, ws, svc);
        }

        private static void AssertFailedBeforeAnyMotion(FakeGemMount mount, WsRecorder ws) {
            Assert.That(mount.Motions, Is.Empty, "zero motions. " + mount.Describe());
            Assert.That(mount.MotorTravelDeg, Is.Zero);
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        // ── M18: a reading the run cannot trust ──────────────────────────────────────────────

        [Test]
        public async Task M18_a_placeholder_reading_after_the_start_fails_the_run_as_pointing_unknown_before_any_motion() {
            var (mount, ws, svc) = await GemRunAfterAsync(m => m.FreshReadsSentinel = 1).ConfigureAwait(false);
            using var _ = svc;

            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.PointingUnknown));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("(0, 0) placeholder"));
            AssertFailedBeforeAnyMotion(mount, ws);
        }

        [Test]
        public async Task M18_a_reconnect_after_the_start_fails_the_run_as_pointing_unknown_before_any_motion() {
            var (mount, ws, svc) = await GemRunAfterAsync(m => m.Reconnect()).ConfigureAwait(false);
            using var _ = svc;

            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.PointingUnknown));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("reconnected"));
            AssertFailedBeforeAnyMotion(mount, ws);
        }

        [Test]
        public async Task M18_a_failed_fresh_read_with_a_cached_value_fails_the_run_as_pointing_unknown_before_any_motion() {
            // The read fails and the daemon's cache answers the (0, 0) J2000 placeholder: never a pose to turn from.
            var (mount, ws, svc) = await GemRunAfterAsync(m => m.FreshReadsFail = 1).ConfigureAwait(false);
            using var _ = svc;

            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.PointingUnknown));
            AssertFailedBeforeAnyMotion(mount, ws);
        }

        [Test]
        public async Task M18_a_reading_older_than_2_s_fails_the_run_as_pointing_unknown_before_any_motion() {
            var (mount, ws, svc) = await GemRunAfterAsync(m => m.FreshReadsStale = 1).ConfigureAwait(false);
            using var _ = svc;

            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.PointingUnknown));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("3.0 s old"));
            AssertFailedBeforeAnyMotion(mount, ws);
        }

        [Test]
        public async Task A_reconnect_read_mid_turn_stops_the_motor_at_that_reading() {
            // The mount reconnects between the turn's checks and its next read: that reading is of another mount session.
            // The motor is stopped at that reading; it is never renewed on it.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            mount.ReconnectAtMovingRead = 3;

            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.PointingUnknown));
            Assert.That(mount.CommandsAtReconnect, Is.Not.Null, "the reconnect came mid-turn. " + mount.Describe());
            var after = mount.Commands.Skip(mount.CommandsAtReconnect!.Value).ToArray();
            Assert.That(after.Where(c => c.IsMotion), Is.Empty,
                "no motor command after the reading of the new session: " + string.Join(", ", after.Select(c => c.Text)));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(1), "the first turn, cut short");
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        [Test]
        public async Task A_reconnect_while_adjusting_fails_the_run_as_pointing_unknown() {
            // No reading is taken while the user turns the knobs; the run-wide watch still sees the new mount session.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            var turns = RaTurns(mount).Length;

            mount.Reconnect();
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.PointingUnknown));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("reconnected"));
            Assert.That(RaTurns(mount), Has.Length.EqualTo(turns), "nothing moved after the reconnect");
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }

        // A park from outside that lands with a reconnect (a Wi-Fi blip, then the user's Park while nothing turns) is the
        // park's mount: the run ends as stopped from outside, and its exit sends no stop that would abort the park.
        [Test]
        public async Task A_park_with_a_reconnect_while_adjusting_leaves_the_mount_to_the_park() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);
            await AdjustingAsync(svc, mount).ConfigureAwait(false);

            var at = mount.Count;
            mount.ParkFromOutside();
            mount.Reconnect();
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("parked"));
            Assert.That(mount.Since(at + 1), Is.Empty, $"nothing after the park: {string.Join(", ", mount.Since(at))}");
        }

        [Test]
        public async Task A_park_with_a_reconnect_before_a_reading_leaves_the_mount_to_the_park() {
            // The reading after the first turn is of a new mount session, and a park came before it.
            var mount = new FakeStopMount(tracking: true) { ParkAndReconnectBeforeTheNextRestRead = true };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("parked"));
            var log = mount.Log;
            var park = Array.IndexOf(log, "park");
            Assert.That(park, Is.GreaterThan(0), string.Join(", ", log));
            Assert.That(log.Skip(park + 1), Is.Empty, $"nothing after the park: {string.Join(", ", log)}");
        }

        // #2048's abort contract, step 2: a stale or placeholder reading never counts as "stopped". Two of them 1.5 s apart
        // read the same, and the mount really did stop here, but Ara cannot confirm it: the run ends with the cut-power alarm.
        [Test]
        public Task A_stale_reading_never_confirms_the_stop() =>
            StopNotConfirmedOnAsync(m => m.FreshReadsStale = int.MaxValue);

        [Test]
        public Task A_placeholder_reading_never_confirms_the_stop() =>
            StopNotConfirmedOnAsync(m => m.FreshReadsSentinel = int.MaxValue);

        /// <summary>A run in its first turn whose readings from now on are all <paramref name="untrusted"/>: the turn's poll
        /// fails it, and its exit cannot confirm the stop.</summary>
        private static async Task StopNotConfirmedOnAsync(Action<FakeGemMount> untrusted) {
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await mount.FirstMove.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            untrusted(mount);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.PointingUnknown), "the turn's poll");
            Assert.That(error.GetProperty("message").GetString(), Does.Contain(PolarAlignService.CutPowerSentence), "the stop not confirmed");
            Assert.That(mount.IsMoving, Is.False, "the mount did stop; only its readings could not show it. " + mount.Describe());
        }

        // ── a Dec past the pole ──────────────────────────────────────────────────────────────

        [Test]
        public async Task A_Dec_over_90_reading_after_the_start_fails_the_run_as_pose_ambiguous_before_any_motion() {
            // A synced mount whose Dec zero is 6° off reads Dec 91° at a true 85°: which side of the pole is the Dec axis on?
            var (mount, ws, svc) = await GemRunAfterAsync(m => m.ReportedDecOffsetDeg = 6.0).ConfigureAwait(false);
            using var _ = svc;

            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.PoseAmbiguous));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("past the pole"));
            AssertFailedBeforeAnyMotion(mount, ws);
        }

        [Test]
        public async Task A_start_with_no_fresh_reading_is_refused_as_pointing_unknown() {
            var mount = PolledGemAt(-4.0);
            mount.FreshReadsSentinel = 2;
            var ws = new WsRecorder();
            var fake = NewFake();
            using var svc = NewServiceOn(fake, new TrueSkySolver(mount), mount, ws, TenDegreeStore());

            var plan = await Assert.ThrowsAsync<RefusalException>(() => svc.GetStartPlanAsync(null, CancellationToken.None));
            var start = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(
                null, new PolarAlignStartConfirmation(PolarAlignDirections.East, NewOrigin: true), null, CancellationToken.None));

            Assert.That(plan!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignPointingUnknown));
            Assert.That(start!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignPointingUnknown));
            Assert.That(mount.Motions, Is.Empty);
            Assert.That(fake.LeaseCalls, Is.Empty, "refused before the camera is taken");
        }

        // ── the turn's poll ──────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_turn_polls_the_mount_with_the_pose_read_never_the_Slewing_read() {
            // #2067 — every poll reads the position and the pier side (a pier side that changes mid-turn stops it), never
            // Slewing: one driver round trip more per poll that the turn never uses, and a longer poll can skip the last 3°
            // slow zone and stop the motor further past the target.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(RaTurns(mount), Has.Length.EqualTo(2), mount.Describe());
            var reads = mount.FreshReadsWhileMoving;
            Assert.That(reads.Pose, Is.GreaterThan(0), "the turns' polls read the position and the pier side");
            Assert.That(reads.PositionOnly, Is.Zero, "never a position-only poll: it has no pier side to check");
            Assert.That(reads.Full, Is.Zero, "no Slewing read while a turn runs");
        }

        // ── the cache ────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task The_cache_is_never_read_while_the_mount_moves() {
            // Neither the cached position nor the cached state (its pier side): the 2 s refresh is stale or a placeholder.
            var mount = PolledGemAt(-4.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws);

            await FineRunToAdjustingAsync(svc).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(RaTurns(mount), Has.Length.EqualTo(2), mount.Describe());
            Assert.That(mount.CachedReadsWhileMoving, Is.Zero, "every reading of a turn comes from the driver");
        }
    }
}
