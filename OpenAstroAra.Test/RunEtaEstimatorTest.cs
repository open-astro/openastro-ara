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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Equipment.Equipment.MyCamera;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;

namespace OpenAstroAra.Test {

    // #1068 — the run ETA is the sequencer's own model, published in run state; these pin the
    // walk the client used to do from the stored body (15 s nominal + exposure × iterations) and
    // the remaining-credit rules it never had.
    [TestFixture]
    [NonParallelizable] // RunEtaEstimator.UtcNow is a process-wide test clock
    public class RunEtaEstimatorTest {

        private static ISequenceItem Leaf(double seconds, SequenceEntityStatus status = SequenceEntityStatus.CREATED) {
            var m = new Mock<ISequenceItem>();
            m.SetupProperty(i => i.Status, status);
            m.Setup(i => i.GetEstimatedDuration()).Returns(TimeSpan.FromSeconds(seconds));
            return m.Object;
        }

        // #1155 — a leaf that DECLARES its duration model (zero means zero), optionally running since a moment.
        private static ISequenceItem ModelledLeaf(double seconds, SequenceEntityStatus status = SequenceEntityStatus.CREATED, DateTimeOffset? runningSince = null, bool estimateIsRemaining = false) {
            var m = new Mock<ISequenceItem>();
            m.SetupProperty(i => i.Status, status);
            m.Setup(i => i.GetEstimatedDuration()).Returns(TimeSpan.FromSeconds(seconds));
            m.As<IEstimatesOwnDuration>().SetupGet(d => d.EstimateIsRemaining).Returns(estimateIsRemaining);
            m.As<IRunningSince>().SetupGet(r => r.RunningSinceUtc).Returns(runningSince);
            return m.Object;
        }

        [Test]
        public void An_estimate_that_is_already_time_left_is_not_credited_a_second_time() {
            // A "wait until 22:00" started at 21:00 reports 1800 s at 21:30; subtracting the 1800 s
            // elapsed as well read 0 with half an hour still to wait.
            var now = new DateTimeOffset(2026, 10, 13, 21, 30, 0, TimeSpan.Zero);
            RunEtaEstimator.UtcNow = () => now;
            try {
                var root = new SequentialContainer();
                root.Add(ModelledLeaf(1800, SequenceEntityStatus.RUNNING, runningSince: now.AddMinutes(-30), estimateIsRemaining: true));
                root.Status = SequenceEntityStatus.RUNNING;
                Assert.That(RunEtaEstimator.EstimateRemainingSeconds(root), Is.EqualTo(1800));
            } finally {
                RunEtaEstimator.UtcNow = () => DateTimeOffset.UtcNow;
            }
            var wait = new WaitForTime(new System.Collections.Generic.List<OpenAstroAra.Sequencer.Utility.DateTimeProvider.IDateTimeProvider>());
            Assert.That(((IEstimatesOwnDuration)wait).EstimateIsRemaining, Is.True, "the real WaitForTime opts out of the credit");
            Assert.That(((IEstimatesOwnDuration)new WaitForTimeSpan()).EstimateIsRemaining, Is.False, "a fixed-length wait is credited");
        }

        [Test]
        public void Every_instruction_that_overrides_the_estimate_declares_the_contract() {
            // #1155 — the point of the contract over the hand list: a type that models its duration but
            // forgets the interface would silently cost the nominal again. Reflection keeps them honest.
            var sequenceItem = typeof(OpenAstroAra.Sequencer.SequenceItem.SequenceItem);
            var overriding = sequenceItem.Assembly.GetTypes()
                .Where(t => !t.IsAbstract && sequenceItem.IsAssignableFrom(t)
                    && t.GetMethod("GetEstimatedDuration", Type.EmptyTypes) is { } m && m.DeclaringType == t)
                .ToList();
            Assert.That(overriding, Is.Not.Empty);
            Assert.That(overriding.Where(t => !typeof(IEstimatesOwnDuration).IsAssignableFrom(t)).Select(t => t.Name), Is.Empty,
                "every instruction with its own duration model must implement IEstimatesOwnDuration");
        }

