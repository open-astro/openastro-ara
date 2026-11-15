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
using OpenAstroAra.Server.Services.LiveStack;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>↑ara#1787: the Session Live Stack fed by the sequencer's background-save path. Lights go through the
    /// same seam BackgroundFrameSaveTest uses (EnqueueSaveAsync with a held-open catalog mock); nothing here reads
    /// the stack engine's fields, only the status and frame the routes serve.</summary>
    [TestFixture]
    [Category("IO")] // real disk, like BackgroundFrameSaveTest
    public class CameraServiceSessionLiveStackTest {

        private string _root = string.Empty;
        private InMemoryProfileStore _profile = null!;
        private Mock<IFrameRepository> _frames = null!;
        private ConcurrentQueue<Guid> _inserted = null!;
        private static readonly Guid CatalogSession = Guid.NewGuid();

        [SetUp]
        public void SetUp() {
            _root = Path.Combine(Path.GetTempPath(), $"oara-sls-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            _profile = new InMemoryProfileStore();
            _profile.PutStorageSettings(_profile.GetStorageSettings() with { SaveDirectory = _root });
            _inserted = new ConcurrentQueue<Guid>();
            _frames = new Mock<IFrameRepository>();
            _frames.Setup(f => f.EnsureManualCaptureSessionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CatalogSession);
            _frames.Setup(f => f.InsertAsync(It.IsAny<FrameDto>(), It.IsAny<CancellationToken>()))
                .Returns((FrameDto frame, CancellationToken _) => { _inserted.Enqueue(frame.Id); return Task.CompletedTask; });
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static CameraCapabilitiesDto Caps(string? bayer) => new(
            OscScene.W, OscScene.H, 3.8, false, true, false, 0, 100, 0, 100, 1, 4, 1, 4, 0.001, 600, BayerPattern: bayer);

        private CameraService NewCamera(string? bayer = "RGGB") {
            var cam = new CameraService(frames: _frames.Object, profileStore: _profile, fallbackFramesDir: _root);
            cam.WithCapabilitiesForTest(Caps(bayer));
            return cam;
        }

#pragma warning disable CA1859 // typed as the interface on purpose: the tests then fail to compile if CameraService stops implementing it
        private static ISessionLiveStackControl Control(CameraService c) => c;
#pragma warning restore CA1859

        private static FrameSaveJob Light(int seed, string target = "M 31", string? filter = "Ha", Guid? session = null,
                FrameType type = FrameType.Light, int bin = 1, double shiftX = 0, double rotDeg = 0, bool centreCrop = false) {
            var req = new ExposureRequestDto(ExposureSec: 1, Gain: 100, BinX: bin, BinY: bin, FilterName: filter);
            var mosaic = OscScene.Mosaic(seed, shiftX: shiftX, rotDeg: rotDeg);
            var size = OscScene.W;
            if (centreCrop) { // centred 256 x 256 at the even offset 128: another subframe size, same Bayer phase, 9 stars
                size = OscScene.W / 2;
                const int off = OscScene.W / 4;
                var crop = new ushort[size * size];
                for (var y = 0; y < size; y++) { Array.Copy(mosaic, (y + off) * OscScene.W + off, crop, y * size, size); }
                mosaic = crop;
            }
            return new FrameSaveJob {
                // Background is left unset on purpose: EnqueueSaveAsync (the sequencer's save path) must set it; SaveFrameAsync does not.
                FrameId = Guid.NewGuid(), Pixels = mosaic, Width = size, Height = size,
                Request = req, Applied = req, ImageType = type == FrameType.Light ? "LIGHT" : "DARK", FrameType = type,
                TargetName = target, CapturedAt = DateTimeOffset.UtcNow, SessionId = session ?? CatalogSession,
            };
        }

        private static async Task Until(Func<bool> condition, string because) {
            for (var i = 0; i < 400 && !condition(); i++) { await Task.Delay(20); }
            Assert.That(condition(), Is.True, because);
        }

        private static async Task Save(CameraService cam, params FrameSaveJob[] jobs) {
            foreach (var j in jobs) { await cam.EnqueueSaveAsync(j); }
            Assert.That(await cam.FinishPendingSavesAsync(), Is.EqualTo(0));
        }

        private static async Task Stacked(CameraService cam, int lights) =>
            await Until(() => Control(cam).GetSessionLiveStackStatus().LightsStacked == lights, $"{lights} lights stacked");

        // Falsified by: removing the feed call from AfterFrameSaved leaves LightsStacked at 0; a status that reads the
        // wrong target or filter fails the name asserts.
        [Test]
        public async Task Sequencer_lights_feed_the_stack_once_it_is_switched_on() {
            using var cam = NewCamera();
            Control(cam).StartSessionLiveStack();
            await Save(cam, Light(1), Light(2, shiftX: 1.5));
            await Stacked(cam, 2);
            var s = Control(cam).GetSessionLiveStackStatus();
            Assert.Multiple(() => {
                Assert.That(s.Enabled, Is.True);
                Assert.That((s.TargetName, s.FilterName, s.SessionId), Is.EqualTo(("M 31", "Ha", (Guid?)CatalogSession)));
                Assert.That((s.Width, s.Height), Is.EqualTo((OscScene.W / 2, OscScene.H / 2)));
                Assert.That(s.LastOutcome, Is.EqualTo("stacked"));
                Assert.That(s.StackSeq, Is.EqualTo(2));
                Assert.That(Control(cam).GetSessionLiveStackFrame(), Is.Not.Null);
            });
        }

        // Falsified by: a switch that defaults to on (or a feed that ignores it) stacks the lights of a night nobody asked for.
        [Test]
        public async Task The_stack_is_off_by_default_and_the_light_is_still_saved() {
            using var cam = NewCamera();
            var light = Light(1);
            await Save(cam, light);
            var s = Control(cam).GetSessionLiveStackStatus();
            Assert.Multiple(() => {
                Assert.That(s.Enabled, Is.False);
                Assert.That(s.LightsStacked, Is.EqualTo(0));
                Assert.That(Control(cam).GetSessionLiveStackFrame(), Is.Null);
                Assert.That(_inserted, Is.EqualTo(new[] { light.FrameId }));
            });
        }

        // Falsified by: feeding on any frame type (dropping the Light check) stacks the dark.
        [Test]
        public async Task A_dark_is_never_stacked() {
            using var cam = NewCamera();
            Control(cam).StartSessionLiveStack();
            // The dark has another target: were it fed it would become the stack, so wait for the light's name
            // (a dark fed first would otherwise satisfy a bare count) and then read the sequence.
            await Save(cam, Light(1, target: "Dark frames", type: FrameType.Dark), Light(2));
            await Until(() => Control(cam).GetSessionLiveStackStatus().TargetName == "M 31", "the light is the stack");
            await Stacked(cam, 1);
            Assert.That(Control(cam).GetSessionLiveStackStatus().StackSeq, Is.EqualTo(1), "a fed dark would make it 2");
        }

        // Falsified by: dropping the Target comparison leaves two objects in one picture; stack_seq must not go back.
        [Test]
        public async Task A_new_target_restarts_the_stack_and_the_sequence_keeps_rising() {
            using var cam = NewCamera();
            Control(cam).StartSessionLiveStack();
            await Save(cam, Light(1), Light(2, shiftX: 1));
            await Stacked(cam, 2);
            await Save(cam, Light(3, target: "M 42"));
            await Stacked(cam, 1);
            var s = Control(cam).GetSessionLiveStackStatus();
            Assert.That((s.TargetName, s.StackSeq), Is.EqualTo(("M 42", 3L)));
        }

        // Falsified by: dropping the filter comparison mixes passbands.
        [Test]
        public async Task A_new_filter_restarts_the_stack() {
            using var cam = NewCamera();
            Control(cam).StartSessionLiveStack();
            await Save(cam, Light(1), Light(2, shiftX: 1));
            await Stacked(cam, 2);
            await Save(cam, Light(3, filter: "L-eXtreme"));
            await Stacked(cam, 1);
            Assert.That(Control(cam).GetSessionLiveStackStatus().FilterName, Is.EqualTo("L-eXtreme"));
        }

        // Falsified by: dropping the catalog-Session comparison keeps last night's lights in tonight's stack.
        [Test]
        public async Task A_new_catalog_session_restarts_the_stack() {
            using var cam = NewCamera();
            Control(cam).StartSessionLiveStack();
            await Save(cam, Light(1), Light(2, shiftX: 1));
            await Stacked(cam, 2);
            var next = Guid.NewGuid();
            await Save(cam, Light(3, session: next));
            await Stacked(cam, 1);
            var s = Control(cam).GetSessionLiveStackStatus();
            Assert.That((s.SessionId, s.Enabled), Is.EqualTo(((Guid?)next, true)), "the switch survives across Sessions");
        }

        // Falsified by: a grid size left out of the restart key leaves WrongSize refusing every later light until a Reset.
        [Test]
        public async Task A_light_of_another_size_restarts_the_stack() {
            using var cam = NewCamera();
            Control(cam).StartSessionLiveStack();
            await Save(cam, Light(1), Light(2, shiftX: 1));
            await Stacked(cam, 2);
            await Save(cam, Light(3, centreCrop: true));
            await Stacked(cam, 1);
            var s = Control(cam).GetSessionLiveStackStatus();
            Assert.That((s.Width, s.Height, s.LastOutcome), Is.EqualTo((OscScene.W / 4, OscScene.H / 4, "reference")), "the one light is the new reference");
        }

        // Falsified by: clearing the stack when the run ends (FinishPendingSavesAsync, SequencerService.cs, is what the
        // sequencer calls on the camera at the end; the catalog Session closes in the frame store, not here).
        [Test]
        public async Task The_end_of_a_run_keeps_the_stack_and_the_switch() {
            using var cam = NewCamera();
            Control(cam).StartSessionLiveStack();
            await Save(cam, Light(1), Light(2, shiftX: 1));
            await Stacked(cam, 2);
            Assert.That(await cam.FinishPendingSavesAsync(), Is.EqualTo(0));
            await Task.Delay(100);
            var s = Control(cam).GetSessionLiveStackStatus();
            Assert.That((s.Enabled, s.LightsStacked), Is.EqualTo((true, 2)));
            Assert.That(Control(cam).GetSessionLiveStackFrame(), Is.Not.Null);
        }

        // Falsified by: feeding on the Light frame type alone: the manual capture (CaptureCoreAsync, saveInBackground
        // false) is LIGHT with another target and catalog Session, so it would wipe the night's stack. The flag is set by
        // EnqueueSaveAsync, which every feed test goes through, so a forgotten assignment turns them all red.
        [Test]
        public async Task A_manual_capture_is_not_fed_and_does_not_restart_the_stack() {
            using var cam = NewCamera();
            Control(cam).StartSessionLiveStack();
            await Save(cam, Light(1), Light(2, shiftX: 1));
            await Stacked(cam, 2);
            await cam.SaveFrameAsync(Light(3, target: "Manual capture", session: Guid.NewGuid()));
            // A following background light proves the worker has gone past the manual one (no fixed wait).
            await Save(cam, Light(4, shiftX: 2));
            await Stacked(cam, 3);
            var s = Control(cam).GetSessionLiveStackStatus();
            Assert.That((s.LightsStacked, s.StackSeq, s.TargetName), Is.EqualTo((3, 3L, "M 31")), "the manual capture is not in the stack");
        }

        // Falsified by: calling Enable/Disable on the engine while holding the service's stack lock. The hold is inside the
        // engine's lock (InsideAddForTest), so such a Reset waits for AddColour with the lock held, the feed blocks on it,
        // the save worker stops and FinishPendingSavesAsync stalls. Reset, Stop and disconnect themselves may wait.
        [TestCase("reset")]
        [TestCase("stop")]
        [TestCase("disconnect")]
        public async Task Reset_Stop_and_disconnect_do_not_stop_the_saves_while_a_stack_runs(string how) {
            using var cam = NewCamera();
            cam.WithLiveViewTestClientForTest(() => new FakeCameraDevice { ExposureFails = true });
            cam.WithCapabilitiesForTest(Caps("RGGB")); // the connect re-read the fake's mono capabilities
            Control(cam).StartSessionLiveStack();
            await Save(cam, Light(1));
            await Stacked(cam, 1);
            using var inside = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            cam.SessionLiveStack.InsideAddForTest = () => { inside.Set(); release.Wait(); };
            await Save(cam, Light(2, shiftX: 1));
            await Until(() => inside.IsSet, "the worker is inside AddColour, holding the engine lock");

            var call = Task.Run(async () => {
                if (how == "reset") {
                    Control(cam).ResetSessionLiveStack();
                } else if (how == "stop") {
                    Control(cam).StopSessionLiveStack();
                } else {
                    await cam.DisconnectAsync(null, CancellationToken.None);
                }
            });
            try {
                // Off the test thread and bounded: under the mutant this read blocks, and the finally below must still run.
                var cleared = Task.Run(() => Until(() => Control(cam).GetSessionLiveStackStatus().LightsStacked == 0, "the status reads cleared while AddColour still runs"));
                Assert.That(await Task.WhenAny(cleared, Task.Delay(2000)), Is.SameAs(cleared), "the status read does not wait for the stack");
                await cleared;
                await cam.EnqueueSaveAsync(Light(3, shiftX: 2));
                var finish = cam.FinishPendingSavesAsync();
                Assert.That(await Task.WhenAny(finish, Task.Delay(1000)), Is.SameAs(finish), "the saves finish within 1 s");
                Assert.That(await finish, Is.EqualTo(0));
            } finally {
                cam.SessionLiveStack.InsideAddForTest = null;
                release.Set();
            }
            await call;
        }

        // Falsified by: restarting on a flipped frame (or not folding the 180-degree frame in) loses the first half of the night.
        [Test]
        public async Task A_meridian_flip_carries_on_the_same_stack() {
            using var cam = NewCamera();
            Control(cam).StartSessionLiveStack();
            // Two at a time at most: one stacking plus one waiting; a third in the same batch is skipped_busy by design.
            await Save(cam, Light(1), Light(2, shiftX: 1));
            await Stacked(cam, 2);
            await Save(cam, Light(3, shiftX: 2, rotDeg: 180));
            await Stacked(cam, 3);
            Assert.That(Control(cam).GetSessionLiveStackStatus().LastOutcome, Is.EqualTo("flipped"));
        }

        // Falsified by: Stop that keeps the pixels, or Reset that also clears the switch; both are idempotent.
        [Test]
        public async Task Stop_empties_and_switches_off_and_Reset_empties_and_stays_on() {
            using var cam = NewCamera();
            Control(cam).StartSessionLiveStack();
            await Save(cam, Light(1), Light(2, shiftX: 1));
            await Stacked(cam, 2);

            Control(cam).ResetSessionLiveStack();
            Control(cam).ResetSessionLiveStack();
            var afterReset = Control(cam).GetSessionLiveStackStatus();
            Assert.That((afterReset.Enabled, afterReset.LightsStacked, afterReset.StackSeq), Is.EqualTo((true, 0, 2L)));

            await Save(cam, Light(3));
            await Stacked(cam, 1);
            Control(cam).StopSessionLiveStack();
            Control(cam).StopSessionLiveStack();
            var afterStop = Control(cam).GetSessionLiveStackStatus();
            Assert.Multiple(() => {
                Assert.That((afterStop.Enabled, afterStop.LightsStacked), Is.EqualTo((false, 0)));
                Assert.That(Control(cam).GetSessionLiveStackFrame(), Is.Null);
            });
        }

        // Falsified by: a camera disconnect that leaves the old camera's stack readable.
        [Test]
        public async Task A_camera_disconnect_clears_the_stack() {
            using var cam = NewCamera();
            cam.WithLiveViewTestClientForTest(() => new FakeCameraDevice { ExposureFails = true });
            cam.WithCapabilitiesForTest(Caps("RGGB")); // the connect re-read the fake's mono capabilities
            Control(cam).StartSessionLiveStack();
            await Save(cam, Light(1));
            await Stacked(cam, 1);
            await cam.DisconnectAsync(null, CancellationToken.None);
            await Until(() => Control(cam).GetSessionLiveStackStatus().LightsStacked == 0, "stack cleared");
            Assert.That(Control(cam).GetSessionLiveStackFrame(), Is.Null);
        }

        // Falsified by: a worker that trusts the engine to be at the current generation. Start, Stop, Reset, disconnect and a
        // restart call the engine after the lock, in no set order, so an older Enable, or a Stop's Disable after a Start's
        // Enable, can land last: every later light was then dropped and the status stayed empty until the next Reset.
        [TestCase("enable")]
        [TestCase("disable")]
        public async Task A_late_engine_call_from_an_older_generation_does_not_drop_the_next_light(string late) {
            using var cam = NewCamera();
            Control(cam).StartSessionLiveStack();
            await Save(cam, Light(1));
            await Stacked(cam, 1);
            if (late == "enable") {
                var stale = cam.SessionLiveStack.Snapshot.SessionId;
                Control(cam).ResetSessionLiveStack();
                cam.SessionLiveStack.Enable(stale); // the older generation's Enable lands after the Reset's
            } else {
                Control(cam).StopSessionLiveStack();
                Control(cam).StartSessionLiveStack();
                cam.SessionLiveStack.Disable(); // the Stop's Disable lands after the Start's Enable
            }
            await Save(cam, Light(2));
            await Stacked(cam, 1);
            Assert.That(Control(cam).GetSessionLiveStackStatus().LastOutcome, Is.EqualTo("reference"));
        }

        // Falsified by: a feed that waits for the stacker (inline Add) holds the save worker: FinishPendingSavesAsync
        // would never return while the stacker is blocked; a third light must be counted skipped_busy but still catalogued.
        [Test]
        public async Task A_light_arriving_while_one_is_stacking_and_one_waits_is_counted_skipped_busy_and_still_saved() {
            using var cam = NewCamera();
            using var inside = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            cam.SessionLiveStack.InsideAddForTest = () => { inside.Set(); release.Wait(); };
            Control(cam).StartSessionLiveStack();
            var first = Light(1);
            await Save(cam, first);
            await Until(() => inside.IsSet, "the worker took the first light");
            var rest = new[] { Light(2, shiftX: 1), Light(3, shiftX: 2) };
            await Save(cam, rest);

            var s = Control(cam).GetSessionLiveStackStatus();
            Assert.Multiple(() => {
                Assert.That(s.LightsSkippedBusy, Is.EqualTo(1));
                Assert.That(s.LastOutcome, Is.EqualTo("skipped_busy"));
                Assert.That(_inserted, Has.Count.EqualTo(3), "every light is catalogued");
                Assert.That(Directory.GetFiles(_root, "*.fits", SearchOption.AllDirectories), Has.Length.EqualTo(3));
            });

            cam.SessionLiveStack.InsideAddForTest = null;
            release.Set();
            await Stacked(cam, 2);
        }

        // Falsified by: stacking a mono mosaic as colour, or not reporting the reason.
        [TestCase(null, 1, TestName = "mono_camera")]
        [TestCase("RGGB", 2, TestName = "osc_binned_in_the_driver")]
        public async Task A_light_with_no_bayer_pattern_reports_not_colour(string? bayer, int bin) {
            using var cam = NewCamera(bayer);
            Control(cam).StartSessionLiveStack();
            await Save(cam, Light(1, bin: bin));
            await Until(() => Control(cam).GetSessionLiveStackStatus().LastOutcome == "not_colour", "not_colour reported");
            Assert.That(Control(cam).GetSessionLiveStackStatus().LightsStacked, Is.EqualTo(0));
        }

        // Falsified by: an engine cap that is not applied to the superpixel grid reports stacked, or throws.
        [Test]
        public async Task An_over_cap_light_reports_too_large() {
            using var cam = NewCamera();
            cam.UseSessionLiveStackForTest(new LiveStackEngine(maxPixels: 100));
            Control(cam).StartSessionLiveStack();
            await Save(cam, Light(1));
            await Until(() => Control(cam).GetSessionLiveStackStatus().LastOutcome == "too_large", "too_large reported");
        }

        // Falsified by: sharing one engine with Live View lets this feed move the Live View stack's counters.
        [Test]
        public async Task The_live_view_stack_is_untouched_by_session_lights() {
            using var cam = NewCamera();
            Control(cam).StartSessionLiveStack();
            await Save(cam, Light(1));
            await Stacked(cam, 1);
            Assert.That(((ILiveStackControl)cam).GetLiveStackStatus().StackSeq, Is.EqualTo(0));
        }
    }
}