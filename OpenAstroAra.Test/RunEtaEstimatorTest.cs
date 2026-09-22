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

namespace OpenAstroAra.Test {

    // #1068 — the run ETA is the sequencer's own model, published in run state; these pin the
    // walk the client used to do from the stored body (15 s nominal + exposure × iterations) and
    // the remaining-credit rules it never had.
    [TestFixture]
    public class RunEtaEstimatorTest {

        private static ISequenceItem Leaf(double seconds, SequenceEntityStatus status = SequenceEntityStatus.CREATED) {
            var m = new Mock<ISequenceItem>();
            m.SetupProperty(i => i.Status, status);
            m.Setup(i => i.GetEstimatedDuration()).Returns(TimeSpan.FromSeconds(seconds));
            return m.Object;
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
        public void A_throwing_estimate_costs_the_nominal_and_a_disabled_loop_does_not_multiply() {
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
            var bias = new SequentialContainer();
            var camera = new Mock<ICameraMediator>();
            camera.Setup(c => c.GetInfo()).Returns(new CameraInfo { Connected = true }); // Validate() runs on attach
            var exposure = new TakeExposure(camera.Object, new Mock<IImagingMediator>().Object) { ExposureTime = 0 };
            bias.Add(exposure);
            Assert.That(RunEtaEstimator.EstimateTotalSeconds(bias), Is.EqualTo(0), "a bias frame's zero exposure is zero, not the nominal");
            var wait = new SequentialContainer();
            wait.Add(new WaitForTimeSpan { Time = 0 });
            Assert.That(RunEtaEstimator.EstimateTotalSeconds(wait), Is.EqualTo(0), "an elapsed wait costs nothing, not the nominal");
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
