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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Background frame saving (NINA's async save): a sequencer frame is handed off once downloaded
    /// and written while the next exposure runs. Real writes to a temp store; the catalog insert is a
    /// mock the tests hold open or fail, so a save can be caught in flight or made to fail.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk
    public class BackgroundFrameSaveTest {

        private static readonly Guid Session = Guid.NewGuid();

        private string _root = string.Empty;
        private InMemoryProfileStore _profile = null!;
        private Mock<IFrameRepository> _frames = null!;
        private ConcurrentQueue<Guid> _inserted = null!;
        // The next insert waits on this when set; cleared by the test to let it through.
        private volatile TaskCompletionSource? _insertGate;
        private volatile bool _insertFails;
        private int _insertAttempts;

        [SetUp]
        public void SetUp() {
            _root = Path.Combine(Path.GetTempPath(), $"oara-bgsave-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            _profile = new InMemoryProfileStore();
            _profile.PutStorageSettings(_profile.GetStorageSettings() with { SaveDirectory = _root });
            _inserted = new ConcurrentQueue<Guid>();
            _insertGate = null;
            _insertFails = false;
            _insertAttempts = 0;
            _frames = new Mock<IFrameRepository>();
            _frames.Setup(f => f.EnsureManualCaptureSessionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Session);
            _frames.Setup(f => f.InsertAsync(It.IsAny<FrameDto>(), It.IsAny<CancellationToken>()))
                .Returns(async (FrameDto frame, CancellationToken _) => {
                    Interlocked.Increment(ref _insertAttempts);
                    if (_insertGate is { } gate) {
                        await gate.Task.ConfigureAwait(false);
                    }
                    if (_insertFails) {
                        throw new IOException("catalog is read-only");
                    }
                    _inserted.Enqueue(frame.Id);
                });
        }

        [TearDown]
        public void TearDown() {
            _insertGate?.TrySetResult();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private CameraService NewCamera(string? fallback = null) =>
            new(frames: _frames.Object, profileStore: _profile, fallbackFramesDir: fallback ?? _root);

        private static FrameSaveJob Job(Guid? id = null) {
            var request = new ExposureRequestDto(ExposureSec: 1, Gain: 100);
            return new FrameSaveJob {
                FrameId = id ?? Guid.NewGuid(), Pixels = new ushort[16], Width = 4, Height = 4, Request = request, Applied = request,
                ImageType = "DARK", FrameType = FrameType.Dark, TargetName = "Darks",
                CapturedAt = DateTimeOffset.UtcNow,
            };
        }

        private static string[] Frames(string root) =>
            Directory.GetFiles(root, "*.fits", SearchOption.AllDirectories);

        private static async Task Until(Func<bool> condition, string because) {
            for (var i = 0; i < 250 && !condition(); i++) {
                await Task.Delay(20);
            }
            Assert.That(condition(), Is.True, because);
        }

        [Test]
        public async Task A_handed_off_frame_returns_before_its_save_finishes() {
            using var camera = NewCamera();
            _insertGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = Job();

            var handOff = camera.EnqueueSaveAsync(job);
            Assert.That(await Task.WhenAny(handOff, Task.Delay(5000)), Is.SameAs(handOff), "the hand-off doesn't wait for the save");
            await Until(() => _insertAttempts == 1, "the save is under way");
            Assert.Multiple(() => {
                Assert.That(camera.HasPendingSaves, Is.True);
                Assert.That(camera.IsFreeToCapture(new object()), Is.False, "storage maintenance waits for a save in flight");
            });

            _insertGate.SetResult();
            Assert.That(await camera.FinishPendingSavesAsync(), Is.EqualTo(0));
            Assert.Multiple(() => {
                Assert.That(_inserted, Is.EqualTo(new[] { job.FrameId }));
                Assert.That(Frames(_root), Has.Length.EqualTo(1));
                Assert.That(camera.HasPendingSaves, Is.False);
                Assert.That(camera.IsFreeToCapture(new object()), Is.True);
            });
        }

        [Test]
        public async Task A_third_hand_off_waits_for_a_slot_and_frames_land_in_capture_order() {
            using var camera = NewCamera();
            _insertGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var jobs = new[] { Job(), Job(), Job() };

            await camera.EnqueueSaveAsync(jobs[0]);
            await camera.EnqueueSaveAsync(jobs[1]);
            var third = camera.EnqueueSaveAsync(jobs[2]);
            await Task.Delay(300);
            Assert.That(third.IsCompleted, Is.False, $"{CameraService.SaveQueueCapacity} frames wait at most; the next one waits for a slot");

            _insertGate.SetResult();
            _insertGate = null;
            await third;
            Assert.That(await camera.FinishPendingSavesAsync(), Is.EqualTo(0));
            Assert.That(_inserted, Is.EqualTo(jobs.Select(j => j.FrameId)));
        }

        [Test]
        public async Task A_frame_the_catalog_refuses_is_held_and_the_next_capture_pauses_until_it_lands() {
            using var camera = NewCamera();
            _insertFails = true;
            var held = Job();
            await camera.EnqueueSaveAsync(held);
            await Until(() => camera.SaveHeld, "the failed save holds the frame");

            // A frame downloaded meanwhile queues behind it rather than waiting for a slot.
            var next = Job();
            var handOff = camera.EnqueueSaveAsync(next);
            Assert.That(await Task.WhenAny(handOff, Task.Delay(5000)), Is.SameAs(handOff));

            var ex = await Assert.ThrowsAsync<StoreUnavailableException>(camera.RetryFailedSavesAsync,
                "still failing: the run pauses awaiting the user instead of exposing more frames");
            Assert.That(ex!.Message, Does.Contain("holding it"));
            Assert.That(camera.HasPendingSaves, Is.True, "both frames are still held");

            _insertFails = false; // the user fixed it; Resume retries
            await camera.RetryFailedSavesAsync();
            Assert.Multiple(() => {
                Assert.That(_inserted, Is.EqualTo(new[] { held.FrameId, next.FrameId }), "nothing lost, order kept");
                Assert.That(Frames(_root), Has.Length.EqualTo(2), "the held frame's file was written once, not again per retry");
                Assert.That(camera.HasPendingSaves, Is.False);
            });
        }

        [Test]
        public async Task A_hand_off_never_waits_on_a_full_queue_whose_head_is_held() {
            // The held frame never frees its slot on its own (its retry is the next capture's), so a
            // hand-off that waited for a slot would wait forever.
            using var camera = NewCamera();
            _insertGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _insertFails = true;
            var jobs = new[] { Job(), Job(), Job() };
            await camera.EnqueueSaveAsync(jobs[0]);
            await camera.EnqueueSaveAsync(jobs[1]);
            _insertGate.SetResult();
            await Until(() => camera.SaveHeld, "the head failed with the queue full");

            var handOff = camera.EnqueueSaveAsync(jobs[2]);
            Assert.That(await Task.WhenAny(handOff, Task.Delay(5000)), Is.SameAs(handOff), "queued behind the held frame");

            _insertGate = null;
            _insertFails = false;
            await camera.RetryFailedSavesAsync();
            Assert.That(_inserted, Is.EqualTo(jobs.Select(j => j.FrameId)));
        }

        [Test]
        public async Task A_frame_the_store_cannot_take_is_written_once_the_store_is_back() {
            // Both the store and the fallback are a plain file, so no frames folder can be made.
            var blocked = Path.Combine(_root, "store");
            await File.WriteAllTextAsync(blocked, "not a folder");
            _profile.PutStorageSettings(_profile.GetStorageSettings() with { SaveDirectory = blocked });
            using var camera = NewCamera(fallback: blocked);
            var job = Job();

            await camera.EnqueueSaveAsync(job);
            await Until(() => camera.SaveHeld, "the failed write holds the frame");
            Assert.That(_insertAttempts, Is.EqualTo(0), "nothing to register without a file");
            await Assert.ThrowsAsync<StoreUnavailableException>(camera.RetryFailedSavesAsync);

            File.Delete(blocked);
            Directory.CreateDirectory(blocked);
            await camera.RetryFailedSavesAsync();
            Assert.Multiple(() => {
                Assert.That(_inserted, Is.EqualTo(new[] { job.FrameId }));
                Assert.That(Frames(blocked), Has.Length.EqualTo(1));
            });
        }

        [Test]
        public async Task A_run_end_drops_only_frames_that_still_cannot_be_saved() {
            using var camera = NewCamera();
            _insertFails = true;
            await camera.EnqueueSaveAsync(Job());
            await Until(() => camera.SaveHeld, "the failed save holds the frame");

            Assert.That(await camera.FinishPendingSavesAsync(), Is.EqualTo(1), "one retry, then the frame is dropped and counted");
            Assert.That(camera.HasPendingSaves, Is.False);

            _insertFails = false;
            var after = Job();
            await camera.EnqueueSaveAsync(after);
            Assert.That(await camera.FinishPendingSavesAsync(), Is.EqualTo(0));
            Assert.That(_inserted, Is.EqualTo(new[] { after.FrameId }), "the queue works again after a drop");
        }

        [Test]
        public async Task A_background_save_registers_in_the_session_it_was_captured_in() {
            using var camera = NewCamera();
            var runSession = Guid.NewGuid();
            var job = new FrameSaveJob {
                FrameId = Guid.NewGuid(), Pixels = new ushort[16], Width = 4, Height = 4,
                Request = new ExposureRequestDto(1, 100), Applied = new ExposureRequestDto(1, 100),
                ImageType = "LIGHT", FrameType = FrameType.Light, TargetName = "M 31", CapturedAt = DateTimeOffset.UtcNow,
                SessionId = runSession,
            };
            await camera.EnqueueSaveAsync(job);
            Assert.That(await camera.FinishPendingSavesAsync(), Is.EqualTo(0));
            _frames.Verify(f => f.InsertAsync(It.Is<FrameDto>(d => d.Id == job.FrameId && d.SessionId == runSession), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Dispose_lands_a_save_still_in_flight() {
            var camera = NewCamera();
            _insertGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = Job();
            await camera.EnqueueSaveAsync(job);
            await Until(() => _insertAttempts == 1, "the save is under way");

            var gate = _insertGate;
            _ = Task.Run(async () => { await Task.Delay(200); gate.SetResult(); });
            camera.Dispose();
            Assert.That(_inserted, Is.EqualTo(new[] { job.FrameId }), "shutdown waited for the frame");
        }
    }
}
