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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.SequenceItem.Camera;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>The plan's Cool Camera step: by default it waits for the target; with
    /// WaitForTemperature off it starts the cool and the plan moves on (Simple Mode slews and
    /// focuses meanwhile, and waits on that cool before the first light).</summary>
    [TestFixture]
    public class CoolCameraStepTest {

        [Test]
        public async Task Without_waiting_the_step_starts_the_cool_and_leaves_nothing_running() {
            var camera = new Mock<ICameraMediator>();
            camera.Setup(c => c.StartCooling(-10, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var step = new CoolCamera(camera.Object) { Temperature = -10, WaitForTemperature = false };

            await step.Execute(new Progress<ApplicationStatus>(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            camera.Verify(c => c.StartCooling(-10, It.IsAny<CancellationToken>()), Times.Once);
            camera.Verify(c => c.CoolCamera(It.IsAny<double>(), It.IsAny<TimeSpan>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()),
                Times.Never, "no cool runs on after the step");
            Assert.That(step.GetEstimatedDuration(), Is.EqualTo(TimeSpan.Zero), "it takes no time in the plan");
        }

        [Test]
        public async Task By_default_the_step_waits_for_the_target_and_fails_when_it_isnt_reached() {
            var camera = new Mock<ICameraMediator>();
            camera.Setup(c => c.CoolCamera(-10, It.IsAny<TimeSpan>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var step = new CoolCamera(camera.Object) { Temperature = -10 };

            Assert.That(step.WaitForTemperature, Is.True);
            await Assert.ThatAsync(
                () => step.Execute(new Progress<ApplicationStatus>(), CancellationToken.None),
                Throws.InstanceOf<OpenAstroAra.Core.Model.SequenceEntityFailedException>());
        }

        [Test]
        public void A_clone_keeps_whether_it_waits() {
            var step = new CoolCamera(new Mock<ICameraMediator>().Object) { Temperature = -10, WaitForTemperature = false };
            Assert.That(((CoolCamera)step.Clone()).WaitForTemperature, Is.False);
        }
    }
}
