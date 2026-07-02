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
using OpenAstroAra.Equipment.Equipment.MyRotator;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Platesolving;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §28/§38 — <see cref="CenterAndRotate"/> execution through the
    /// <see cref="ICenteringExecutor"/> seam: the centre half runs for real; the
    /// rotate half fails loudly with a rotator connected and is a documented skip
    /// without one (NINA itself only rotates with a rotator).
    /// </summary>
    [TestFixture]
    public class CenterAndRotateExecutionTest {

        private static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>();

        private static InputCoordinates SomeTarget() =>
            new InputCoordinates(new Coordinates(Angle.ByHours(5.5), Angle.ByDegree(-5.4), Epoch.J2000));

        private static Mock<IRotatorMediator> Rotator(bool connected) {
            var rotator = new Mock<IRotatorMediator>();
            rotator.Setup(r => r.GetInfo()).Returns(new RotatorInfo { Connected = connected });
            return rotator;
        }

        [Test]
        public void Unwired_executor_fails_loudly() {
            var item = new CenterAndRotate(centeringExecutor: null) { Coordinates = SomeTarget() };
            Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => item.Execute(NoProgress, CancellationToken.None));
        }

        [Test]
        public void Missing_coordinates_fail_loudly() {
            var executor = new Mock<ICenteringExecutor>();
            var item = new CenterAndRotate(executor.Object) { Coordinates = null! };
            Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => item.Execute(NoProgress, CancellationToken.None));
            executor.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Converged_centering_completes_and_passes_the_target() {
            var executor = new Mock<ICenteringExecutor>();
            Coordinates? seen = null;
            executor.Setup(e => e.CenterAsync(It.IsAny<Coordinates>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback<Coordinates, IProgress<ApplicationStatus>, CancellationToken>((c, _, _) => seen = c)
                .ReturnsAsync(true);

            var target = SomeTarget();
            var item = new CenterAndRotate(executor.Object) { Coordinates = target };
            await item.Execute(NoProgress, CancellationToken.None);

            Assert.That(seen, Is.Not.Null);
            Assert.That(seen!.RA, Is.EqualTo(target.Coordinates.RA).Within(1e-9), "the plan's own coordinates drive the centre");
            Assert.That(seen.Dec, Is.EqualTo(target.Coordinates.Dec).Within(1e-9));
        }

        [Test]
        public void Unconverged_centering_fails_the_instruction() {
            // An un-centred target would quietly ruin every subsequent frame — a false
            // return from the executor must fail the step, never continue.
            var executor = new Mock<ICenteringExecutor>();
            executor.Setup(e => e.CenterAsync(It.IsAny<Coordinates>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var item = new CenterAndRotate(executor.Object) { Coordinates = SomeTarget() };
            Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => item.Execute(NoProgress, CancellationToken.None));
        }

        [Test]
        public void Connected_rotator_fails_loudly_instead_of_skipping_rotation() {
            // With a rotator the user expects the rotate half — until rotation is wired,
            // failing loudly beats silently mis-framing.
            var executor = new Mock<ICenteringExecutor>();
            var item = new CenterAndRotate(executor.Object, Rotator(connected: true).Object) { Coordinates = SomeTarget() };
            Assert.ThrowsAsync<SequenceEntityFailedException>(
                () => item.Execute(NoProgress, CancellationToken.None));
            executor.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Disconnected_rotator_centres_without_rotation() {
            // No rotator = NINA itself never rotates; centring alone is faithful.
            var executor = new Mock<ICenteringExecutor>();
            executor.Setup(e => e.CenterAsync(It.IsAny<Coordinates>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var item = new CenterAndRotate(executor.Object, Rotator(connected: false).Object) {
                Coordinates = SomeTarget(),
                PositionAngle = 45,
            };
            await item.Execute(NoProgress, CancellationToken.None);
            executor.Verify(e => e.CenterAsync(It.IsAny<Coordinates>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void Clone_carries_the_wiring() {
            // NINA runs CLONES of prototypes — losing the executor on Clone would turn
            // every real run back into the fail-loudly path.
            var executor = new Mock<ICenteringExecutor>();
            executor.Setup(e => e.CenterAsync(It.IsAny<Coordinates>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var prototype = new CenterAndRotate(executor.Object) { Coordinates = SomeTarget() };
            var clone = (CenterAndRotate)prototype.Clone();
            Assert.DoesNotThrowAsync(() => clone.Execute(NoProgress, CancellationToken.None));
            executor.Verify(e => e.CenterAsync(It.IsAny<Coordinates>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
