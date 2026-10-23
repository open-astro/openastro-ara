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
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Platesolving;
using OpenAstroAra.Sequencer.SequenceItem.Telescope;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1446 — the sequence's Slew step honours Settings → Plate Solving → "Center target after
    /// slew". On 2026-10-23 a run slewed to the Helix Nebula with the setting on, the mount reported
    /// the target, and the frames showed a field 5° away in RA: the slew never solved, so nothing
    /// synced or re-slewed. With the setting on the step now centres through the same
    /// <see cref="ICenteringExecutor"/> as Center and Rotate (slew, plate-solve, sync, re-slew).
    /// </summary>
    [TestFixture]
    public class SlewScopeToRaDecCenteringTest {

        private static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>();

        private static readonly string[] StopCentreResume = { "stop_guiding", "center", "start_guiding" };

        private static InputCoordinates Helix() =>
            new InputCoordinates(new Coordinates(Angle.ByHours(22.494042), Angle.ByDegree(-20.837111), Epoch.J2000));

        private static Mock<ITelescopeMediator> Mount() {
            var mount = new Mock<ITelescopeMediator>();
            mount.Setup(m => m.GetInfo()).Returns(new TelescopeInfo { Connected = true, AtPark = false });
            mount.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            return mount;
        }

        private static Mock<IGuiderMediator> Guider(bool wasGuiding, List<string>? order = null) {
            var guider = new Mock<IGuiderMediator>();
            guider.Setup(g => g.StopGuiding(It.IsAny<CancellationToken>())).Returns(() => {
                order?.Add("stop_guiding");
                return Task.FromResult(wasGuiding);
            });
            guider.Setup(g => g.StartGuiding(It.IsAny<bool>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns(() => {
                    order?.Add("start_guiding");
                    return Task.FromResult(true);
                });
            return guider;
        }

        private static IProfileService Profile(bool centerAfterSlew) {
            var settings = new Mock<IPlateSolveSettings>();
            settings.SetupGet(s => s.SlewToTarget).Returns(centerAfterSlew);
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile.PlateSolveSettings).Returns(settings.Object);
            return profileService.Object;
        }

        private static Mock<ICenteringExecutor> Centering(bool converges, List<string>? order = null) {
            var executor = new Mock<ICenteringExecutor>();
            executor.Setup(e => e.CenterAsync(It.IsAny<Coordinates>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns(() => {
                    order?.Add("center");
                    return Task.FromResult(converges);
                });
            return executor;
        }

        [Test]
        public async Task With_center_after_slew_on_the_step_centres_on_its_target() {
            var mount = Mount();
            var centering = Centering(converges: true);
            Coordinates? seen = null;
            centering.Setup(e => e.CenterAsync(It.IsAny<Coordinates>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Callback<Coordinates, IProgress<ApplicationStatus>, CancellationToken>((c, _, _) => seen = c)
                .ReturnsAsync(true);
            var target = Helix();
            var item = new SlewScopeToRaDec(mount.Object, Guider(wasGuiding: false).Object, Profile(centerAfterSlew: true), centering.Object) {
                Coordinates = target,
            };

            await item.Execute(NoProgress, CancellationToken.None);

            Assert.That(seen, Is.Not.Null, "the step never centred: a blind slew lands wherever the mount's pointing model says");
            Assert.That(seen!.RA, Is.EqualTo(target.Coordinates.RA).Within(1e-9));
            Assert.That(seen.Dec, Is.EqualTo(target.Coordinates.Dec).Within(1e-9));
            mount.Verify(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Never,
                "the centring executor owns the slew; a second blind slew first would only add a goto");
        }

        [Test]
        public async Task Centring_that_does_not_converge_fails_the_step() {
            // Same rule as Center and Rotate: an off-target field would quietly ruin every frame after it.
            var item = new SlewScopeToRaDec(Mount().Object, Guider(wasGuiding: false).Object, Profile(centerAfterSlew: true),
                Centering(converges: false).Object) { Coordinates = Helix() };

            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => item.Execute(NoProgress, CancellationToken.None));
        }

        [Test]
        public async Task With_center_after_slew_off_the_step_keeps_the_blind_slew() {
            var mount = Mount();
            var centering = Centering(converges: true);
            var item = new SlewScopeToRaDec(mount.Object, Guider(wasGuiding: false).Object, Profile(centerAfterSlew: false), centering.Object) {
                Coordinates = Helix(),
            };

            await item.Execute(NoProgress, CancellationToken.None);

            mount.Verify(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Once);
            centering.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Without_a_wired_centring_executor_the_step_keeps_the_blind_slew() {
            // Prototype construction (JSON resolve, validate) has no executor; the slew must still work.
            var mount = Mount();
            var item = new SlewScopeToRaDec(mount.Object, Guider(wasGuiding: false).Object, Profile(centerAfterSlew: true)) {
                Coordinates = Helix(),
            };

            await item.Execute(NoProgress, CancellationToken.None);

            mount.Verify(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Guiding_stops_before_the_centre_and_resumes_after_it() {
            var order = new List<string>();
            var item = new SlewScopeToRaDec(Mount().Object, Guider(wasGuiding: true, order).Object, Profile(centerAfterSlew: true),
                Centering(converges: true, order).Object) { Coordinates = Helix() };

            await item.Execute(NoProgress, CancellationToken.None);

            Assert.That(order, Is.EqualTo(StopCentreResume));
        }

        [Test]
        public async Task A_clone_keeps_the_centring_wiring() {
            // Run bodies execute clones of the factory prototype, so the clone must carry the deps.
            var centering = Centering(converges: true);
            var prototype = new SlewScopeToRaDec(Mount().Object, Guider(wasGuiding: false).Object, Profile(centerAfterSlew: true), centering.Object);
            var clone = (SlewScopeToRaDec)prototype.Clone();
            clone.Coordinates = Helix();

            await clone.Execute(NoProgress, CancellationToken.None);

            centering.Verify(e => e.CenterAsync(It.IsAny<Coordinates>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
