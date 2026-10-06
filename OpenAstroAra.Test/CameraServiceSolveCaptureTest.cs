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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Model;

using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §28 — the plate-solve capture seam (<c>IImagingMediator.CaptureAndPrepareImage</c>) that
    /// <c>CaptureSolver</c> depends on. It used to throw NotSupported unconditionally, which failed
    /// every centering caller on its first exposure.
    /// </summary>
    [TestFixture]
    public class CameraServiceSolveCaptureTest {

        [Test]
        public void Solve_request_maps_the_sequence_exposure_binning_gain_and_filter() {
            var seq = new CaptureSequence(6.5, ImageTypes.SNAPSHOT,
                new FilterInfo { Name = "L", Position = 0 }, new BinningMode(2, 2), exposureCount: 1) {
                Gain = 120,
                Offset = 30,
            };
            var req = CameraService.SolveCaptureRequest(seq);
            Assert.Multiple(() => {
                Assert.That(req.ExposureSec, Is.EqualTo(6.5));
                Assert.That(req.BinX, Is.EqualTo(2));
                Assert.That(req.BinY, Is.EqualTo(2));
                Assert.That(req.Gain, Is.EqualTo(120));
                Assert.That(req.CameraOffset, Is.EqualTo(30));
                Assert.That(req.FilterName, Is.EqualTo("L"));
            });
        }

        [Test]
        public void Unset_gain_offset_and_filter_leave_the_camera_at_its_current_values() {
            // NINA's -1 sentinel means "don't touch"; the daemon's DTO says that with null.
            var seq = new CaptureSequence(2, ImageTypes.SNAPSHOT, null, new BinningMode(1, 1), exposureCount: 1) {
                Gain = -1,
                Offset = -1,
            };
            var req = CameraService.SolveCaptureRequest(seq);
            Assert.Multiple(() => {
                Assert.That(req.Gain, Is.Null);
                Assert.That(req.CameraOffset, Is.Null);
                Assert.That(req.FilterName, Is.Null);
                Assert.That(req.BinX, Is.EqualTo(1));
                Assert.That(req.BinY, Is.EqualTo(1));
            });
        }

        [Test]
        public void Zero_binning_reads_as_one_by_one() {
            var seq = new CaptureSequence(2, ImageTypes.SNAPSHOT, null, new BinningMode(0, 0), exposureCount: 1);
            var req = CameraService.SolveCaptureRequest(seq);
            Assert.That((req.BinX, req.BinY), Is.EqualTo((1, 1)));
        }

        private static CameraCapabilitiesDto Caps(int maxBin, double maxExposure) => new(
            SensorWidth: 1000, SensorHeight: 800, PixelSizeUm: 3.76,
            CanSetTemperature: false, CanAbortExposure: true, CanGetCoolerPower: false,
            MinGain: 0, MaxGain: 0, MinOffset: 0, MaxOffset: 0,
            MinBinX: 1, MaxBinX: maxBin, MinBinY: 1, MaxBinY: maxBin,
            MinExposureSec: 0.001, MaxExposureSec: maxExposure);

        // #1149 — the solve-path guards read the capabilities, which only a real Alpaca connect used to
        // set; WithCapabilitiesForTest seats them so the guards are testable. Without the seam these
        // sequences fall through to "camera is not connected" (an InvalidOperationException) instead.
        [Test]
        public async Task Solve_capture_binning_above_the_cameras_maximum_is_refused_before_any_capture() {
            using var svc = new CameraService(legacyProfile: () => new HeadlessProfileService());
            svc.WithCapabilitiesForTest(Caps(maxBin: 2, maxExposure: 60));
            var seq = new CaptureSequence(2.0, ImageTypes.SNAPSHOT, null, new BinningMode(4, 4), exposureCount: 1);

            var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                svc.CaptureAndPrepareImage(seq, new PrepareImageParameters(detectStars: false), CancellationToken.None, null));
            Assert.That(ex!.Message, Does.Contain("binning").And.Contain("2x2"));
        }

        [Test]
        public async Task Solve_capture_exposure_outside_the_cameras_range_is_refused_before_any_capture() {
            using var svc = new CameraService(legacyProfile: () => new HeadlessProfileService());
            svc.WithCapabilitiesForTest(Caps(maxBin: 4, maxExposure: 60));
            var seq = new CaptureSequence(120.0, ImageTypes.SNAPSHOT, null, new BinningMode(1, 1), exposureCount: 1);

            var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                svc.CaptureAndPrepareImage(seq, new PrepareImageParameters(detectStars: false), CancellationToken.None, null));
            Assert.That(ex!.Message, Does.Contain("exposure"));
        }

        [Test]
        public async Task Capture_without_a_camera_fails_as_not_connected_not_not_supported() {
            // The whole point: a disconnected camera is an ordinary equipment failure the
            // centering loop's attempt policy understands — not a "feature missing" throw.
            using var svc = new CameraService(legacyProfile: () => new HeadlessProfileService());
            var seq = new CaptureSequence(2, ImageTypes.SNAPSHOT, null, new BinningMode(1, 1), exposureCount: 1);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.CaptureAndPrepareImage(seq, new PrepareImageParameters(detectStars: false), CancellationToken.None, null));
            Assert.That(ex!.Message, Does.Contain("not connected"));
        }

        [Test]
        public async Task Capture_without_a_legacy_profile_still_reaches_the_camera_check() {
            // The profile is only read by render paths the solver never calls, so its absence must
            // not be a failure mode of its own.
            using var svc = new CameraService();
            var seq = new CaptureSequence(2, ImageTypes.SNAPSHOT, null, new BinningMode(1, 1), exposureCount: 1);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.CaptureAndPrepareImage(seq, new PrepareImageParameters(detectStars: false), CancellationToken.None, null));
            Assert.That(ex!.Message, Does.Contain("not connected"));
        }

        [Test]
        public void Wrapped_frame_exposes_raw_pixels_metadata_and_an_8bit_render() {
            var pixels = new ushort[4 * 3];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = (ushort)(i * 5000);
            var at = new DateTimeOffset(2026, 9, 22, 4, 0, 0, TimeSpan.Zero);
            var frame = new AnalysisFrame(pixels, 4, 3, at);
            var request = new ExposureRequestDto(ExposureSec: 3.5, Gain: 120, BinX: 2, BinY: 2, CameraOffset: 30);

            var rendered = CameraService.RenderForSolve(frame, request, isBayered: false, cameraName: "Sim", profile: null);

            Assert.Multiple(() => {
                Assert.That(rendered.RawImageData.Properties.Width, Is.EqualTo(4));
                Assert.That(rendered.RawImageData.Properties.Height, Is.EqualTo(3));
                Assert.That(rendered.RawImageData.Properties.BitDepth, Is.EqualTo(16));
                Assert.That(rendered.RawImageData.Properties.IsBayered, Is.False);
                Assert.That(rendered.RawImageData.Data.FlatArray, Is.EqualTo(pixels));
                Assert.That(rendered.RawImageData.MetaData.Image.ExposureTime, Is.EqualTo(3.5));
                Assert.That(rendered.RawImageData.MetaData.Image.ExposureStart, Is.EqualTo(at.UtcDateTime));
                Assert.That(rendered.RawImageData.MetaData.Camera.Gain, Is.EqualTo(120));
                Assert.That(rendered.RawImageData.MetaData.Camera.Offset, Is.EqualTo(30));
                Assert.That(rendered.RawImageData.MetaData.Camera.BinX, Is.EqualTo(2));
                Assert.That(rendered.RawImageData.MetaData.Camera.Name, Is.EqualTo("Sim"));
                // One grayscale byte per pixel — the invariant GetThumbnail asserts on.
                Assert.That(rendered.Image.Length, Is.EqualTo(pixels.Length));
            });
        }

        [Test]
        public void Wrapped_frame_thumbnail_encodes() {
            var frame = new AnalysisFrame(new ushort[64 * 48], 64, 48, DateTimeOffset.UtcNow);
            var request = new ExposureRequestDto(ExposureSec: 1, Gain: null);
            var rendered = CameraService.RenderForSolve(frame, request, isBayered: true, cameraName: null, profile: null);
            var jpeg = rendered.GetThumbnail().GetAwaiter().GetResult();
            Assert.That(jpeg, Is.Not.Empty);
            Assert.That(rendered.RawImageData.Properties.IsBayered, Is.True);
        }

        [Test]
        public async Task Non_positive_exposure_is_rejected_before_touching_the_camera() {
            using var svc = new CameraService(legacyProfile: () => new HeadlessProfileService());
            var seq = new CaptureSequence(0, ImageTypes.SNAPSHOT, null, new BinningMode(1, 1), exposureCount: 1);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                svc.CaptureAndPrepareImage(seq, new PrepareImageParameters(detectStars: false), CancellationToken.None, null));
        }
    }
}
