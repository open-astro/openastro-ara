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
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The Dither instruction never lets a frame start on a star that has not settled: a failed settle gets
    /// one more dither, a second failure or a refused dither parks the run awaiting the user, and Resume
    /// dithers again (2026-10-16: two settle timeouts, two trailed frames).
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
                return Task.FromResult(verdict != "refused");
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
        public async Task One_settle_failure_dithers_again_and_a_settled_retry_continues() {
            var (guider, calls) = Guider(connected: true, "timed-out waiting for guider to settle", null);
            var (item, gate) = Rig(guider.Object);
            string? status = null;

            await item.Execute(new SyncProgress(s => status = s.Status), CancellationToken.None);

            Assert.That(calls, Has.Count.EqualTo(2), "the second dither settled, so the frame may start");
            Assert.That(gate.IsPauseRequested, Is.False);
            Assert.That(status, Does.Contain("trying once more"));
        }

        [Test]
        public async Task Two_settle_failures_park_the_run_and_resume_dithers_again() {
            var (guider, calls) = Guider(connected: true, "timed-out waiting for guider to settle", "settle timed out — no SettleDone from the guider", null);
            var (item, gate) = Rig(guider.Object);
            string? status = null;

            var execute = item.Execute(new SyncProgress(s => status = s.Status), CancellationToken.None);
            Assert.That(await PauseRequested(gate), Is.True);
            Assert.That(execute.IsCompleted, Is.False, "parked inside the pause, not failed and not exposing");
            Assert.That(gate.PendingKind, Is.EqualTo(PauseKind.AwaitingUser));
            Assert.That(calls, Has.Count.EqualTo(2));
            Assert.That(status, Does.Contain("did not settle twice"));

            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(calls, Has.Count.EqualTo(3), "Resume dithered again and it settled");
            Assert.That(gate.IsPauseRequested, Is.False);
        }

        [Test]
        public async Task A_refused_dither_parks_the_run_at_once() {
            // Not guiding is not a settle problem: a second dither would be refused too.
            var (guider, calls) = Guider(connected: true, "refused", null);
            var (item, gate) = Rig(guider.Object);
            string? status = null;

            var execute = item.Execute(new SyncProgress(s => status = s.Status), CancellationToken.None);
            Assert.That(await PauseRequested(gate), Is.True);
            Assert.That(calls, Has.Count.EqualTo(1), "no retry on a refusal");
            Assert.That(status, Does.Contain("not guiding"));

            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(calls, Has.Count.EqualTo(2));
        }

        [Test]
        public async Task A_dither_fired_by_the_dither_after_exposures_trigger_pauses_the_run_too() {
            // Real plans dither through the trigger, whose TriggerRunner used to hang off nothing: the
            // Dither inside it could not find the root's pause gate, threw, and the trigger merely logged
            // FAILED while the next frame started on the swinging star.
            var (guider, calls) = Guider(connected: true, "timed out", "timed out", null);
            var gate = new PauseGate();
            var root = new SequenceRootContainer { PauseGate = gate };
            var trigger = new DitherAfterExposures { AfterExposures = 1 };
            trigger.TriggerRunner.Add(new Dither(guider.Object, Mock.Of<IProfileService>()));
            trigger.AttachNewParent(root);

            var run = trigger.Run(root, NoProgress, CancellationToken.None);
            Assert.That(await PauseRequested(gate), Is.True, "the Dither inside the trigger must reach the root's pause gate");
            Assert.That(run.IsCompleted, Is.False);
            Assert.That(calls, Has.Count.EqualTo(2));

            gate.Resume();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(calls, Has.Count.EqualTo(3));
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
            Assert.That(trigger.TriggerRunner.PauseGate, Is.SameAs(gate), "the gate reached the runner by value");
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
        public async Task Without_a_pause_gate_a_second_settle_failure_fails_the_instruction() {
            var (guider, _) = Guider(connected: true, "timed out", "timed out");
            var item = new Dither(guider.Object, Mock.Of<IProfileService>());

            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => item.Execute(NoProgress, CancellationToken.None));
        }
    }
}
