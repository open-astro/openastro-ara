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

        [Test]
        public void Capture_without_a_camera_fails_as_not_connected_not_not_supported() {
            // The whole point: a disconnected camera is an ordinary equipment failure the
            // centering loop's attempt policy understands — not a "feature missing" throw.
            using var svc = new CameraService(legacyProfile: () => new HeadlessProfileService());
            var seq = new CaptureSequence(2, ImageTypes.SNAPSHOT, null, new BinningMode(1, 1), exposureCount: 1);
            var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.CaptureAndPrepareImage(seq, new PrepareImageParameters(detectStars: false), CancellationToken.None, null));
            Assert.That(ex!.Message, Does.Contain("not connected"));
        }

        [Test]
        public void Capture_without_a_legacy_profile_still_reaches_the_camera_check() {
            // The profile is only read by render paths the solver never calls, so its absence must
            // not be a failure mode of its own.
            using var svc = new CameraService();
            var seq = new CaptureSequence(2, ImageTypes.SNAPSHOT, null, new BinningMode(1, 1), exposureCount: 1);
            var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
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
        public void Non_positive_exposure_is_rejected_before_touching_the_camera() {
            using var svc = new CameraService(legacyProfile: () => new HeadlessProfileService());
            var seq = new CaptureSequence(0, ImageTypes.SNAPSHOT, null, new BinningMode(1, 1), exposureCount: 1);
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                svc.CaptureAndPrepareImage(seq, new PrepareImageParameters(detectStars: false), CancellationToken.None, null));
        }
    }
}
