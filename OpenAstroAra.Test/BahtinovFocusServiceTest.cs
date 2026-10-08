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
                return new AnalysisFrame(pixels, W, H, DateTimeOffset.UtcNow);
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
            await svc.StopAsync();
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
