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
using OpenAstroAra.Equipment.Equipment.MyRotator;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.Rotator;
using OpenAstroAra.Sequencer.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Rotate camera by hand — the no-rotator framing step: starts the daemon's readout, parks the run
    /// awaiting the user INSIDE the pause, and stops the readout on Resume or abort; a no-op with a rotator.
    /// </summary>
    [TestFixture]
    public class RotateCameraByHandTest {
        private static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>();

        private static Mock<IRotatorMediator> Rotator(bool connected) {
            var rotator = new Mock<IRotatorMediator>();
            rotator.Setup(r => r.GetInfo()).Returns(new RotatorInfo { Connected = connected });
            return rotator;
        }

        /// <summary>Reports on the caller's thread: <see cref="Progress{T}"/> posts to the thread pool,
        /// and under parallel fixtures (#1265) a short delay did not always see the status (CI, #1269).</summary>
        private sealed class SyncProgress(Action<ApplicationStatus> report) : IProgress<ApplicationStatus> {
            public void Report(ApplicationStatus value) => report(value);
        }

        private static (RotateCameraByHand Item, SequenceRootContainer Root, PauseGate Gate) Rig(IRotationAssistExecutor assist, bool rotatorConnected = false) {
            var gate = new PauseGate();
            var root = new SequenceRootContainer { PauseGate = gate };
            var item = new RotateCameraByHand(assist, Rotator(rotatorConnected).Object) { PositionAngle = 299 };
            item.AttachNewParent(root);
            return (item, root, gate);
        }

        [Test]
        public async Task With_a_rotator_connected_the_step_is_a_no_op() {
            var assist = new Mock<IRotationAssistExecutor>(MockBehavior.Strict);
            var (item, _, gate) = Rig(assist.Object, rotatorConnected: true);

            await item.Execute(NoProgress, CancellationToken.None);

            Assert.That(gate.IsPauseRequested, Is.False, "Center and Rotate rotates for real — nothing to wait for");
            assist.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Unwired_assist_fails_loudly() {
            var bare = new RotateCameraByHand(null, Rotator(false).Object) { PositionAngle = 10 };
            bare.AttachNewParent(new SequenceRootContainer { PauseGate = new PauseGate() });
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => bare.Execute(NoProgress, CancellationToken.None));
        }

        [Test]
        public async Task Without_a_pause_gate_the_step_skips_rather_than_hang() {
            var assist = new Mock<IRotationAssistExecutor>(MockBehavior.Strict);
            var item = new RotateCameraByHand(assist.Object, Rotator(false).Object) { PositionAngle = 45 };
            item.AttachNewParent(new SequenceRootContainer()); // no gate

            await item.Execute(NoProgress, CancellationToken.None);

            assist.VerifyNoOtherCalls();
        }

        [Test]
        public async Task Starts_the_readout_parks_the_run_awaiting_the_user_and_stops_on_resume() {
            var assist = new Mock<IRotationAssistExecutor>();
            double? started = null;
            assist.Setup(a => a.StartAsync(It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .Callback<double, CancellationToken>((pa, _) => started = pa)
                .Returns(Task.CompletedTask);
            assist.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
            var (item, _, gate) = Rig(assist.Object);
            string? status = null;
            var progress = new SyncProgress(s => status = s.Status);

            var execute = item.Execute(progress, CancellationToken.None);
            // The step is sitting inside the pause: armed as AwaitingUser, readout running, not finished.
            await Task.Delay(50);
            Assert.That(execute.IsCompleted, Is.False);
            Assert.That(gate.IsPauseRequested, Is.True);
            Assert.That(gate.PendingKind, Is.EqualTo(PauseKind.AwaitingUser));
            Assert.That(started, Is.EqualTo(299));
            assist.Verify(a => a.StopAsync(), Times.Never);

            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));

            assist.Verify(a => a.StopAsync(), Times.Once, "Resume ends the readout before the next instruction takes the camera");
            Assert.That(gate.IsPauseRequested, Is.False);
            Assert.That(status, Does.Contain("299"));
        }

        [Test]
        public async Task An_abort_during_the_pause_stops_the_readout_and_propagates() {
            var assist = new Mock<IRotationAssistExecutor>();
            assist.Setup(a => a.StartAsync(It.IsAny<double>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            assist.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
            var (item, _, gate) = Rig(assist.Object);
            using var cts = new CancellationTokenSource();

            var execute = item.Execute(NoProgress, cts.Token);
            await Task.Delay(50);
            Assert.That(gate.IsPauseRequested, Is.True);
            await cts.CancelAsync();

            await Assert.CatchAsync<OperationCanceledException>(() => execute.WaitAsync(TimeSpan.FromSeconds(5)));
            assist.Verify(a => a.StopAsync(), Times.Once);
        }

        [Test]
        public async Task A_readout_already_running_by_hand_is_taken_over_not_a_failed_step() {
            // The panel's Start (REST) left a loop running; the service refuses a second Start with
            // "already running". The step restarts it toward its own angle and parks as usual.
            var assist = new Mock<IRotationAssistExecutor>();
            var starts = 0;
            assist.Setup(a => a.StartAsync(It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .Returns(() => ++starts == 1
                    ? Task.FromException(new InvalidOperationException("the rotation readout is already running"))
                    : Task.CompletedTask);
            assist.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
            var (item, _, gate) = Rig(assist.Object);

            var execute = item.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);
            for (var i = 0; i < 100 && !gate.IsPauseRequested; i++) {
                await Task.Delay(10);
            }
            Assert.That(gate.IsPauseRequested, Is.True);

            Assert.That(starts, Is.EqualTo(2), "stopped and restarted toward this step's angle");
            assist.Verify(a => a.StopAsync(), Times.Once);
            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));
            assist.Verify(a => a.StopAsync(), Times.Exactly(2));
        }

        [Test]
        public async Task A_rig_that_cannot_solve_skips_the_step_instead_of_parking_the_run() {
            // No plate solver / no optics: the readout refuses at start; the step warns and moves on so
            // Center and Rotate still centres and the run never waits on a readout that can only fail.
            var assist = new Mock<IRotationAssistExecutor>();
            assist.Setup(a => a.StartAsync(It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RotationAssistNotReadyException("the plate solver binary is missing at /usr/bin/astap_cli"));
            var (item, _, gate) = Rig(assist.Object);
            string? status = null;
            var progress = new SyncProgress(s => status = s.Status);

            await item.Execute(progress, CancellationToken.None);

            Assert.That(gate.IsPauseRequested, Is.False);
            assist.Verify(a => a.StopAsync(), Times.Never);
            Assert.That(status, Does.Contain("skipped"));
        }

        [Test]
        public void Position_angle_normalises_and_clones() {
            var item = new RotateCameraByHand(null, null) { PositionAngle = -61 };
            Assert.That(item.PositionAngle, Is.EqualTo(299));
            var clone = (RotateCameraByHand)item.Clone();
            Assert.That(clone.PositionAngle, Is.EqualTo(299));
        }
    }
}
