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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free unit coverage for the §64 Live View surface on <see cref="CameraService"/>: the
    /// not-connected/validation guards and the initial idle state. The actual short-exposure render
    /// loop is exercised against OmniSim in the integration companion (a connected device is needed
    /// to produce a frame).
    /// </summary>
    [TestFixture]
    public class CameraServiceLiveViewTest {

        [Test]
        public void StartLiveViewAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new CameraService();
            Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0), CancellationToken.None));
        }

        [Test]
        public void StartLiveViewAsync_rejects_nonpositive_exposure_before_connected_check() {
            using var svc = new CameraService();
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(0), CancellationToken.None));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(-1.5), CancellationToken.None));
        }

        [Test]
        public void StartLiveViewAsync_rejects_exposure_over_the_cap() {
            using var svc = new CameraService();
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(120), CancellationToken.None));
        }

        [Test]
        public void StartLiveViewAsync_accepts_the_exposure_cap_boundary() {
            using var svc = new CameraService();
            // 15.0 is the inclusive cap: it must pass validation and fall through to the
            // connection check (InvalidOperation), NOT be rejected as out-of-range.
            Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(15.0), CancellationToken.None));
        }

        [Test]
        public void StartLiveViewAsync_rejects_invalid_binning() {
            using var svc = new CameraService();
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0, BinX: 0), CancellationToken.None));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0, BinY: 0), CancellationToken.None));
            // Absurdly large binning is rejected up front (cap is 16) rather than failing per-frame.
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0, BinX: 9999), CancellationToken.None));
        }

        [Test]
        public async Task StopLiveViewAsync_when_not_running_is_a_noop() {
            using var svc = new CameraService();
            await svc.StopLiveViewAsync(); // must not throw
            Assert.That(svc.GetLiveViewStatus().Active, Is.False);
        }

        [Test]
        public void GetLiveViewStatus_is_idle_before_any_start() {
            using var svc = new CameraService();
            var status = svc.GetLiveViewStatus();
            Assert.Multiple(() => {
                Assert.That(status.Active, Is.False);
                Assert.That(status.FrameSeq, Is.EqualTo(0));
                Assert.That(status.Width, Is.Null);
                Assert.That(status.LastFrameAtUtc, Is.Null);
            });
            Assert.That(svc.GetLiveViewFrame(), Is.Null);
        }

        [Test]
        public void Placeholder_live_view_surface_is_inert() {
            var svc = new PlaceholderCameraService();
            Assert.DoesNotThrowAsync(() => svc.StartLiveViewAsync(new LiveViewStartRequestDto(1.0), CancellationToken.None));
            Assert.DoesNotThrowAsync(() => svc.StopLiveViewAsync());
            Assert.That(svc.GetLiveViewStatus().Active, Is.False);
            Assert.That(svc.GetLiveViewFrame(), Is.Null);
        }

        // ─── §64 OSC debayered render (the RenderLiveFrame seam) ───

        [Test]
        public void RenderLiveFrame_with_a_bayer_pattern_renders_halved_color_dimensions() {
            // 8×6 RGGB mosaic with varied cell values. Super-pixel debayer halves both axes, and
            // the published dims must be the halved ones (the client sizes its viewport by them).
            const int w = 8, h = 6;
            var mosaic = new ushort[w * h];
            for (var i = 0; i < mosaic.Length; i++) {
                mosaic[i] = (ushort)(i * 997 % 65536);
            }
            var (jpeg, ow, oh) = CameraService.RenderLiveFrame(
                mosaic, w, h, OpenAstroAra.Stretch.BayerPattern.RGGB);
            Assert.That((ow, oh), Is.EqualTo((4, 3)));
            Assert.That(jpeg, Has.Length.GreaterThan(2));
            Assert.That((jpeg[0], jpeg[1]), Is.EqualTo(((byte)0xFF, (byte)0xD8)), "JPEG SOI marker");
        }

        [Test]
        public void RenderLiveFrame_without_a_pattern_keeps_dimensions() {
            // Mono (or binned-OSC) path: greyscale luminance at the native dimensions, as before.
            const int w = 8, h = 6;
            var pixels = new ushort[w * h];
            for (var i = 0; i < pixels.Length; i++) {
                pixels[i] = (ushort)(i * 997 % 65536);
            }
            var (jpeg, ow, oh) = CameraService.RenderLiveFrame(pixels, w, h, bayerPattern: null);
            Assert.That((ow, oh), Is.EqualTo((8, 6)));
            Assert.That((jpeg[0], jpeg[1]), Is.EqualTo(((byte)0xFF, (byte)0xD8)), "JPEG SOI marker");
        }

        [Test]
        public void SuperPixelStretched_maps_the_bayer_cells_to_interleaved_rgb() {
            // One RGGB tile: R=65535, G=32768 (both greens), B=16384. Manual stretch with default
            // params is a straight linear 16→8-bit map, so the interleaved output is deterministic:
            // [255, ~128, ~64] — proving both the cell→channel mapping and the R,G,B byte order.
            var mosaic = new ushort[] { 65535, 32768, 32768, 16384 };
            var (rgb, w, h) = OpenAstroAra.Stretch.Debayer.SuperPixelStretched(
                mosaic, 2, 2, OpenAstroAra.Stretch.BayerPattern.RGGB,
                OpenAstroAra.Stretch.StretchAlgorithm.Manual, new OpenAstroAra.Stretch.StretchParams());
            Assert.That((w, h), Is.EqualTo((1, 1)));
            Assert.That(rgb, Has.Length.EqualTo(3));
            Assert.That(rgb[0], Is.EqualTo(255));
            Assert.That((int)rgb[1], Is.EqualTo(128).Within(2));
            Assert.That((int)rgb[2], Is.EqualTo(64).Within(2));
        }
    }
}