        [Test]
        public void An_instruction_that_declares_its_duration_model_costs_its_honest_zero_and_an_undeclared_zero_costs_the_nominal() {
            // The contract (#1155) replaces the hand list: SetReadoutMode-style truthful zeros cost 0,
            // a bare mock with a Zero placeholder still costs the 15 s nominal.
            var root = new SequentialContainer();
            root.Add(ModelledLeaf(0));
            root.Add(Leaf(0));
            Assert.That(RunEtaEstimator.EstimateTotalSeconds(root), Is.EqualTo(RunEtaEstimator.NominalInstructionSeconds));
            // The real instructions carry the contract.
            Assert.That(new OpenAstroAra.Sequencer.SequenceItem.Camera.SetReadoutMode(null!), Is.InstanceOf<IEstimatesOwnDuration>());
            Assert.That(new OpenAstroAra.Sequencer.SequenceItem.Camera.SetUSBLimit(null!), Is.InstanceOf<IEstimatesOwnDuration>());
            Assert.That(new WaitForTimeSpan(), Is.InstanceOf<IEstimatesOwnDuration>());
        }

        [Test]
        public void A_running_instruction_is_charged_only_its_unspent_part() {
            var now = new DateTimeOffset(2026, 10, 13, 3, 0, 0, TimeSpan.Zero);
            RunEtaEstimator.UtcNow = () => now;
            try {
                var root = new SequentialContainer();
                root.Add(ModelledLeaf(300, SequenceEntityStatus.RUNNING, runningSince: now.AddSeconds(-250)));
                root.Add(ModelledLeaf(300));
                root.Status = SequenceEntityStatus.RUNNING;
                Assert.That(RunEtaEstimator.EstimateRemainingSeconds(root), Is.EqualTo(50 + 300), "250 s of the running 300 s exposure are spent");
                Assert.That(RunEtaEstimator.EstimateTotalSeconds(root), Is.EqualTo(600), "the total is the plan, not the clock");
                var overrun = new SequentialContainer();
                overrun.Add(ModelledLeaf(300, SequenceEntityStatus.RUNNING, runningSince: now.AddSeconds(-900)));
                overrun.Status = SequenceEntityStatus.RUNNING;
                Assert.That(RunEtaEstimator.EstimateRemainingSeconds(overrun), Is.EqualTo(0), "an overrun never goes negative");
                var unstamped = new SequentialContainer();
                unstamped.Add(ModelledLeaf(300, SequenceEntityStatus.RUNNING));
                unstamped.Status = SequenceEntityStatus.RUNNING;
                Assert.That(RunEtaEstimator.EstimateRemainingSeconds(unstamped), Is.EqualTo(300), "no stamp: charged in full, as before");
            } finally {
                RunEtaEstimator.UtcNow = () => DateTimeOffset.UtcNow;
            }
        }

