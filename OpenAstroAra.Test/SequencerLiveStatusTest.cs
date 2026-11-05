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
using OpenAstroAra.Equipment.Equipment.MyCamera;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.SequenceItem.Platesolving;
using OpenAstroAra.Sequencer.Utility;
using OpenAstroAra.Server.Services;
using Slew = OpenAstroAra.Sequencer.SequenceItem.Telescope.SlewScopeToRaDec;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The run's tree-derived live status (SequencerService.LiveStatus) and the target-name resolver it
    /// shares with TakeExposure: a client-built target block is a plain SequentialContainer named after the
    /// target carrying an AboveHorizonCondition, with the exposure loop nested inside it.
    /// </summary>
    [TestFixture]
    public class SequencerLiveStatusTest {

        [Test]
        public void A_progress_report_describes_the_status_and_its_detail() {
            Assert.That(SequencerService.DescribeStatus(new ApplicationStatus { Status = "Start guiding", Status2 = "Calibrating" }),
                Is.EqualTo("Start guiding · Calibrating"));
            Assert.That(SequencerService.DescribeStatus(new ApplicationStatus { Status = "Start guiding" }), Is.EqualTo("Start guiding"));
            Assert.That(SequencerService.DescribeStatus(new ApplicationStatus { Status = "", Status2 = "Calibrating" }), Is.EqualTo("Calibrating"),
                "a detail without a headline still shows");
            Assert.That(SequencerService.DescribeStatus(new ApplicationStatus { Status = "" }), Is.Empty, "nothing to show keeps the last description");
            Assert.That(SequencerService.ResumedDescription, Does.StartWith("Resumed"));
        }

        private static (SequenceRootContainer Root, TakeExposure Exposure, LoopCondition Loop) ClientBuiltTree(TakeExposure? withExposure = null) {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var exposure = withExposure ?? (TakeExposure)factory.Items.OfType<TakeExposure>().First().Clone();
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
        public async Task The_exposure_hands_the_target_name_to_the_capture() {
            var camera = new Mock<ICameraMediator>();
            camera.Setup(c => c.GetInfo()).Returns(new CameraInfo { Connected = true }); // Validate() runs on attach
            var imaging = new Mock<IImagingMediator>();
            var (_, exposure, _) = ClientBuiltTree(new TakeExposure(camera.Object, imaging.Object));

            await exposure.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            imaging.Verify(m => m.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<ApplicationStatus>?>(), "NGC 7000"), Times.Once,
                "frames file under the target, not the imaging loop's \"Imaging\"");
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
        public void A_cool_step_that_does_not_wait_says_the_plan_carries_on() {
            // #1574 — a session Cool Camera (WaitForTemperature = false) starts the cooler and moves on.
            var cool = (OpenAstroAra.Sequencer.SequenceItem.Camera.CoolCamera)HeadlessSequencerFactory.WithDefaults()
                .Items.OfType<OpenAstroAra.Sequencer.SequenceItem.Camera.CoolCamera>().First().Clone();
            cool.Temperature = -10;
            cool.WaitForTemperature = false;
            Assert.That(SequencerService.DescribeLeaf(cool), Is.EqualTo("Cooling the camera to -10 °C, not waiting"));
        }

        [Test]
        public void A_slew_that_follows_the_profiles_centre_after_slew_reads_as_centring() {
            // #1573 — Center = null centres when Settings -> Plate Solving -> "Center target after slew"
            // is on, so the label must say Centring, as it does for Center = true.
            Slew Make(bool profileCenters, bool? center) {
                var settings = new Mock<OpenAstroAra.Profile.Interfaces.IPlateSolveSettings>();
                settings.SetupGet(x => x.SlewToTarget).Returns(profileCenters);
                var profile = new Mock<OpenAstroAra.Profile.Interfaces.IProfileService>();
                profile.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(settings.Object);
                var mount = new Mock<ITelescopeMediator>();
                mount.Setup(m => m.GetInfo()).Returns(new TelescopeInfo { Connected = true });
                var slew = new Slew(mount.Object, Mock.Of<IGuiderMediator>(), profile.Object) { Center = center };
                var target = new SequentialContainer { Name = "NGC 7000" };
                target.Add((AboveHorizonCondition)HeadlessSequencerFactory.WithDefaults().GetCondition<AboveHorizonCondition>().Clone());
                target.Add(slew);
                new SequenceRootContainer().Add(target);
                return slew;
            }
            Assert.Multiple(() => {
                Assert.That(SequencerService.DescribeLeaf(Make(true, null)), Is.EqualTo("Centring on NGC 7000"));
                Assert.That(SequencerService.DescribeLeaf(Make(false, null)), Is.EqualTo("Slewing to NGC 7000"));
                Assert.That(SequencerService.DescribeLeaf(Make(true, false)), Is.EqualTo("Slewing to NGC 7000"), "an explicit blind slew wins");
            });
        }

        [Test]
        public void The_plans_steps_read_in_plain_words_naming_the_target() {
            // User, 2026-11-02: "Slewing to target XXX", not "Slew scope to ra dec".
            var factory = HeadlessSequencerFactory.WithDefaults();
            T Step<T>() where T : class, ISequenceItem => (T)factory.Items.OfType<T>().First().Clone();
            var slew = Step<OpenAstroAra.Sequencer.SequenceItem.Telescope.SlewScopeToRaDec>();
            var focus = Step<OpenAstroAra.Sequencer.SequenceItem.Autofocus.RunAutofocus>();
            var cool = Step<OpenAstroAra.Sequencer.SequenceItem.Camera.CoolCamera>();
            cool.Temperature = -10;
            // A client-built target block: its horizon condition marks it as the target.
            var target = new SequentialContainer { Name = "NGC 7000" };
            target.Add((AboveHorizonCondition)factory.GetCondition<AboveHorizonCondition>().Clone());
            target.Add(slew);
            target.Add(focus);
            target.Add(cool);
            new SequenceRootContainer().Add(target);

            Assert.Multiple(() => {
                Assert.That(SequencerService.DescribeLeaf(slew), Is.EqualTo("Slewing to NGC 7000"));
                slew.Center = true;
                Assert.That(SequencerService.DescribeLeaf(slew), Is.EqualTo("Centring on NGC 7000"), "a plan's centring slew");
                slew.Center = null;
                Assert.That(SequencerService.DescribeLeaf(focus), Is.EqualTo("Focusing"));
                Assert.That(SequencerService.DescribeLeaf(cool), Is.EqualTo("Waiting for the camera to cool to -10 °C"));
                Assert.That(SequencerService.DescribeLeaf(Step<OpenAstroAra.Sequencer.SequenceItem.Telescope.UnparkScope>()), Is.EqualTo("Unparking the mount"));
                Assert.That(SequencerService.DescribeLeaf(Step<OpenAstroAra.Sequencer.SequenceItem.Guider.StartGuiding>()), Is.EqualTo("Starting guiding"));
                Assert.That(SequencerService.DescribeLeaf(new CenterAndRotate(centeringExecutor: null)), Is.EqualTo("Centring"),
                    "outside a target block there is no name to give");
            });
        }

        [Test]
        public void The_other_plan_steps_read_in_plain_words_too() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            T Step<T>() where T : class, ISequenceItem => (T)factory.Items.OfType<T>().First().Clone();
            var wait = Step<OpenAstroAra.Sequencer.SequenceItem.Utility.WaitForTime>();
            wait.Hours = 9;
            wait.Minutes = 5;
            var filter = Step<OpenAstroAra.Sequencer.SequenceItem.FilterWheel.SwitchFilter>();
            filter.Filter = new OpenAstroAra.Core.Model.Equipment.FilterInfo("Ha", 0, 0);

            Assert.Multiple(() => {
                Assert.That(SequencerService.DescribeLeaf(wait), Is.EqualTo("Waiting until 09:05"), "zero-padded clock time");
                Assert.That(SequencerService.DescribeLeaf(filter), Is.EqualTo("Switching to the Ha filter"));
                Assert.That(SequencerService.DescribeLeaf(Step<OpenAstroAra.Sequencer.SequenceItem.Telescope.ParkScope>()), Is.EqualTo("Parking the mount"));
                Assert.That(SequencerService.DescribeLeaf(Step<OpenAstroAra.Sequencer.SequenceItem.Telescope.SetTracking>()), Is.EqualTo("Starting tracking"));
                Assert.That(SequencerService.DescribeLeaf(Step<OpenAstroAra.Sequencer.SequenceItem.Camera.WarmCamera>()), Is.EqualTo("Warming the camera"));
            });
        }

        [Test]
        public void Other_instructions_get_a_humanised_type_name() {
            Assert.That(SequencerService.DescribeLeaf(new OpenAstroAra.Sequencer.SequenceItem.Utility.WaitForTimeSpan()), Is.EqualTo("Wait for time span"));
        }
    }
}
