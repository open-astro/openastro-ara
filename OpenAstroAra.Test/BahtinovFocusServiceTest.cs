#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1299 — the Bahtinov readout's loop: samples follow the mask star's offset with one sign for
    /// the session, a frame without a pattern is a sample (not an error), the camera failing ends it, a busy
    /// camera refuses it, and the focus zone comes from the optics.</summary>
    [TestFixture]
    public class BahtinovFocusServiceTest {

        private const int W = 640;
        private const int H = 480;

        /// <summary>Renders the next scripted frame: a mask star at the queued offset and rotation, a plain
        /// star field for NaN, or a capture fault for +∞.</summary>
        private sealed class ScriptedFrames : IAnalysisFrameSource {
            public readonly ConcurrentQueue<(double Offset, double Rotation)> Script = new();
            public (double Offset, double Rotation) Last = (0, 23);
            /// <summary>The Bayer pattern the frames carry (null = mono), as CameraService stamps a 1×1 colour readout.</summary>
            public string? Bayer;
            private int _seed;

            public async Task<AnalysisFrame> CaptureForAnalysisAsync(double exposureSec, int binning, CancellationToken ct) {
                await Task.Delay(2, ct);
                if (Script.TryDequeue(out var next)) {
                    Last = next;
                }
                var (offset, rotation) = Last;
                if (double.IsPositiveInfinity(offset)) {
                    throw new InvalidOperationException("camera gone");
                }
                var pixels = double.IsNaN(offset)
                    ? SyntheticSky.Render(W, H, 1.6, stars: 40, frameSeed: ++_seed)
                    : SyntheticSky.RenderBahtinov(W, H, 330, 250, offset, 1.5 + Math.Abs(offset) / 3, rotationDeg: rotation, frameSeed: ++_seed);
                return new AnalysisFrame(pixels, W, H, DateTimeOffset.UtcNow, Bayer);
            }
        }

        private static readonly BahtinovOptics RedCat = new(FocalRatio: 4.9, PixelSizeUm: 3.76);

        private static async Task<BahtinovFocusStatusDto> WaitForSeq(BahtinovFocusService svc, long seq) {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline) {
                var s = svc.GetStatus();
                if (s.Seq >= seq || s.State == "error") return s;
                await Task.Delay(5);
            }
            return svc.GetStatus();
        }

        [Test]
        public void The_zone_comes_from_the_optics_or_falls_back_to_half_a_pixel() {
            var (px, fromOptics) = BahtinovFocusService.ZonePx(new BahtinovOptics(5, 3.76), 1);
            // CFZ/2 = 2.2 × 25 / 2 = 27.5 µm; 3.76 µm × 3π/4 × 5 = 44.3 µm per pixel of offset.
            Assert.That(fromOptics, Is.True);
            Assert.That(BahtinovFocusService.ZoneUm(new BahtinovOptics(5, 3.76)), Is.EqualTo(27.5).Within(1e-9));
            Assert.That(px, Is.EqualTo(27.5 / (3.76 * 3 * Math.PI / 4 * 5)).Within(1e-9));
            Assert.That(BahtinovFocusService.ZonePx(new BahtinovOptics(5, 3.76), 2).Px, Is.EqualTo(px / 2).Within(1e-9), "binned pixels are twice as big");
            Assert.That(BahtinovFocusService.ZonePx(null, 1), Is.EqualTo((BahtinovFocusService.FallbackZonePx, false)));
        }

        [Test]
        public void Exposure_and_binning_are_validated() {
            Assert.That(BahtinovFocusService.ResolveExposure(1), Is.EqualTo(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => BahtinovFocusService.ResolveExposure(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => BahtinovFocusService.ResolveExposure(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => BahtinovFocusService.ResolveExposure(31));
            Assert.That(BahtinovFocusService.ResolveBinning(null, 4), Is.EqualTo(1), "1×1 by default: the offset is sub-pixel");
            Assert.That(BahtinovFocusService.ResolveBinning(2, 0), Is.EqualTo(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => BahtinovFocusService.ResolveBinning(3, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => BahtinovFocusService.ResolveBinning(0, 4));
        }

        [Test]
        public async Task Samples_follow_the_offset_and_report_the_zone() {
            var frames = new ScriptedFrames();
            frames.Script.Enqueue((2.0, 23));
            using var svc = new BahtinovFocusService(frames, () => RedCat);
            await svc.StartAsync(new BahtinovFocusStartRequestDto(ExposureSec: 0.1), CancellationToken.None);
            var s = await WaitForSeq(svc, 2);
            Assert.That(s.State, Is.EqualTo("running"));
            Assert.That(s.Latest!.Detected, Is.True);
            Assert.That(s.Latest.OffsetPx, Is.EqualTo(2.0).Within(0.25));
            Assert.That(s.Latest.DefocusUm, Is.EqualTo(s.Latest.OffsetPx!.Value * 3.76 * 3 * Math.PI / 4 * 4.9).Within(0.2));
            Assert.That(s.Latest.WithinZone, Is.False);
            Assert.That(s.ZoneFromOptics, Is.True);
            Assert.That(s.FocalRatio, Is.EqualTo(4.9));
            Assert.That(s.HasFrame, Is.True);
            var overlay = s.Latest.Overlay!;
            Assert.That(overlay.Lines, Has.Count.EqualTo(3));
            Assert.That(overlay.Lines[1].Role, Is.EqualTo("central"));
            foreach (var line in overlay.Lines) {
                foreach (var v in new[] { line.X1, line.Y1, line.X2, line.Y2 }) {
                    Assert.That(v, Is.InRange(-0.01, overlay.CropSize + 0.01), "clipped to the crop");
                }
            }
            Assert.That(s.Recent, Is.Not.Empty);
            Assert.That(s.Recent[^1].Overlay, Is.Null, "the trend keeps numbers only");

            frames.Script.Enqueue((0.05, 23));
            var seq = s.Seq;
            s = await WaitForSeq(svc, seq + 2);
            Assert.That(s.Latest!.WithinZone, Is.True);
            Assert.That(s.WithinZone, Is.True);
            Assert.That(Math.Abs(s.BestOffsetPx!.Value), Is.LessThan(0.2));

            await svc.StopAsync();
            Assert.That(svc.GetStatus().State, Is.EqualTo("stopped"));
            Assert.That(svc.IsActive, Is.False);
        }

        [Test]
        public async Task The_sign_holds_for_the_session_across_the_wrap() {
            var frames = new ScriptedFrames();
            frames.Script.Enqueue((2.0, 0.4));
            using var svc = new BahtinovFocusService(frames, () => null);
            await svc.StartAsync(new BahtinovFocusStartRequestDto(ExposureSec: 0.1), CancellationToken.None);
            var first = (await WaitForSeq(svc, 2)).Latest!.OffsetPx!.Value;
            // The mask star turns a hair, so the central spike's angle wraps from 0.4° to 179.6°.
            frames.Script.Enqueue((2.0, -0.4));
            var s = await WaitForSeq(svc, svc.GetStatus().Seq + 2);
            Assert.That(Math.Sign(s.Latest!.OffsetPx!.Value), Is.EqualTo(Math.Sign(first)));
            Assert.That(s.ZoneFromOptics, Is.False);
            Assert.That(s.ZonePx, Is.EqualTo(BahtinovFocusService.FallbackZonePx));
            Assert.That(s.Latest.DefocusUm, Is.Null, "no optics, no micrometres");
            await svc.StopAsync();
        }

        [Test]
        public async Task The_whole_frame_preview_of_a_colour_camera_is_debayered() {
            // #1385: the readout's picture of a colour camera's frame went through the renderer with no Bayer
            // pattern and showed the colour-filter grid. A frame carrying one renders through the super-pixel
            // debayer, half the width, like the autofocus preview.
            var frames = new ScriptedFrames { Bayer = "RGGB" };
            frames.Script.Enqueue((double.NaN, 0));
            using var svc = new BahtinovFocusService(frames, () => RedCat);
            await svc.StartAsync(new BahtinovFocusStartRequestDto(ExposureSec: 0.1), CancellationToken.None);
            var s = await WaitForSeq(svc, 3);
            Assert.That(s.HasFrame, Is.True);
            var frame = svc.GetFrame();
            Assert.That(frame, Is.Not.Null);
            Assert.That(AutofocusSweepServiceTest.JpegWidth(frame!.Value.Jpeg.Span), Is.EqualTo(W / 2), "super-pixel debayer: 640 → 320");
            await svc.StopAsync();
        }

        [Test]
        public async Task A_frame_without_a_pattern_is_a_sample_not_an_error() {
            var frames = new ScriptedFrames();
            frames.Script.Enqueue((double.NaN, 0));
            using var svc = new BahtinovFocusService(frames, () => RedCat);
            await svc.StartAsync(new BahtinovFocusStartRequestDto(ExposureSec: 0.1), CancellationToken.None);
            var s = await WaitForSeq(svc, 3);
            Assert.That(s.State, Is.EqualTo("running"));
            Assert.That(s.Latest!.Detected, Is.False);
            Assert.That(s.Latest.Problem, Is.EqualTo("no_pattern"));
            Assert.That(s.Latest.OffsetPx, Is.Null);
            Assert.That(s.HasFrame, Is.True, "the whole frame shows instead");
            Assert.That(s.FrameCrop, Is.Null, "the picture is the whole frame");
            Assert.That((s.FrameWidth, s.FrameHeight), Is.EqualTo((W, H)));
            await svc.StopAsync();
        }

        [Test]
        public async Task A_picked_star_is_cropped_and_kept_without_a_pattern() {
            var frames = new ScriptedFrames();
            frames.Script.Enqueue((double.NaN, 0));
            using var svc = new BahtinovFocusService(frames, () => RedCat);
            await svc.StartAsync(new BahtinovFocusStartRequestDto(ExposureSec: 0.1), CancellationToken.None);
            await WaitForSeq(svc, 2);
            Assert.That(() => svc.Pick(W + 5, 10), Throws.TypeOf<ArgumentOutOfRangeException>());

            svc.Pick(100, 400);
            // Well past LockLostAfter misses: an unpicked lock would have been dropped by now.
            var s = await WaitForSeq(svc, svc.GetStatus().Seq + BahtinovFocusService.LockLostAfter + 3);
            Assert.That(s.Latest!.Detected, Is.False);
            Assert.That((s.PickX, s.PickY), Is.EqualTo((100, 400)));
            Assert.That(s.FrameCrop, Is.Not.Null, "the picture is the crop around the picked star");
            var crop = s.FrameCrop!;
            Assert.That(crop.Size, Is.EqualTo(256));
            // Centred on the brightest star near the pick (within the picked 32 px search window), so the
            // crop holds the pick and sits near the frame's bottom-left corner.
            Assert.That(crop.Left, Is.InRange(0, 100), "clamped near the left edge");
            Assert.That(crop.Top, Is.InRange(H - 256 - 100, H - 256), "clamped near the bottom edge");
            Assert.That((s.Latest.StarX, s.Latest.StarY), Is.EqualTo((100, 400)), "the lock stays on the pick");

            svc.ClearPick();
            s = await WaitForSeq(svc, s.Seq + 2);
            Assert.That(s.PickX, Is.Null);
            Assert.That(s.FrameCrop, Is.Null, "back to the whole frame");
            await svc.StopAsync();
        }

        [Test]
        public async Task A_tap_during_a_frames_measurement_is_not_overwritten_by_that_frame() {
            // Review of #1365: Record wrote the lock from a frame whose search position predated the tap,
            // so the readout went back to the old star while pick_x/pick_y reported the new one.
            var frames = new ScriptedFrames();
            frames.Script.Enqueue((2.0, 23));
            using var svc = new BahtinovFocusService(frames, () => RedCat);
            await svc.StartAsync(new BahtinovFocusStartRequestDto(ExposureSec: 0.1), CancellationToken.None);
            var s = await WaitForSeq(svc, 3);
            Assert.That(s.Latest!.Detected, Is.True, "precondition: locked on the mask star at (330, 250)");

            var tapped = 0;
            svc.AfterMeasureForTest = () => {
                if (Interlocked.Exchange(ref tapped, 1) == 0) {
                    svc.Pick(100, 400); // lands between that frame's search and its Record
                }
            };
            s = await WaitForSeq(svc, s.Seq + 4);
            Assert.That(tapped, Is.EqualTo(1));
            Assert.That((s.PickX, s.PickY), Is.EqualTo((100, 400)));
            Assert.That(Math.Abs(s.Latest!.StarX - 100), Is.LessThan(40),
                $"the readout followed the tap, not the frame that was in flight (star_x {s.Latest.StarX})");
            await svc.StopAsync();
        }

        /// <summary>Holds the first capture until released, so a test can act before the first frame.</summary>
        private sealed class GatedFrames : IAnalysisFrameSource, IDisposable {
            public readonly SemaphoreSlim Gate = new(0);
            public void Dispose() => Gate.Dispose();
            private int _seed;
            public async Task<AnalysisFrame> CaptureForAnalysisAsync(double exposureSec, int binning, CancellationToken ct) {
                await Gate.WaitAsync(ct);
                Gate.Release(); // every later capture passes straight through
                var pixels = SyntheticSky.RenderBahtinov(W, H, 330, 250, 2.0, 2.0, frameSeed: ++_seed);
                return new AnalysisFrame(pixels, W, H, DateTimeOffset.UtcNow);
            }
        }

        [Test]
        public async Task A_pick_outside_the_first_frame_is_dropped_when_the_frame_arrives() {
            // Review of #1365: before the first frame Pick can only refuse negatives, so an off-frame
            // pick became a lock that never found a star and never fell back.
            using var frames = new GatedFrames();
            using var svc = new BahtinovFocusService(frames, () => RedCat);
            await svc.StartAsync(new BahtinovFocusStartRequestDto(ExposureSec: 0.1), CancellationToken.None);
            svc.Pick(W + 500, 10); // no frame yet: accepted
            Assert.That(svc.GetStatus().PickX, Is.EqualTo(W + 500));
            frames.Gate.Release();
            var s = await WaitForSeq(svc, 3);
            Assert.That(s.PickX, Is.Null, "dropped once the frame size was known");
            Assert.That(s.Latest!.Detected, Is.True, "the search went back to the brightest star");
            await svc.StopAsync();
        }

        [Test]
        public void The_crop_around_a_star_stays_inside_the_frame() {
            Assert.That(BahtinovFocusService.CropAround((5, 5), 640, 480), Is.EqualTo((0, 0, 256)));
            Assert.That(BahtinovFocusService.CropAround((320, 240), 640, 480), Is.EqualTo((192, 112, 256)));
            Assert.That(BahtinovFocusService.CropAround((639, 479), 640, 480), Is.EqualTo((384, 224, 256)));
            Assert.That(BahtinovFocusService.CropAround((50, 50), 100, 120), Is.EqualTo((0, 0, 100)), "a small frame is its own crop");
        }

        [Test]
        public async Task Repeated_capture_faults_end_in_error() {
            var frames = new ScriptedFrames();
            frames.Script.Enqueue((double.PositiveInfinity, 0));
            using var svc = new BahtinovFocusService(frames, () => RedCat);
            await svc.StartAsync(new BahtinovFocusStartRequestDto(ExposureSec: 0.1), CancellationToken.None);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (svc.GetStatus().State == "running" && DateTimeOffset.UtcNow < deadline) {
                await Task.Delay(5);
            }
            var s = svc.GetStatus();
            Assert.That(s.State, Is.EqualTo("error"));
            Assert.That(s.ConsecutiveFailures, Is.EqualTo(BahtinovFocusService.MaxConsecutiveFailures));
            Assert.That(s.Error, Does.Contain("camera gone"));
        }

        [Test]
        public async Task A_busy_camera_refuses_the_start_and_a_second_start_is_refused() {
            using var busy = new BahtinovFocusService(new ScriptedFrames(), () => RedCat, () => "an autofocus run is in progress");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => busy.StartAsync(new BahtinovFocusStartRequestDto(), CancellationToken.None));
            Assert.That(ex!.Message, Does.Contain("autofocus"));

            var frames = new ScriptedFrames();
            frames.Script.Enqueue((1.0, 23));
            using var svc = new BahtinovFocusService(frames, () => RedCat);
            await svc.StartAsync(new BahtinovFocusStartRequestDto(ExposureSec: 0.1), CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(new BahtinovFocusStartRequestDto(), CancellationToken.None));
            await svc.StopAsync();
            await svc.StopAsync();
            Assert.That(svc.GetStatus().State, Is.EqualTo("stopped"), "stop is idempotent");
        }

        [Test]
        public void The_synthetic_source_crosses_zero_at_best_focus() {
            var o = new SyntheticSkySettings(BestPosition: 10_000, StepsPerPixel: 400);
            Assert.That(SyntheticBahtinovFrameSource.OffsetAt(10_000, o), Is.EqualTo(0));
            Assert.That(SyntheticBahtinovFrameSource.OffsetAt(10_400, o), Is.EqualTo(2.5));
            Assert.That(SyntheticBahtinovFrameSource.OffsetAt(9_600, o), Is.EqualTo(-2.5));
        }
    }
}
