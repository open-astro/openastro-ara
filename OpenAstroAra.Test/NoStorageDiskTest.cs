#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.Trigger.Autofocus;
using OpenAstroAra.Sequencer.Trigger.Guider;
using OpenAstroAra.Sequencer.Utility;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Equipment;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// No storage disk, no imaging. 2026-10-16 the store drive fell off mid-run: every exposure failed
    /// in an instant, each failed frame still fired the dither, and the user watched the guider
    /// "settling" for half an hour. A run now refuses to start without its disk, a disk lost mid-run
    /// parks the run awaiting the user, and a frame that was never taken does not dither.
    /// </summary>
    [TestFixture]
    public class NoStorageDiskTest {

        /// <summary>Reports on the caller's thread (see StartGuidingPauseTest).</summary>
        private sealed class SyncProgress(Action<ApplicationStatus> report) : IProgress<ApplicationStatus> {
            public void Report(ApplicationStatus value) => report(value);
        }

        private static readonly SequenceStartRequestDto Start =
            new(DryRun: false, StartFromInstructionIndex: null, ContinueOnRecoverableErrors: false);

        private static InMemoryProfileStore StoreAt(string saveDir = "/media/openastroara") {
            var store = new InMemoryProfileStore();
            store.PutStorageSettings(store.GetStorageSettings() with { SaveDirectory = saveDir });
            return store;
        }

        private static Mock<IBahtinovFocusService> NoReadout() {
            var readout = new Mock<IBahtinovFocusService>();
            readout.SetupGet(r => r.IsActive).Returns(false);
            return readout;
        }

        // ── the run-start gate ──────────────────────────────────────────────

        [Test]
        public async Task A_run_without_its_disk_is_refused_and_never_started() {
            var sequencer = new Mock<ISequencerService>(MockBehavior.Strict);
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.StoreRefusal()).Returns("No storage disk: nothing is mounted at /media/openastroara.");

            var result = await SequenceEndpoints.StartSequenceAsync(Guid.NewGuid(), Start, null,
                sequencer.Object, NoReadout().Object, camera.Object, CancellationToken.None);

            var problem = result as ProblemHttpResult;
            Assert.That(problem, Is.Not.Null, "a Problem response");
            Assert.That(problem!.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That(problem.ProblemDetails.Title, Is.EqualTo("store_unavailable"), "the wire token");
            Assert.That(problem.ProblemDetails.Detail, Does.Contain("No storage disk"), "the client shows the daemon's words");
            sequencer.VerifyNoOtherCalls();
        }

        [Test]
        public async Task A_run_with_its_disk_starts() {
            var id = Guid.NewGuid();
            var accepted = new OperationAcceptedDto(Guid.NewGuid(), "sequence.start", DateTimeOffset.UtcNow, null);
            var sequencer = new Mock<ISequencerService>();
            sequencer.Setup(s => s.StartAsync(id, It.IsAny<SequenceStartRequestDto>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(accepted);
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.StoreRefusal()).Returns((string?)null);

            var result = await SequenceEndpoints.StartSequenceAsync(id, Start, null,
                sequencer.Object, NoReadout().Object, camera.Object, CancellationToken.None);

            Assert.That((result as Accepted<OperationAcceptedDto>)?.Value, Is.SameAs(accepted));
        }

        [Test]
        public void An_ejected_store_is_a_refusal_naming_the_mount_point() {
            using var camera = new CameraService(profileStore: StoreAt()) { StoreMountedProbe = () => false };
            Assert.That(camera.StoreRefusal(), Does.StartWith("No storage disk: nothing is mounted at /media/openastroara")
                .And.Contain("before imaging"));
        }

        [Test]
        public void A_mounted_store_or_an_unreadable_mount_table_is_no_refusal() {
            using var mounted = new CameraService(profileStore: StoreAt()) { StoreMountedProbe = () => true };
            Assert.That(mounted.StoreRefusal(), Is.Null);
            // Unknown (no /proc — a dev box) never refuses: a probe fault must not cost the user a night.
            using var unknown = new CameraService(profileStore: StoreAt()) { StoreMountedProbe = () => null };
            Assert.That(unknown.StoreRefusal(), Is.Null);
        }

        [Test]
        public void A_save_directory_off_the_store_is_no_refusal() {
            using var camera = new CameraService(profileStore: StoreAt("/srv/frames")) { StoreMountedProbe = () => false };
            Assert.That(camera.StoreRefusal(), Is.Null);
        }

        [Test]
        public void A_critically_full_disk_under_abort_is_a_refusal() {
            // ~1 EiB critical threshold: every real volume reads critical; the store probe says "unknown"
            // so the full-disk arm, not the ejected arm, answers.
            var store = StoreAt(System.IO.Path.GetTempPath());
            store.PutStorageSettings(store.GetStorageSettings() with { MinFreeDiskWarnGb = 2_000_000, MinFreeDiskCriticalGb = 1_000_000 });
            store.PutSafetyPolicies(store.GetSafetyPolicies() with { OnDiskSpaceCritical = "abort" });
            using var camera = new CameraService(profileStore: store) { StoreMountedProbe = () => null };
            Assert.That(camera.StoreRefusal(), Does.StartWith("The storage disk is full"));
        }

        [Test]
        public async Task A_sequence_capture_into_an_ejected_store_throws_the_typed_refusal() {
            using var camera = new CameraService(profileStore: StoreAt()) { StoreMountedProbe = () => false };
            var ex = await Assert.ThrowsAsync<StoreUnavailableException>(() =>
                camera.CaptureImage(new CaptureSequence(), CancellationToken.None, null, "M31"));
            Assert.That(ex!.Message, Does.StartWith("No storage disk"));
        }

        // ── a disk lost mid-run ────────────────────────────────────────────

        private static Mock<ICameraMediator> ConnectedCamera() {
            var camera = new Mock<ICameraMediator>();
            camera.Setup(c => c.GetInfo()).Returns(new OpenAstroAra.Equipment.Equipment.MyCamera.CameraInfo { Connected = true });
            return camera;
        }

        private static (TakeExposure Item, PauseGate Gate) Rig(IImagingMediator imaging) {
            var gate = new PauseGate();
            var root = new SequenceRootContainer { PauseGate = gate };
            var item = new TakeExposure(ConnectedCamera().Object, imaging) { ExposureTime = 1 };
            item.AttachNewParent(root);
            return (item, gate);
        }

        [Test]
        public async Task A_disk_lost_mid_run_parks_the_run_and_resume_takes_the_frame() {
            var imaging = new Mock<IImagingMediator>();
            var calls = 0;
            imaging.Setup(m => m.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<string>()))
                .Returns(() => ++calls == 1
                    ? throw new StoreUnavailableException("No storage disk: nothing is mounted at /media/openastroara, so there is nowhere to save frames.")
                    : Task.FromResult(new Mock<IExposureData>().Object));
            var (item, gate) = Rig(imaging.Object);
            string? status = null;
            var progress = new SyncProgress(s => status = s.Status);

            var execute = item.Execute(progress, CancellationToken.None);
            await Task.Delay(50);
            Assert.That(execute.IsCompleted, Is.False, "parked inside the pause, not a failed frame");
            Assert.That(gate.PendingKind, Is.EqualTo(PauseKind.AwaitingUser));
            Assert.That(status, Does.Contain("No storage disk").And.Contain("press Resume"));

            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(calls, Is.EqualTo(2), "Resume took the frame again");
            Assert.That(item.ExposureCount, Is.EqualTo(1), "one frame taken, not two");
        }

        [Test]
        public async Task Without_a_pause_gate_a_lost_disk_fails_the_instruction() {
            var imaging = new Mock<IImagingMediator>();
            imaging.Setup(m => m.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<string>()))
                .ThrowsAsync(new StoreUnavailableException("No storage disk"));
            var item = new TakeExposure(ConnectedCamera().Object, imaging.Object) { ExposureTime = 1 };
            await Assert.ThrowsAsync<StoreUnavailableException>(() => item.Execute(new SyncProgress(_ => { }), CancellationToken.None));
        }

        [Test]
        public async Task Any_other_capture_failure_still_fails_the_frame() {
            var imaging = new Mock<IImagingMediator>();
            imaging.Setup(m => m.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<string>()))
                .ThrowsAsync(new InvalidOperationException("device timeout"));
            var (item, gate) = Rig(imaging.Object);
            await Assert.ThrowsAsync<InvalidOperationException>(() => item.Execute(new SyncProgress(_ => { }), CancellationToken.None));
            Assert.That(gate.IsPauseRequested, Is.False);
        }

        // ── a frame never taken does not dither ────────────────────────────

        private static TakeExposure ExposureWith(SequenceEntityStatus status) {
            var exp = HeadlessSequencerFactory.WithDefaults().GetItem<TakeExposure>()!;
            exp.Status = status;
            return exp;
        }

        [TestCase(SequenceEntityStatus.FAILED)]
        [TestCase(SequenceEntityStatus.SKIPPED)]
        [TestCase(SequenceEntityStatus.DISABLED)]
        public void A_frame_not_taken_neither_counts_nor_dithers(SequenceEntityStatus status) {
            var trigger = new DitherAfterExposures { AfterExposures = 1 };
            Assert.That(trigger.ShouldTrigger(ExposureWith(status), null), Is.False);
            Assert.That(trigger.ShouldTrigger(ExposureWith(SequenceEntityStatus.FINISHED), null), Is.True,
                "the next frame taken is the first of the group");
        }

        /// <summary>Counts fires instead of dithering: no guider needed.</summary>
        private sealed class CountingDither : DitherAfterExposures {
            public int Fired;
            public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
                Fired++;
                return Task.CompletedTask;
            }
        }

        [Test]
        public async Task A_failed_last_frame_of_a_looping_block_does_not_dither_on_the_next_pass() {
            // The SmartExposure shape: the exposure is the block's last item, so its trigger pass runs
            // on the NEXT iteration — after the loop's reset has put the failed item back to CREATED.
            var imaging = new Mock<IImagingMediator>();
            var captures = 0;
            imaging.Setup(m => m.CaptureImage(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<string>()))
                .Returns(() => ++captures <= 2
                    ? throw new InvalidOperationException("device timeout")
                    : Task.FromResult(new Mock<IExposureData>().Object));
            var block = new SequentialContainer();
            block.Add(new LoopCondition { Iterations = 4 });
            var dither = new CountingDither { AfterExposures = 1 };
            block.Add(dither);
            block.Add(new TakeExposure(ConnectedCamera().Object, imaging.Object) {
                ExposureTime = 1, ErrorBehavior = OpenAstroAra.Sequencer.Utility.InstructionErrorBehavior.ContinueOnError,
            });

            await block.Run(new SyncProgress(_ => { }), CancellationToken.None);

            Assert.That(captures, Is.EqualTo(4));
            // Frames 1 and 2 failed, 3 and 4 were taken; frame 4's trigger pass never comes (the loop ends).
            Assert.That(dither.Fired, Is.EqualTo(1), "only the taken frame 3 dithers (before frame 4)");
        }

        [Test]
        public void A_failed_frame_does_not_count_toward_autofocus_after_exposures() {
            var trigger = new AutofocusAfterExposures { AfterExposures = 2 };
            Assert.That(trigger.ShouldTrigger(ExposureWith(SequenceEntityStatus.FINISHED), null), Is.False); // 1
            Assert.That(trigger.ShouldTrigger(ExposureWith(SequenceEntityStatus.FAILED), null), Is.False, "not counted");
            Assert.That(trigger.ShouldTrigger(ExposureWith(SequenceEntityStatus.FINISHED), null), Is.True); // 2
        }
    }
}
