#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Common.DeviceInterfaces;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free unit coverage for the §14e <see cref="CameraService"/> (the capture-path head).
    /// The live capture pipeline is exercised by the <c>[Category("Integration")]</c> companion
    /// test against OmniSim; here we cover the not-connected/disposed REST contracts, the
    /// validation ordering, and the pure helpers (ImageArray conversion, state mapping,
    /// frames-dir resolution).
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class CameraServiceTest {

        [Test]
        public async Task ForgetAsync_with_no_device_retained_returns_false() {
            using var svc = new CameraService();
            Assert.That(await svc.ForgetAsync(CancellationToken.None), Is.False);
        }

        [Test]
        public async Task ForgetAsync_after_a_disconnect_drops_the_retained_device() {
            using var svc = new CameraService();
            var dead = new DiscoveredDeviceDto("uid", "U", DeviceType.Camera, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            // Disconnect supersedes the in-flight connect (generation bump), so no settle poll is needed.
            await svc.DisconnectAsync(null, CancellationToken.None);
            var retained = await svc.GetAsync(CancellationToken.None);
            Assert.That(retained, Is.Not.Null, "retained after disconnect");
            Assert.That(retained!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));

            Assert.That(await svc.ForgetAsync(CancellationToken.None), Is.True);

            // The card's Remove: the status GET reads 404 again, as before any device was selected.
            Assert.That(await svc.GetAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ForgetAsync_while_connecting_throws_InvalidOperation() {
            using var svc = new CameraService();
            var dead = new DiscoveredDeviceDto("uid", "U", DeviceType.Camera, "127.0.0.1", "127.0.0.1", 1, 0, false);
            await svc.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            // ConnectAsync sets Connecting synchronously; the live states refuse a removal (→ 409).
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ForgetAsync(CancellationToken.None));
            await svc.DisconnectAsync(null, CancellationToken.None); // supersede the dead connect before dispose
        }

        [Test]
        public async Task GetAsync_returns_null_before_any_device_was_selected() {
            using var svc = new CameraService();
            Assert.That(await svc.GetAsync(CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task StartExposureAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new CameraService();
            Assert.Throws<InvalidOperationException>(
                () => { _ = svc.StartExposureAsync(new ExposureRequestDto(1.0, Gain: null), null, CancellationToken.None); });
        }

        [Test]
        public async Task CaptureForAnalysisAsync_when_not_connected_throws_InvalidOperation() {
            // §59 — a probe capture on a disconnected camera must fail loudly (a silent
            // gap in the sweep's measurements would corrupt the focus curve).
            using var svc = new CameraService();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.CaptureForAnalysisAsync(1.0, binning: 1, CancellationToken.None));
        }

        [Test]
        public async Task CaptureForAnalysisAsync_rejects_nonpositive_exposure_before_connected_check() {
            using var svc = new CameraService();
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.CaptureForAnalysisAsync(0, binning: 1, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.CaptureForAnalysisAsync(-1, binning: 1, CancellationToken.None));
        }

        [Test]
        public async Task CaptureForAnalysisAsync_rejects_out_of_range_binning() {
            // An over-short binning would otherwise WRAP in ApplyExposureSettings' narrowing
            // cast and TrySet would log-and-skip — a silently mis-binned AF probe.
            using var svc = new CameraService();
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.CaptureForAnalysisAsync(1.0, binning: 0, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.CaptureForAnalysisAsync(1.0, binning: short.MaxValue + 1, CancellationToken.None));
        }

        [Test]
        public async Task CaptureForAnalysisAsync_rejects_nan_and_infinite_exposures() {
            // NaN comparisons are always false, so a bare `<= 0` guard lets NaN through to the
            // device call — and the §59 sweep feeds COMPUTED exposures into this seam.
            using var svc = new CameraService();
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.CaptureForAnalysisAsync(double.NaN, binning: 1, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.CaptureForAnalysisAsync(double.PositiveInfinity, binning: 1, CancellationToken.None));
        }

        [Test]
        public void StartExposureAsync_rejects_nonpositive_exposure_before_connected_check() {
            using var svc = new CameraService();
            // Argument range validates BEFORE the connected check (services-wide ordering), so a
            // bad exposure on a disconnected service reports the argument problem, not the state.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.StartExposureAsync(new ExposureRequestDto(0, Gain: null), null, CancellationToken.None); });
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.StartExposureAsync(new ExposureRequestDto(-2, Gain: null), null, CancellationToken.None); });
        }

        [Test]
        public void StartExposureAsync_rejects_invalid_binning() {
            using var svc = new CameraService();
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.StartExposureAsync(new ExposureRequestDto(1.0, Gain: null, BinX: 0), null, CancellationToken.None); });
        }

        [Test]
        public void StartExposureAsync_rejects_negative_offset() {
            using var svc = new CameraService();
            // Offset validates before the connected check too; a negative offset fails fast rather
            // than falling through to the device default.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = svc.StartExposureAsync(new ExposureRequestDto(1.0, Gain: null, CameraOffset: -5), null, CancellationToken.None); });
        }

        [Test]
        public async Task AbortExposureAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new CameraService();
            // Async method: the guard surfaces on the returned Task.
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AbortExposureAsync(CancellationToken.None));
        }

        [Test]
        public async Task SetReadoutModeAsync_when_not_connected_throws_InvalidOperation() {
            using var svc = new CameraService();
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetReadoutModeAsync(0, CancellationToken.None));
        }

        [Test]
        public async Task SetReadoutModeAsync_rejects_a_negative_index_before_the_connected_check() {
            using var svc = new CameraService();
            // Same precedence as dome Slew/Sync: a structurally-bad index is a 400 even
            // while disconnected; a plausible index on a disconnected camera is a 409.
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.SetReadoutModeAsync(-1, CancellationToken.None));
        }

        [Test]
        public void Ops_after_Dispose_throw_ObjectDisposed() {
            var svc = new CameraService();
            svc.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.GetAsync(CancellationToken.None); });
            Assert.Throws<ObjectDisposedException>(
                () => { _ = svc.StartExposureAsync(new ExposureRequestDto(1.0, Gain: null), null, CancellationToken.None); });
        }

        [Test]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1814:Prefer jagged arrays over multidimensional",
            Justification = "The API under test consumes ASCOM's ImageArray, which IS a multidimensional int[x,y] by spec.")]
        public void ConvertImageArray_transposes_column_major_to_row_major_and_clamps() {
            // ASCOM ImageArray is [x, y]; FITS wants row-major rows of width.
            var arr = new int[2, 3]; // width=2, height=3
            arr[0, 0] = 10; arr[1, 0] = 20;
            arr[0, 1] = 30; arr[1, 1] = 40;
            arr[0, 2] = -5; arr[1, 2] = 70000; // clamp both ends

            var (pixels, width, height) = CameraService.ConvertImageArray(arr);

            Assert.That(width, Is.EqualTo(2));
            Assert.That(height, Is.EqualTo(3));
            Assert.That(pixels, Is.EqualTo(new ushort[] { 10, 20, 30, 40, 0, 65535 }));
        }

        [Test]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1814:Prefer jagged arrays over multidimensional",
            Justification = "The API under test consumes ASCOM's ImageArray, which IS a multidimensional array by spec.")]
        public void ConvertImageArray_handles_double_payloads_from_bridged_drivers() {
            var arr = new double[3, 2];
            arr[0, 0] = 10.4; arr[1, 0] = 20.6; arr[2, 0] = double.NaN;
            arr[0, 1] = -3.0; arr[1, 1] = 99999.0; arr[2, 1] = double.PositiveInfinity;

            var (pixels, width, height) = CameraService.ConvertImageArray(arr);

            Assert.That((width, height), Is.EqualTo((3, 2)));
            // NaN reads as 0; saturated/+Inf clamps to white (65535), never wraps to black.
            Assert.That(pixels, Is.EqualTo(new ushort[] { 10, 21, 0, 0, 65535, 65535 }));
        }

        [Test]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1814:Prefer jagged arrays over multidimensional",
            Justification = "The API under test consumes ASCOM's ImageArray, which IS a multidimensional array by spec (3-axis = color).")]
        public void ConvertImageArray_rejects_color_and_unknown_payloads() {
            Assert.Throws<InvalidOperationException>(() => CameraService.ConvertImageArray(new int[2, 2, 3]));
            Assert.Throws<InvalidOperationException>(() => CameraService.ConvertImageArray(null));
            Assert.Throws<InvalidOperationException>(() => CameraService.ConvertImageArray("not an array"));
        }

        [Test]
        public void MapState_covers_the_ascom_camera_states() {
            Assert.That(CameraService.MapState(CameraState.Idle), Is.EqualTo("idle"));
            Assert.That(CameraService.MapState(CameraState.Waiting), Is.EqualTo("exposing"));
            Assert.That(CameraService.MapState(CameraState.Exposing), Is.EqualTo("exposing"));
            Assert.That(CameraService.MapState(CameraState.Reading), Is.EqualTo("downloading"));
            Assert.That(CameraState.Download, Is.Not.EqualTo(CameraState.Reading));
            Assert.That(CameraService.MapState(CameraState.Download), Is.EqualTo("downloading"));
            Assert.That(CameraService.MapState(CameraState.Error), Is.EqualTo("error"));
        }

        [Test]
        public void ResolveFramesDir_falls_back_when_no_store_is_configured() {
            var fallback = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ara-frames-{Guid.NewGuid():N}");
            using var svc = new CameraService(fallbackFramesDir: fallback);
            var dir = svc.ResolveFramesDir();
            Assert.That(dir, Does.StartWith(fallback));
            Assert.That(dir, Does.EndWith("manual"));
        }

        // §65 OSC: ASCOM BayerOffsetX/Y shifts the CFA origin. The base RGGB pattern at the
        // sensor origin, re-anchored to the image (0,0) origin, yields these four patterns.
        [TestCase(0, 0, "RGGB")]
        [TestCase(1, 0, "GRBG")]
        [TestCase(0, 1, "GBRG")]
        [TestCase(1, 1, "BGGR")]
        public void EffectiveBayerPattern_maps_ascom_offsets(int ox, int oy, string expected) {
            Assert.That(CameraService.EffectiveBayerPattern(ox, oy), Is.EqualTo(expected));
        }

        [TestCase(2, 0, "RGGB")]   // even offsets are equivalent to 0
        [TestCase(3, 2, "GRBG")]   // odd-x, even-y ≡ (1,0)
        [TestCase(-1, 0, "GRBG")]  // negative offsets normalize to [0,1]
        [TestCase(-1, -1, "BGGR")]
        public void EffectiveBayerPattern_normalizes_offsets_modulo_two(int ox, int oy, string expected) {
            Assert.That(CameraService.EffectiveBayerPattern(ox, oy), Is.EqualTo(expected));
        }
    

        private static CameraCapabilitiesDto Caps(int minGain, int maxGain, int minOffset, int maxOffset) =>
            new(100, 100, 3.76, false, true, false, minGain, maxGain, minOffset, maxOffset, 1, 1, 1, 1, 0.001, 3600);

        private static ImagingDefaultsDto Defaults(int gain, int offset) =>
            new(ExposureSeconds: 5, Gain: gain, Offset: offset, Bin: 1, FrameKind: "light",
                CoolerTargetC: -10, CoolerRampCPerMin: 2, WarmupAtSessionEnd: true);

        [Test]
        public void AnalysisGainOffset_applies_the_profile_imaging_gain_and_offset() {
            // The 2026-10-03 night: the profile said gain 100 / offset 50, the camera sat at its
            // power-on 0 / 3, and the autofocus probes were shot there.
            Assert.That(CameraService.AnalysisGainOffset(Defaults(100, 50), Caps(0, 600, 0, 255)), Is.EqualTo(((int?)100, (int?)50)));
            Assert.That(CameraService.AnalysisGainOffset(Defaults(0, 0), Caps(0, 600, 0, 255)), Is.EqualTo(((int?)0, (int?)0)),
                "zero is a real gain, not 'unset'");
        }

        [Test]
        public void AnalysisGainOffset_leaves_the_camera_alone_without_a_profile_or_outside_its_range() {
            Assert.That(CameraService.AnalysisGainOffset(null, Caps(0, 600, 0, 255)), Is.EqualTo(((int?)null, (int?)null)));
            Assert.That(CameraService.AnalysisGainOffset(Defaults(900, 50), Caps(0, 600, 0, 255)), Is.EqualTo(((int?)null, (int?)50)),
                "a gain written for another camera is dropped, the offset still applies");
            Assert.That(CameraService.AnalysisGainOffset(Defaults(100, 300), Caps(0, 600, 0, 255)), Is.EqualTo(((int?)100, (int?)null)));
            Assert.That(CameraService.AnalysisGainOffset(Defaults(100, 50), Caps(0, 0, 0, 0)), Is.EqualTo(((int?)100, (int?)50)),
                "a zero range means the bounds read failed: the value goes through, as StartExposureAsync treats it");
            Assert.That(CameraService.AnalysisGainOffset(Defaults(100, 50), null), Is.EqualTo(((int?)100, (int?)50)));
        }
    }
}
