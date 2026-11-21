#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    public partial class PolarAlignServiceTest {

        // #2071 — the routine turns the mount against its sidereal time: with the mount's clock not verified it
        // is refused, whatever the user confirmed, before the lease, a Home, a slew or any motor move.
        [Test]
        public async System.Threading.Tasks.Task Start_with_the_mount_clock_not_verified_is_refused_before_anything_moves() {
            var fake = NewFake();
            var mount = NewMount();
            var gate = new Mock<IMountClockGate>();
            gate.Setup(g => g.MountClockMotionRefusal()).Returns("Mount clock not verified: the rig clock has not been synced from the app or a GPS.");
            using var svc = new PolarAlignService(fake, NullLogger<PolarAlignService>.Instance, new ScriptedSolver(), mount.Object,
                MediatorMotionControl.For(mount.Object), NewStore(), new WsRecorder(), log: null, new FakeFrameFetcher(), mountClock: gate.Object);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(PolarAlignDirections.East), null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.MountClockNotVerified));
            Assert.That(fake.LeaseCalls, Is.Empty, "a refused start must not acquire the lease");
            mount.Verify(m => m.FindHome(It.IsAny<System.IProgress<OpenAstroAra.Core.Model.ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            mount.Verify(m => m.SlewToCoordinatesAsync(It.IsAny<OpenAstroAra.Astrometry.Coordinates>(), It.IsAny<CancellationToken>()), Times.Never);
            mount.Verify(m => m.MoveAxis(It.IsAny<OpenAstroAra.Core.Enums.TelescopeAxes>(), It.IsAny<double>()), Times.Never);
        }

        /// <summary>A clock gate the test opens and closes; it has no push in flight, so the waiting answer is the current one.</summary>
        private static Mock<IMountClockGate> SwitchableGate(System.Func<string?> refusal) {
            var gate = new Mock<IMountClockGate>();
            gate.Setup(g => g.MountClockMotionRefusal()).Returns(refusal);
            gate.Setup(g => g.MountClockRefusalAfterPushAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(refusal()));
            return gate;
        }

        // #2087 review — a Check pressed while the clock gate is closed ended the adjust loop (stopping the mount) and only
        // then answered 409. The gate is read while the loop still runs: a refused Check leaves it running, nothing stopped.
        [Test]
        public async Task A_Check_refused_by_the_mount_clock_leaves_the_adjust_loop_running_and_stops_nothing() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            string? closed = null;
            var gate = SwitchableGate(() => closed);
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: TenDegreeStore(), mountClock: gate.Object);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Check, CancellationToken.None).ConfigureAwait(false);
            closed = "Mount clock not verified: the rig clock stepped since the mount's clock was checked. Ara is checking it again.";
            var commands = mount.Log.Length;
            var mark = Mark(svc);

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Check,
                new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.MountClockNotVerified));
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That((status.State, status.Stage), Is.AnyOf(("adjusting", PolarAlignStages.Fine), ("paused", PolarAlignStages.Fine)),
                "the adjust loop is still running");
            Assert.That(ws.Events.Skip(mark).Select(e => e.Type), Has.None.EqualTo((string)WsEventCatalog.PolarAlignStopped));
            Assert.That(mount.Since(commands), Is.Empty, "no stop, fence or move: " + string.Join(", ", mount.Since(commands)));
        }

        // #2087 review — a Start sent just after the app connects waits for the clock push in flight, as a GoTo, Sync or
        // Center does, then starts; with no push in flight a closed gate refuses it at once.
        [Test]
        public async Task A_Start_during_a_mount_clock_push_waits_for_it_and_without_one_is_refused_at_once() {
            var fake = NewFake(answerCaptures: false);
            const string pushing = "Mount clock not verified: Ara is setting and checking the mount's clock. Try again in a moment.";
            string? refusalNow = pushing;
            var push = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new Mock<IMountClockGate>();
            gate.Setup(g => g.MountClockMotionRefusal()).Returns(() => refusalNow);
            gate.Setup(g => g.MountClockRefusalAfterPushAsync(It.IsAny<CancellationToken>())).Returns(() => push.Task);
            var ws = new WsRecorder();
            using var svc = NewService(fake, new ScriptedSolver(), ws: ws, mountClock: gate.Object);
            // #2068 — the app reads the plan before Start, and the plan checks the clock (the pre-flight list): it waits
            // for the push in flight too, or the confirm step is refused before Start could wait.
            var planRead = svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None);
            Assert.That(planRead.IsCompleted, Is.False, "the plan waits for the push in flight");
            refusalNow = null;
            push.SetResult(null);
            var plan = await planRead.WaitAsync(SignalTimeout).ConfigureAwait(false);
            // A second push starts (a host clock step) before the user taps Start.
            refusalNow = pushing;
            push = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var start = svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None);

            Assert.That(start.IsCompleted, Is.False, "the Start waits for the push in flight");
            Assert.That(fake.LeaseCalls, Is.Empty);
            refusalNow = null; // the push set and checked the mount's clock
            push.SetResult(null);
            await start.WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That(PaCalls(fake), Is.Not.Empty, "the Start went ahead and took the lease");
            Assert.That(ws.Events.Select(e => e.Type), Has.Some.EqualTo((string)WsEventCatalog.PolarAlignStarted));

            var closedFake = NewFake(answerCaptures: false);
            const string closed = "Mount clock not verified: the mount's clock has not been checked since it connected.";
            using var closedSvc = NewService(closedFake, new ScriptedSolver(), mountClock: SwitchableGate(() => closed).Object);
            var refused = closedSvc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None);
            Assert.That(refused.IsCompleted, Is.True, "no push in flight: refused at once");
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => refused);
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.MountClockNotVerified));
            Assert.That(closedFake.LeaseCalls, Is.Empty);
        }
    }
}
