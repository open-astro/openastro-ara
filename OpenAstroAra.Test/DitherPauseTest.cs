#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Guider;
using OpenAstroAra.Sequencer.Trigger.Guider;
using OpenAstroAra.Sequencer.Utility;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The Dither instruction: a settle timeout or failure only logs a warning and the next frame starts (a
    /// settle timeout is never a reason to pause, 2026-11-15, #1894); a refused dither parks the run awaiting
    /// the user, and Resume dithers again.
    /// </summary>
    [TestFixture]
    public class DitherPauseTest {
        private static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>();

        private sealed class SyncProgress(Action<ApplicationStatus> report) : IProgress<ApplicationStatus> {
            public void Report(ApplicationStatus value) => report(value);
        }

        /// <summary>Polls for the pause request instead of sleeping a fixed time (a loaded CI runner
        /// makes a fixed delay flaky).</summary>
        private static async Task<bool> PauseRequested(PauseGate gate) {
            for (var i = 0; i < 100 && !gate.IsPauseRequested; i++) {
                await Task.Delay(20);
            }
            return gate.IsPauseRequested;
        }

        /// <summary>A guider whose successive Dither calls play out the given verdicts: null = settled,
        /// "refused" = the guider was not guiding (Dither returns false), anything else = a settle error.</summary>
        private static (Mock<IGuiderMediator> Guider, List<int> Calls) Guider(bool connected, params string?[] verdicts) {
            var guider = new Mock<IGuiderMediator>();
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = connected });
            var calls = new List<int>();
            string? last = null;
            guider.Setup(g => g.Dither(It.IsAny<CancellationToken>())).Returns(() => {
                var verdict = calls.Count < verdicts.Length ? verdicts[calls.Count] : null;
                calls.Add(calls.Count + 1);
                last = verdict;
                // "refused" = not guiding; "refused: …" = the guider rejected the dither RPC with that text.
                return Task.FromResult(!(verdict?.StartsWith("refused", StringComparison.Ordinal) ?? false));
            });
            guider.SetupGet(g => g.LastDitherError).Returns(() => last);
            return (guider, calls);
        }

        private static (Dither Item, PauseGate Gate) Rig(IGuiderMediator guider) {
            var gate = new PauseGate();
            var root = new SequenceRootContainer { PauseGate = gate };
            var item = new Dither(guider, Mock.Of<IProfileService>());
            item.AttachNewParent(root);
            return (item, gate);
        }

        [Test]
        public async Task A_settled_dither_neither_retries_nor_pauses() {
            var (guider, calls) = Guider(connected: true, new string?[] { null });
            var (item, gate) = Rig(guider.Object);

            await item.Execute(NoProgress, CancellationToken.None);

            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(gate.IsPauseRequested, Is.False);
        }

        [Test]
        public async Task A_settle_timeout_never_pauses_the_run_the_next_frame_starts() {
            var (guider, calls) = Guider(connected: true, "timed-out waiting for guider to settle", null);
            var (item, gate) = Rig(guider.Object);

            await item.Execute(NoProgress, CancellationToken.None);

            Assert.That(gate.IsPauseRequested, Is.False, "a settle timeout is never a reason to pause");
            Assert.That(calls, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task A_settle_timeout_sends_exactly_one_dither() {
            var (guider, calls) = Guider(connected: true, "timed-out waiting for guider to settle", "timed-out waiting for guider to settle");
            var (item, _) = Rig(guider.Object);

            // Bounded: the old code parks on the gate after the second failure and would hang, not fail.
            await item.Execute(NoProgress, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(calls, Has.Count.EqualTo(1), "no extra dither after a failed settle");
        }

        [Test]
        public async Task A_settle_timeout_reports_a_status_and_returns() {
            var (guider, _) = Guider(connected: true, "timed-out waiting for guider to settle");
            var (item, _) = Rig(guider.Object);
            string? status = null;

            await item.Execute(new SyncProgress(s => status = s.Status), CancellationToken.None);

            Assert.That(status, Does.Contain("carrying on"));
            Assert.That(status, Does.Contain("timed-out waiting for guider to settle"));
        }

        [Test]
        public async Task Successive_settle_timeouts_never_pause_the_run() {
            var (guider, calls) = Guider(connected: true, "timed-out waiting for guider to settle", "settle timed out — no SettleDone from the guider");
            var (item, gate) = Rig(guider.Object);

            await item.Execute(NoProgress, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await item.Execute(NoProgress, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(gate.IsPauseRequested, Is.False);
            Assert.That(calls, Has.Count.EqualTo(2), "one dither per Execute");
        }

        [Test]
        public async Task A_refused_dither_restarts_guiding_and_dithers_again_when_that_works() {
            // Rig night 2026-10-18: a guider stopped by a Planning GoTo parked the run; like an ASIAIR,
            // Ara now restarts guiding itself (stored calibration, auto-selected star) and carries on.
            var (guider, calls) = Guider(connected: true, "refused", null);
            var starts = 0;
            guider.Setup(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { starts++; return true; });
            var (item, gate) = Rig(guider.Object);
            string? status = null;

            await item.Execute(new SyncProgress(s => status = s.Status), CancellationToken.None);

            Assert.That(starts, Is.EqualTo(1), "guiding restarted once");
            Assert.That(calls, Has.Count.EqualTo(2), "the dither after the restart settled, so the frame may start");
            Assert.That(gate.IsPauseRequested, Is.False);
        }

        [Test]
        public async Task A_refused_dither_parks_the_run_when_restarting_guiding_fails_and_resume_tries_again() {
            // Not guiding is not a settle problem: a second dither would be refused too. With the restart
            // failing (no star, guider refused) the run parks; Resume restarts guiding itself before dithering.
            var (guider, calls) = Guider(connected: true, "refused", "refused", null);
            var starts = 0;
            guider.Setup(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ++starts > 1);
            var (item, gate) = Rig(guider.Object);
            string? status = null;

            var execute = item.Execute(new SyncProgress(s => status = s.Status), CancellationToken.None);
            Assert.That(await PauseRequested(gate), Is.True);
            Assert.That(calls, Has.Count.EqualTo(1), "no second dither while the guider is not guiding");
            Assert.That(starts, Is.EqualTo(1));
            Assert.That(status, Does.Contain("not guiding"));
            Assert.That(status, Does.Contain("restarting it did not help"));

            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(starts, Is.EqualTo(2), "Resume restarted guiding again instead of asking for a manual Guide");
            Assert.That(calls, Has.Count.EqualTo(3), "refused again on Resume, then settled after the restart");
        }

        [Test]
        public async Task A_restart_that_throws_parks_the_run_with_the_guiders_message() {
            var (guider, calls) = Guider(connected: true, "refused", null);
            guider.Setup(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("no star selected"));
            var (item, gate) = Rig(guider.Object);
            string? status = null;

            var execute = item.Execute(new SyncProgress(s => status = s.Status), CancellationToken.None);
            Assert.That(await PauseRequested(gate), Is.True, "a throwing restart parks the run instead of failing the item");
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(status, Does.Contain("no star selected"), "the guider's own words reach the user");
            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Test]
        public async Task A_guider_that_disconnects_while_the_run_is_parked_keeps_it_parked_on_resume() {
            // The CHANGELOG promise: Resume with the guider gone pauses again, named as "not connected",
            // and never lets the frame start.
            var (guider, calls) = Guider(connected: true, "refused", "refused");
            var connected = true;
            guider.Setup(g => g.GetInfo()).Returns(() => new GuiderInfo { Connected = Volatile.Read(ref connected) });
            var (item, gate) = Rig(guider.Object);
            string? status = null;

            var execute = item.Execute(new SyncProgress(s => status = s.Status), CancellationToken.None);
            Assert.That(await PauseRequested(gate), Is.True);
            Assert.That(status, Does.Contain("not guiding"), "connected at the first dither");

            Volatile.Write(ref connected, false); // the guider drops while the run is parked
            gate.Resume();
            await Task.Delay(50);
            Assert.That(await PauseRequested(gate), Is.True, "parked again: the guider is gone");
            Assert.That(execute.IsCompleted, Is.False, "the frame must not start");
            Assert.That(calls, Has.Count.EqualTo(2));
            Assert.That(status, Does.Contain("not connected"));
        }

        [Test]
        public async Task A_dither_the_guider_rejected_pauses_with_the_guiders_own_reason() {
            var (guider, calls) = Guider(connected: true, "refused: dither amount out of range", null);
            var (item, gate) = Rig(guider.Object);
            string? status = null;

            var execute = item.Execute(new SyncProgress(s => status = s.Status), CancellationToken.None);
            Assert.That(await PauseRequested(gate), Is.True);
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(status, Does.Contain("dither amount out of range"), "the guider's reason, not a generic 'not guiding'");

            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Test]
        public async Task A_dither_fired_by_the_dither_after_exposures_trigger_pauses_the_run_too() {
            // Real plans dither through the trigger, whose TriggerRunner used to hang off nothing: the
            // Dither inside it could not find the root's pause gate, threw, and the trigger merely logged
            // FAILED while the next frame started on a rejected dither.
            var (guider, calls) = Guider(connected: true, "refused: dither amount out of range", null);
            var gate = new PauseGate();
            var root = new SequenceRootContainer { PauseGate = gate };
            var trigger = new DitherAfterExposures { AfterExposures = 1 };
            trigger.TriggerRunner.Add(new Dither(guider.Object, Mock.Of<IProfileService>()));
            trigger.AttachNewParent(root);

            var run = trigger.Run(root, NoProgress, CancellationToken.None);
            Assert.That(await PauseRequested(gate), Is.True, "the Dither inside the trigger must reach the root's pause gate");
            Assert.That(run.IsCompleted, Is.False);
            Assert.That(calls, Has.Count.EqualTo(1));

            gate.Resume();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(calls, Has.Count.EqualTo(2));
            Assert.That(trigger.Status, Is.Not.EqualTo(OpenAstroAra.Core.Enums.SequenceEntityStatus.FAILED));
        }

        /// <summary>A trigger that would fire on anything, counting how often the strategy asks it.</summary>
        private sealed class AlwaysTrigger : OpenAstroAra.Sequencer.Trigger.SequenceTrigger {
            public int Asked;
            public override bool ShouldTrigger(ISequenceItem? previousItem, ISequenceItem? nextItem) { Asked++; return false; }
            public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) => Task.CompletedTask;
            public override object Clone() => new AlwaysTrigger();
        }

        [Test]
        public async Task A_runner_item_never_walks_the_enclosing_containers_triggers() {
            // The runner must stay detached from the tree: with a parent, SequentialStrategy would run
            // every enclosing trigger around each runner item — a Reconnect trigger for a device that
            // stays offline would then re-fire inside its own runner without end (review r2). The gate
            // reaches the runner by value instead.
            var (guider, calls) = Guider(connected: true, new string?[] { null });
            var gate = new PauseGate();
            var root = new SequenceRootContainer { PauseGate = gate };
            var block = new SequentialContainer();
            block.AttachNewParent(root);
            var sibling = new AlwaysTrigger();
            block.Triggers.Add(sibling);
            sibling.AttachNewParent(block);
            var trigger = new DitherAfterExposures { AfterExposures = 1 };
            trigger.TriggerRunner.Add(new Dither(guider.Object, Mock.Of<IProfileService>()));
            block.Triggers.Add(trigger);
            trigger.AttachNewParent(block);

            await trigger.Run(block, NoProgress, CancellationToken.None);

            Assert.That(trigger.TriggerRunner.Parent, Is.Null, "the runner hangs off nothing");
            Assert.That(sibling.Asked, Is.EqualTo(0), "the runner's Dither must not evaluate the block's other triggers");
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(trigger.TriggerRunner.PauseGate, Is.Null, "the gate is lent for the run only and handed back after it");
        }

        [Test]
        public async Task A_rig_without_a_connected_guider_keeps_the_old_fire_and_forget_dither() {
            var (guider, calls) = Guider(connected: false, "refused", "refused");
            var (item, gate) = Rig(guider.Object);

            await item.Execute(NoProgress, CancellationToken.None);

            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(gate.IsPauseRequested, Is.False);
        }

        [Test]
        public async Task An_abort_during_the_pause_propagates() {
            var (guider, _) = Guider(connected: true, "refused", "refused");
            var (item, gate) = Rig(guider.Object);
            using var cts = new CancellationTokenSource();

            var execute = item.Execute(NoProgress, cts.Token);
            Assert.That(await PauseRequested(gate), Is.True);
            await cts.CancelAsync();

            await Assert.CatchAsync<OperationCanceledException>(() => execute.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public async Task Without_a_pause_gate_a_settle_failure_does_not_fail_the_instruction() {
            var (guider, calls) = Guider(connected: true, "timed out");
            var item = new Dither(guider.Object, Mock.Of<IProfileService>());

            await item.Execute(NoProgress, CancellationToken.None);

            Assert.That(calls, Has.Count.EqualTo(1));
        }

        // ── §42.2 a restart after a long guiding gap re-centers first ─────────────────────────────

        private static readonly Coordinates Target = new(ra: 83.8, dec: -5.4, Epoch.J2000, Coordinates.RAType.Degrees);
        private static readonly string[] RecenterThenStart = ["recenter", "start"];
        private static readonly string[] StartOnly = ["start"];

        /// <summary>A Dither inside a real target container (RetrieveContextCoordinates walks up to it),
        /// with a restart re-center that records its calls.</summary>
        private static (Dither Item, PauseGate Gate, List<string> Calls) TargetRig(Mock<IGuiderMediator> guider, bool recenterSucceeds = true) {
            var calls = new List<string>();
            var recenter = new Mock<IGuidingRestartRecenter>();
            recenter.SetupGet(r => r.LongGap).Returns(TimeSpan.FromMinutes(3));
            recenter.Setup(r => r.RecenterAsync(It.IsAny<Coordinates>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { calls.Add("recenter"); return recenterSucceeds; });
            guider.Setup(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { calls.Add("start"); return true; });
            var gate = new PauseGate();
            var root = new SequenceRootContainer { PauseGate = gate };
            var dso = new DeepSkyObjectContainer(new HeadlessProfileService()) { Name = "Target" };
            dso.Target.InputCoordinates = new InputCoordinates(Target);
            dso.Target.DeepSkyObject = new DeepSkyObject("Target", Target, null);
            dso.AttachNewParent(root);
            var item = new Dither(guider.Object, Mock.Of<IProfileService>(), recenter.Object);
            item.AttachNewParent(dso);
            return (item, gate, calls);
        }

        [Test]
        public async Task A_restart_after_a_long_guiding_gap_re_centers_the_target_first() {
            var (guider, dithers) = Guider(connected: true, "refused", null);
            guider.SetupGet(g => g.GuidingStoppedUtc).Returns(DateTimeOffset.UtcNow.AddMinutes(-20));
            var (item, gate, calls) = TargetRig(guider);
            string? status = null;

            await item.Execute(new SyncProgress(s => status = s.Status), CancellationToken.None);

            Assert.That(calls, Is.EqualTo(RecenterThenStart), "plate solve and re-center, then guiding, then the dither");
            Assert.That(dithers, Has.Count.EqualTo(2));
            Assert.That(gate.IsPauseRequested, Is.False);
        }

        [Test]
        public async Task A_restart_after_a_short_gap_keeps_the_plain_restart() {
            var (guider, dithers) = Guider(connected: true, "refused", null);
            guider.SetupGet(g => g.GuidingStoppedUtc).Returns(DateTimeOffset.UtcNow.AddSeconds(-40));
            var (item, _, calls) = TargetRig(guider);

            await item.Execute(new SyncProgress(_ => { }), CancellationToken.None);

            Assert.That(calls, Is.EqualTo(StartOnly), "a slew's stop or a brief loss is no reason to re-center");
            Assert.That(dithers, Has.Count.EqualTo(2));
        }

        [Test]
        public async Task A_re_center_that_fails_still_restarts_guiding_and_carries_on() {
            var (guider, dithers) = Guider(connected: true, "refused", null);
            guider.SetupGet(g => g.GuidingStoppedUtc).Returns(DateTimeOffset.UtcNow.AddMinutes(-20));
            var (item, gate, calls) = TargetRig(guider, recenterSucceeds: false);

            await item.Execute(new SyncProgress(_ => { }), CancellationToken.None);

            Assert.That(calls, Is.EqualTo(RecenterThenStart));
            Assert.That(dithers, Has.Count.EqualTo(2), "guiding where the mount points beats a parked run");
            Assert.That(gate.IsPauseRequested, Is.False);
        }

        [Test]
        public async Task Without_a_guiding_stopped_time_no_re_center_runs() {
            var (guider, dithers) = Guider(connected: true, "refused", null);
            guider.SetupGet(g => g.GuidingStoppedUtc).Returns((DateTimeOffset?)null);
            var (item, _, calls) = TargetRig(guider);

            await item.Execute(new SyncProgress(_ => { }), CancellationToken.None);

            Assert.That(calls, Is.EqualTo(StartOnly));
            Assert.That(dithers, Has.Count.EqualTo(2));
        }
    }
}