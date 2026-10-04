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
using OpenAstroAra.Equipment.Equipment.MyGuider;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.Guider;
using OpenAstroAra.Sequencer.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Start Guiding on a rig with a guider: a refusal parks the run awaiting the user instead of letting
    /// the night run unguided (2026-10-03), and Resume tries again.
    /// </summary>
    [TestFixture]
    public class StartGuidingPauseTest {
        private static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>();

        /// <summary>Reports on the caller's thread: <see cref="Progress{T}"/> posts to the thread pool,
        /// and a short delay is a race under parallel fixtures (#1265).</summary>
        private sealed class SyncProgress(Action<ApplicationStatus> report) : IProgress<ApplicationStatus> {
            public void Report(ApplicationStatus value) => report(value);
        }

        private static Mock<IGuiderMediator> Guider() {
            var guider = new Mock<IGuiderMediator>();
            // Validate() (run on attach) reads the guider's info; a connected guider validates clean.
            guider.Setup(g => g.GetInfo()).Returns(new GuiderInfo { Connected = true, CanClearCalibration = true });
            return guider;
        }

        private static (StartGuiding Item, PauseGate Gate) Rig(IGuiderMediator guider) {
            var gate = new PauseGate();
            var root = new SequenceRootContainer { PauseGate = gate };
            var item = new StartGuiding(guider);
            item.AttachNewParent(root);
            return (item, gate);
        }

        [Test]
        public async Task Guiding_that_starts_first_time_does_not_pause() {
            var guider = Guider();
            guider.Setup(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            var (item, gate) = Rig(guider.Object);

            await item.Execute(NoProgress, CancellationToken.None);

            Assert.That(gate.IsPauseRequested, Is.False);
            guider.Verify(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task A_refused_start_parks_the_run_and_resume_tries_again() {
            var guider = Guider();
            var calls = 0;
            guider.Setup(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ++calls >= 2);
            var (item, gate) = Rig(guider.Object);
            string? status = null;
            var progress = new SyncProgress(s => status = s.Status);

            var execute = item.Execute(progress, CancellationToken.None);
            await Task.Delay(50);
            Assert.That(execute.IsCompleted, Is.False, "parked inside the pause, not failed");
            Assert.That(gate.IsPauseRequested, Is.True);
            Assert.That(gate.PendingKind, Is.EqualTo(PauseKind.AwaitingUser));
            Assert.That(calls, Is.EqualTo(1));

            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(calls, Is.EqualTo(2), "Resume retried and the second attempt guided");
            Assert.That(gate.IsPauseRequested, Is.False);
            Assert.That(status, Does.Contain("Guiding did not start"));
        }

        [Test]
        public async Task A_guider_exception_is_a_pause_too_with_its_message() {
            var guider = Guider();
            var calls = 0;
            guider.Setup(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns(() => ++calls == 1 ? throw new InvalidOperationException("polar-alignment session in progress") : Task.FromResult(true));
            var (item, gate) = Rig(guider.Object);
            string? status = null;
            var progress = new SyncProgress(s => status = s.Status);

            var execute = item.Execute(progress, CancellationToken.None);
            await Task.Delay(50);
            Assert.That(gate.IsPauseRequested, Is.True);
            Assert.That(status, Does.Contain("polar-alignment session in progress"));

            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(calls, Is.EqualTo(2));
        }

        [Test]
        public async Task An_abort_during_the_pause_propagates() {
            var guider = Guider();
            guider.Setup(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
            var (item, gate) = Rig(guider.Object);
            using var cts = new CancellationTokenSource();

            var execute = item.Execute(NoProgress, cts.Token);
            await Task.Delay(50);
            Assert.That(gate.IsPauseRequested, Is.True);
            await cts.CancelAsync();

            await Assert.CatchAsync<OperationCanceledException>(() => execute.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public async Task Without_a_pause_gate_a_refused_start_fails_the_instruction() {
            var guider = Guider();
            guider.Setup(g => g.StartGuiding(false, It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
            var item = new StartGuiding(guider.Object);

            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => item.Execute(NoProgress, CancellationToken.None));
        }
    }
}
