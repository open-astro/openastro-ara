#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Trigger.MeridianFlip;
using System;

namespace OpenAstroAra.Test.Sequencer.Trigger.MeridianFlip {

    /// <summary>
    /// §58 — <see cref="MeridianFlipTrigger"/> decision logic (re-ported headless). Exercises
    /// <see cref="MeridianFlipTrigger.ShouldTrigger"/> against a mocked <see cref="ITelescopeMediator"/> +
    /// profile, with no equipment touched (the flip orchestration lives behind <see cref="IMeridianFlipExecutor"/>).
    /// The full side-of-pier projection matrix is a follow-up (it needs coordinate/sidereal-time fixtures).
    /// </summary>
    [TestFixture]
    public class MeridianFlipTriggerTest {
        private Mock<IProfileService> profileServiceMock = null!;
        private Mock<ITelescopeMediator> telescopeMediatorMock = null!;
        private Mock<IMeridianFlipExecutor> executorMock = null!;
        private Mock<IMeridianFlipSettings> settingsMock = null!;

        [SetUp]
        public void Setup() {
            profileServiceMock = new Mock<IProfileService>();
            telescopeMediatorMock = new Mock<ITelescopeMediator>();
            executorMock = new Mock<IMeridianFlipExecutor>();
            settingsMock = new Mock<IMeridianFlipSettings>();
            settingsMock.SetupGet(m => m.UseSideOfPier).Returns(false);
            profileServiceMock.SetupGet(m => m.ActiveProfile.MeridianFlipSettings).Returns(settingsMock.Object);
        }

        private MeridianFlipTrigger CreateSUT() =>
            new(profileServiceMock.Object, telescopeMediatorMock.Object, executorMock.Object);

        private void SetTelescope(double timeToMeridianFlipHours, bool connected = true, bool tracking = true, bool parked = false, bool atHome = false) {
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo {
                Connected = connected,
                TrackingEnabled = tracking,
                AtPark = parked,
                AtHome = atHome,
                TimeToMeridianFlip = timeToMeridianFlipHours,
            });
        }

        private static Mock<ISequenceItem> NextItem(TimeSpan duration) {
            var m = new Mock<ISequenceItem>();
            m.Setup(x => x.GetEstimatedDuration()).Returns(duration);
            return m;
        }

        [Test]
        public void Clone_produces_a_distinct_instance_preserving_metadata() {
            var initial = CreateSUT();
            initial.Icon = "MeridianFlipSVG";

            var sut = (MeridianFlipTrigger)initial.Clone();

            Assert.That(sut, Is.Not.SameAs(initial));
            Assert.That(sut.Icon, Is.EqualTo("MeridianFlipSVG"));
        }

        [Test]
        public void ShouldTrigger_returns_false_when_the_telescope_is_disconnected() {
            SetTelescope(timeToMeridianFlipHours: 10, connected: false);
            Assert.That(CreateSUT().ShouldTrigger(null, NextItem(TimeSpan.FromMinutes(5)).Object), Is.False);
        }

        [Test]
        public void ShouldTrigger_returns_false_when_time_to_meridian_is_NaN() {
            SetTelescope(timeToMeridianFlipHours: double.NaN);
            Assert.That(CreateSUT().ShouldTrigger(null, NextItem(TimeSpan.FromMinutes(5)).Object), Is.False);
        }

        [Test]
        public void ShouldTrigger_returns_false_when_parked() {
            SetTelescope(timeToMeridianFlipHours: 0, parked: true);
            Assert.That(CreateSUT().ShouldTrigger(null, NextItem(TimeSpan.Zero).Object), Is.False);
        }

        [Test]
        public void ShouldTrigger_returns_false_when_at_home() {
            SetTelescope(timeToMeridianFlipHours: 0, atHome: true);
            Assert.That(CreateSUT().ShouldTrigger(null, NextItem(TimeSpan.Zero).Object), Is.False);
        }

        [Test]
        public void ShouldTrigger_returns_false_when_not_tracking() {
            SetTelescope(timeToMeridianFlipHours: 0, tracking: false);
            Assert.That(CreateSUT().ShouldTrigger(null, NextItem(TimeSpan.Zero).Object), Is.False);
        }

        [Test]
        public void ShouldTrigger_returns_true_when_time_to_meridian_flip_has_arrived() {
            settingsMock.SetupGet(m => m.MinutesAfterMeridian).Returns(5);
            settingsMock.SetupGet(m => m.MaxMinutesAfterMeridian).Returns(5);
            settingsMock.SetupGet(m => m.PauseTimeBeforeMeridian).Returns(0);
            SetTelescope(timeToMeridianFlipHours: 0);
            Assert.That(CreateSUT().ShouldTrigger(null, NextItem(TimeSpan.Zero).Object), Is.True);
        }

        // No pause, no side-of-pier: a flip fires once the remaining time falls into the [min, max] window.
        // minAfter/maxAfter in minutes; remaining is the telescope's reported minutes-to-flip.
        [Test]
        [TestCase(5, 5, -1, true)]
        [TestCase(5, 5, 0, true)]
        [TestCase(5, 5, 2, true)]
        [TestCase(5, 5, 5, true)]
        [TestCase(5, 10, 8, false)]
        [TestCase(5, 10, 10, false)]
        [TestCase(5, 10, 11, false)]
        public void ShouldTrigger_no_pause_no_pierside_flips_inside_the_window(double minAfter, double maxAfter, double remainingMinutes, bool expectFlip) {
            settingsMock.SetupGet(m => m.MinutesAfterMeridian).Returns(minAfter);
            settingsMock.SetupGet(m => m.MaxMinutesAfterMeridian).Returns(maxAfter);
            settingsMock.SetupGet(m => m.PauseTimeBeforeMeridian).Returns(0);
            SetTelescope(timeToMeridianFlipHours: TimeSpan.FromMinutes(remainingMinutes).TotalHours);

            var should = CreateSUT().ShouldTrigger(null, NextItem(TimeSpan.FromMinutes(minAfter)).Object);

            Assert.That(should, Is.EqualTo(expectFlip));
        }

        [Test]
        public void Validate_fails_when_the_telescope_is_disconnected() {
            SetTelescope(timeToMeridianFlipHours: 10, connected: false);
            var sut = CreateSUT();
            Assert.That(sut.Validate(), Is.False);
            Assert.That(sut.Issues, Is.Not.Empty);
        }

        [Test]
        public void Validate_passes_when_the_telescope_is_connected() {
            SetTelescope(timeToMeridianFlipHours: 10);
            var sut = CreateSUT();
            Assert.That(sut.Validate(), Is.True);
            Assert.That(sut.Issues, Is.Empty);
        }

        [Test]
        public void Validate_fails_when_minutes_after_meridian_exceeds_max() {
            // pause_after > max_wait inverts the flip-time window — caught at setup.
            settingsMock.SetupGet(m => m.MinutesAfterMeridian).Returns(15);
            settingsMock.SetupGet(m => m.MaxMinutesAfterMeridian).Returns(10);
            SetTelescope(timeToMeridianFlipHours: 10);
            var sut = CreateSUT();
            Assert.That(sut.Validate(), Is.False);
            Assert.That(sut.Issues, Is.Not.Empty);
        }
    }
}
