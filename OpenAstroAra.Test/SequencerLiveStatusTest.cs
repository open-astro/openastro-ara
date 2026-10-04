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
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.SequenceItem.Platesolving;
using OpenAstroAra.Sequencer.Utility;
using OpenAstroAra.Server.Services;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The run's tree-derived live status (SequencerService.LiveStatus) and the target-name resolver it
    /// shares with TakeExposure: a client-built target block is a plain SequentialContainer named after the
    /// target carrying an AboveHorizonCondition, with the exposure loop nested inside it.
    /// </summary>
    [TestFixture]
    public class SequencerLiveStatusTest {

        private static (SequenceRootContainer Root, TakeExposure Exposure, LoopCondition Loop) ClientBuiltTree() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var exposure = (TakeExposure)factory.Items.OfType<TakeExposure>().First().Clone();
            exposure.ExposureTime = 300;
            exposure.ImageType = "LIGHT";

            var loop = new LoopCondition { Iterations = 60 };
            var imaging = new SequentialContainer { Name = "Imaging" };
            imaging.Add(loop);
            imaging.Add(exposure);

            var target = new SequentialContainer { Name = "NGC 7000" };
            target.Add((AboveHorizonCondition)factory.GetCondition<AboveHorizonCondition>().Clone());
            target.Add(imaging);

            var root = new SequenceRootContainer();
            root.Add(target);
            return (root, exposure, loop);
        }

        [Test]
        public void Frames_and_the_live_status_name_the_target_block_not_the_imaging_loop() {
            var (_, exposure, _) = ClientBuiltTree();
            Assert.That(ItemUtility.ResolveTargetName(exposure), Is.EqualTo("NGC 7000"));
            Assert.That(SequencerService.TargetNameOf(exposure), Is.EqualTo("NGC 7000"));
        }

        [Test]
        public void Outside_any_target_block_there_is_no_target() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var exposure = (TakeExposure)factory.Items.OfType<TakeExposure>().First().Clone();
            var plain = new SequentialContainer { Name = "Imaging" };
            plain.Add(exposure);
            new SequenceRootContainer().Add(plain);
            Assert.That(ItemUtility.ResolveTargetName(exposure), Is.Null);
        }

        [Test]
        public void An_exposure_describes_itself_with_its_loop_progress() {
            var (_, exposure, loop) = ClientBuiltTree();
            Assert.That(SequencerService.DescribeLeaf(exposure), Is.EqualTo("Exposure 300 s LIGHT — 1/60"));
            loop.CompletedIterations = 3;
            Assert.That(SequencerService.DescribeLeaf(exposure), Is.EqualTo("Exposure 300 s LIGHT — 4/60"));
        }

        [Test]
        public void Other_instructions_get_a_humanised_type_name() {
            Assert.That(SequencerService.DescribeLeaf(new CenterAndRotate(centeringExecutor: null)), Is.EqualTo("Center and rotate"));
        }
    }
}
