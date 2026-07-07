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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Trigger.MeridianFlip;
using OpenAstroAra.Sequencer.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

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
        public async Task ShouldTrigger_skips_a_second_flip_for_the_same_target_within_11h() {
            // A successful flip records (lastFlipTime, lastFlipCoordinates); evaluating again for the same
            // target inside 11 h (with side-of-pier off) must not re-fire — the dedup guard.
            var coords = new Coordinates(Angle.ByHours(5), Angle.ByDegree(20), Epoch.J2000);
            telescopeMediatorMock.Setup(x => x.GetCurrentPosition()).Returns(coords);
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo {
                Connected = true,
                TrackingEnabled = true,
                TimeToMeridianFlip = 1,
                Coordinates = coords,
            });
            executorMock
                .Setup(x => x.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<TimeSpan>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var sut = CreateSUT();
            await sut.Execute(null!, new Progress<ApplicationStatus>(), CancellationToken.None);

            var should = sut.ShouldTrigger(null, NextItem(TimeSpan.Zero).Object);
            Assert.That(should, Is.False);
        }

        [Test]
        public void Execute_throws_when_no_target_coordinates_are_available() {
            // Context has none AND the mount returns null (disconnected mid-decision) — fail loud, don't
            // continue un-flipped.
            telescopeMediatorMock.Setup(x => x.GetCurrentPosition()).Returns((Coordinates)null!);
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = true, TimeToMeridianFlip = 1 });
            var sut = CreateSUT();
            Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.Execute(null!, new Progress<ApplicationStatus>(), CancellationToken.None));
        }

        [Test]
        public void Execute_throws_when_the_executor_reports_failure_and_no_pause_gate_is_wired() {
            // No root/gate reachable (standalone execution): the only safe halt is
            // the old throw → Failed path. Continuing un-flipped is never an option.
            var coords = new Coordinates(Angle.ByHours(5), Angle.ByDegree(20), Epoch.J2000);
            telescopeMediatorMock.Setup(x => x.GetCurrentPosition()).Returns(coords);
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = true, TimeToMeridianFlip = 1, Coordinates = coords });
            executorMock
                .Setup(x => x.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<TimeSpan>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var sut = CreateSUT();
            Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.Execute(null!, new Progress<ApplicationStatus>(), CancellationToken.None));
        }

        [Test]
        public async Task Execute_pauses_the_run_awaiting_user_instead_of_throwing_when_a_gate_is_wired() {
            // §58.12 — with the run's pause gate reachable through the root, a failed
            // flip arms it as AwaitingUser (the executor has already safe-rested the
            // mount and notified) instead of failing the whole run. The engine then
            // suspends before the next instruction; resume re-attempts the flip.
            var coords = new Coordinates(Angle.ByHours(5), Angle.ByDegree(20), Epoch.J2000);
            telescopeMediatorMock.Setup(x => x.GetCurrentPosition()).Returns(coords);
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = true, TimeToMeridianFlip = 1, Coordinates = coords });
            executorMock
                .Setup(x => x.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<TimeSpan>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var gate = new PauseGate();
            var root = new SequenceRootContainer { PauseGate = gate };

            var sut = CreateSUT();
            await sut.Execute(root, new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(gate.IsPauseRequested, Is.True, "the failed flip must arm the gate");
            Assert.That(gate.PendingKind, Is.EqualTo(PauseKind.AwaitingUser));
        }

        [Test]
        public void Execute_still_throws_on_failure_when_the_root_has_no_gate() {
            // A root without a wired gate (pause unavailable) must keep the halt path.
            var coords = new Coordinates(Angle.ByHours(5), Angle.ByDegree(20), Epoch.J2000);
            telescopeMediatorMock.Setup(x => x.GetCurrentPosition()).Returns(coords);
            telescopeMediatorMock.Setup(x => x.GetInfo()).Returns(new TelescopeInfo { Connected = true, TimeToMeridianFlip = 1, Coordinates = coords });
            executorMock
                .Setup(x => x.MeridianFlip(It.IsAny<Coordinates>(), It.IsAny<TimeSpan>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var root = new SequenceRootContainer(); // PauseGate stays null

            Assert.ThrowsAsync<InvalidOperationException>(
                () => CreateSUT().Execute(root, new Progress<ApplicationStatus>(), CancellationToken.None));
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