        [Test]
        public void A_real_item_stamps_the_moment_it_starts_running() {
            var item = new WaitForTimeSpan();
            Assert.That(item.RunningSinceUtc, Is.Null);
            item.Status = SequenceEntityStatus.RUNNING;
            var first = item.RunningSinceUtc;
            Assert.That(first, Is.Not.Null.And.GreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1)));
            item.Status = SequenceEntityStatus.RUNNING; // re-assigned (a future pause/resume): the stamp survives
            Assert.That(item.RunningSinceUtc, Is.EqualTo(first));
            item.Status = SequenceEntityStatus.FINISHED;
            Assert.That(item.RunningSinceUtc, Is.Null, "cleared on every other transition");
        }

        [Test]
        public void Disabled_leaves_are_not_part_of_the_instruction_count_either() {
            // #1155 — instructions_total and the time estimate count the same items.
            var root = new SequentialContainer();
            root.Add(Leaf(10));
            root.Add(Leaf(10, SequenceEntityStatus.DISABLED));
            var block = new SequentialContainer();
            block.Add(Leaf(10));
            block.Status = SequenceEntityStatus.DISABLED;
            root.Add(block);
            var leaves = SequencerService.CollectLeaves(root);
            Assert.That(leaves, Has.Count.EqualTo(1));
            Assert.That(SequencerService.CountTerminalLeaves(leaves), Is.EqualTo(0));
        }

        private static SequentialContainer Loop(int iterations, int completed, params ISequenceItem[] items) {
            var c = new SequentialContainer();
            c.Add(new LoopCondition { Iterations = iterations, CompletedIterations = completed });
            foreach (var i in items) {
                c.Add(i);
            }
            return c;
        }

        [Test]
        public void Total_sums_exposure_times_times_loop_iterations_plus_the_nominal_cost() {
            // slew (no estimate → 15 s nominal) + 10 × 120 s exposure = 1215 s — the client's old test case.
            var root = new SequentialContainer();
            root.Add(Leaf(0));
            root.Add(Loop(10, 0, Leaf(120)));
            Assert.That(RunEtaEstimator.EstimateTotalSeconds(root), Is.EqualTo(1215));
            Assert.That(RunEtaEstimator.EstimateRemainingSeconds(root), Is.EqualTo(1215), "nothing started yet");
        }

        [Test]
        public void Remaining_credits_terminal_leaves_and_completed_loop_passes() {
            var loop = Loop(10, 4, Leaf(120, SequenceEntityStatus.FINISHED), Leaf(30, SequenceEntityStatus.RUNNING));
            loop.Status = SequenceEntityStatus.RUNNING;
            var root = new SequentialContainer();
            root.Add(Leaf(0, SequenceEntityStatus.FINISHED)); // the slew is done
            root.Add(loop);
            root.Status = SequenceEntityStatus.RUNNING;
            // Pass 5 of 10 in flight: 30 s left in it, then 5 full passes of 150 s.
            Assert.That(RunEtaEstimator.EstimateTotalSeconds(root), Is.EqualTo(15 + 10 * 150));
            Assert.That(RunEtaEstimator.EstimateRemainingSeconds(root), Is.EqualTo(30 + 5 * 150));
        }

        [Test]
        public void A_throwing_estimate_on_an_instruction_without_a_duration_model_costs_the_nominal_and_a_disabled_loop_does_not_multiply() {
            var throwing = new Mock<ISequenceItem>();
            throwing.SetupProperty(i => i.Status, SequenceEntityStatus.CREATED);
            throwing.Setup(i => i.GetEstimatedDuration()).Throws(new ArgumentOutOfRangeException("hour"));
            var root = new SequentialContainer();
            root.Add(throwing.Object);
            Assert.That(RunEtaEstimator.EstimateTotalSeconds(root), Is.EqualTo(RunEtaEstimator.NominalInstructionSeconds));

            var loop = Loop(10, 0, Leaf(120));
            ((LoopCondition)loop.Conditions[0]).Status = SequenceEntityStatus.DISABLED;
            Assert.That(RunEtaEstimator.EstimateTotalSeconds(loop), Is.EqualTo(120), "a disabled loop condition runs the block once");
        }

        [Test]
        public void Disabled_subtrees_are_out_of_the_total_a_parallel_block_costs_its_longest_child_and_a_zero_exposure_is_zero() {
            // #1080 — total and remaining count the same items.
            var root = new SequentialContainer();
            root.Add(Leaf(100));
            root.Add(Leaf(100, SequenceEntityStatus.DISABLED));
            Assert.That(RunEtaEstimator.EstimateTotalSeconds(root), Is.EqualTo(100), "a DISABLED block is not in the total");
            var par = new ParallelContainer();
            par.Add(Leaf(30));
            par.Add(Leaf(120));
            Assert.That(RunEtaEstimator.EstimateTotalSeconds(par), Is.EqualTo(120), "parallel children run concurrently");
            Assert.That(RunEtaEstimator.EstimateRemainingSeconds(par), Is.EqualTo(120));
            // The pass-in-progress branch of Remaining(): a RUNNING parallel block with both
            // children RUNNING is still its longest child (summing gives 150).
            var running = new ParallelContainer();
            running.Add(Leaf(30, SequenceEntityStatus.RUNNING));
            running.Add(Leaf(120, SequenceEntityStatus.RUNNING));
            running.Status = SequenceEntityStatus.RUNNING;
            Assert.That(RunEtaEstimator.EstimateRemainingSeconds(running), Is.EqualTo(120), "a running parallel pass is its longest unfinished child");
            var bias = new SequentialContainer();
            var camera = new Mock<ICameraMediator>();
            camera.Setup(c => c.GetInfo()).Returns(new CameraInfo { Connected = true }); // Validate() runs on attach
            var exposure = new TakeExposure(camera.Object, new Mock<IImagingMediator>().Object) { ExposureTime = 0 };
            bias.Add(exposure);
            Assert.That(RunEtaEstimator.EstimateTotalSeconds(bias), Is.EqualTo(0), "a zero exposure (unset/invalid) is zero, not the nominal");
            var wait = new SequentialContainer();
            wait.Add(new WaitForTimeSpan { Time = 0 });
            Assert.That(RunEtaEstimator.EstimateTotalSeconds(wait), Is.EqualTo(0), "an elapsed wait costs nothing, not the nominal");
            // A model that THROWS is "no estimate", not an honest zero, whatever the type.
            var throwing = new Mock<WaitForTimeSpan>();
            throwing.Setup(w => w.GetEstimatedDuration()).Throws<ArgumentOutOfRangeException>();
            var corrupt = new SequentialContainer();
            corrupt.Add(throwing.Object);
            Assert.That(RunEtaEstimator.EstimateTotalSeconds(corrupt), Is.EqualTo(RunEtaEstimator.NominalInstructionSeconds), "a throwing duration model costs the nominal even on a typed instruction");
        }

        [Test]
        public void A_skip_credits_the_pass_in_progress_but_stays_in_the_total() {
            // #1080 review — SKIPPED is reset to CREATED between loop passes (ResetProgress), so a
            // leaf skipped in pass 3 of 10 still runs in passes 4..10: the total must not move and
            // remaining drops by exactly the one skipped pass.
            var loop = Loop(10, 2, Leaf(300, SequenceEntityStatus.SKIPPED), Leaf(10, SequenceEntityStatus.RUNNING));
            loop.Status = SequenceEntityStatus.RUNNING;
            Assert.That(RunEtaEstimator.EstimateTotalSeconds(loop), Is.EqualTo(3100), "the plan's total is stable across a skip");
            Assert.That(RunEtaEstimator.EstimateRemainingSeconds(loop), Is.EqualTo(10 + 7 * 310), "this pass: the running leaf only; 7 full passes to come");
        }

        [Test]
        public void Finished_disabled_and_skipped_cost_nothing_and_never_go_negative() {
            var root = new SequentialContainer();
            root.Add(Leaf(100, SequenceEntityStatus.FINISHED));
            root.Add(Leaf(100, SequenceEntityStatus.SKIPPED));
            root.Add(Leaf(100, SequenceEntityStatus.DISABLED));
            root.Status = SequenceEntityStatus.FINISHED;
            Assert.That(RunEtaEstimator.EstimateRemainingSeconds(root), Is.EqualTo(0));
            // An over-completed loop (CompletedIterations > Iterations) clamps, not negative.
            var over = Loop(2, 5, Leaf(10));
            over.Status = SequenceEntityStatus.RUNNING;
            Assert.That(RunEtaEstimator.EstimateRemainingSeconds(over), Is.EqualTo(10));
        }
    }
}
